using System;
using System.Linq;
using Tvheadend.Htsp;
using Tvheadend.Htsp.Protocol;
using Xunit;

namespace TVHeadEnd.Tests.Htsp;

/// <summary>
/// The HTSMSG binary encoding, which everything else in the protocol rests on.
/// </summary>
public class HtspCodecTests
{
    [Fact]
    public void AMessageSurvivesBeingEncodedAndDecoded()
    {
        var original = HtspMessage.Create("subscribe")
            .Set("subscriptionId", 42)
            .Set("channelId", 1234)
            .Set("90khz", 1)
            .Set("profile", "pass");

        var decoded = RoundTrip(original);

        Assert.Equal("subscribe", decoded.Method);
        Assert.Equal(42, decoded.GetInt32("subscriptionId"));
        Assert.Equal(1234, decoded.GetInt32("channelId"));
        Assert.True(decoded.GetBoolean("90khz"));
        Assert.Equal("pass", decoded.GetString("profile"));
    }

    [Fact]
    public void ANestedMapKeepsItsFields()
    {
        // subscriptionStart carries sourceinfo as a nested map, and the plugin decides which
        // service it is looking at from what is in it.
        var sourceInfo = new HtspMessage()
            .Set("mux_uuid", "abcdef")
            .Set("service", "Das Erste HD");

        var decoded = RoundTrip(HtspMessage.Create("subscriptionStart").Set("sourceinfo", sourceInfo));

        var nested = decoded.GetMap("sourceinfo");
        Assert.NotNull(nested);
        Assert.Equal("abcdef", nested!.GetString("mux_uuid"));
        Assert.Equal("Das Erste HD", nested.GetString("service"));
    }

    [Fact]
    public void AListOfMapsKeepsItsOrder()
    {
        // The streams list is ordered, and its order is part of what it means: it is what the
        // plugin walks to match elementary streams against the delivered program map.
        var decoded = RoundTrip(HtspMessage.Create("subscriptionStart").Set(
            "streams",
            [
                new HtspMessage().Set("index", 1).Set("type", "H264"),
                new HtspMessage().Set("index", 2).Set("type", "MPEG2AUDIO"),
                new HtspMessage().Set("index", 3).Set("type", "DVBSUB"),
            ]));

        var list = decoded.GetMapList("streams");
        Assert.Equal(3, list.Count);
        Assert.Equal([1, 2, 3], list.Select(entry => entry.GetInt32("index")));
        Assert.Equal(["H264", "MPEG2AUDIO", "DVBSUB"], list.Select(entry => entry.GetString("type")));
    }

    [Fact]
    public void AListOfIntegersKeepsItsOrder()
    {
        // subscriptionFilterStream sends every index to disable as one list.
        var decoded = RoundTrip(HtspMessage.Create("subscriptionFilterStream")
            .Set("disable", Enumerable.Range(0, 512).Select(value => (long)value)));

        var disabled = decoded.GetInt64List("disable");
        Assert.Equal(512, disabled.Count);
        Assert.Equal(0, disabled[0]);
        Assert.Equal(511, disabled[511]);
    }

    [Fact]
    public void AListOfStringsKeepsItsOrder()
    {
        // The shape TVHeadend's string_list_serialize writes: unnamed strings in a list. Its
        // categories and keywords arrive this way.
        var decoded = RoundTrip(HtspMessage.Create("eventAdd").Set("category", ["Krimi", "Drama"]));

        Assert.Equal(["Krimi", "Drama"], decoded.GetStringList("category"));
    }

    [Fact]
    public void AStringListThatIsNotThereIsEmpty()
    {
        // Most events carry no categories at all -- measured on the live server, none of 5174 did.
        // A missing field is an ordinary answer here and never an error.
        Assert.Empty(HtspMessage.Create("eventAdd").GetStringList("category"));
    }

    [Fact]
    public void AFieldThatIsNotAListIsNotReadAsOne()
    {
        Assert.Empty(HtspMessage.Create("eventAdd").Set("category", "Krimi").GetStringList("category"));
        Assert.Empty(HtspMessage.Create("eventAdd").Set("category", 42).GetStringList("category"));
    }

    [Fact]
    public void ElementsOfTheWrongTypeAreSkippedRatherThanFatal()
    {
        // A list is not typed on the wire, so nothing guarantees the elements are all strings.
        // Reading what is readable is the same rule the other list getters follow.
        var mixed = RoundTrip(HtspMessage.Create("eventAdd").Set(
            "mixed",
            [new HtspMessage().Set("a", 1)]));

        Assert.Empty(mixed.GetStringList("mixed"));
        Assert.Single(mixed.GetMapList("mixed"));
    }

    [Fact]
    public void ChannelTagsAreIntegersAndAreReadAsIntegers()
    {
        // Not every list TVHeadend sends is strings. A channel's tags are the numbers of the tags
        // it is in -- measured on the live server -- and reading them as strings would silently
        // put every channel in no groups at all.
        var decoded = RoundTrip(HtspMessage.Create("channelAdd").Set("tags", [1L, 2L]));

        Assert.Equal([1, 2], decoded.GetInt64List("tags"));
        Assert.Empty(decoded.GetStringList("tags"));
    }

    [Fact]
    public void BinaryFieldsSurviveUntouched()
    {
        // The authentication challenge is binary, and a digest taken over a mangled one fails
        // in a way that looks like a wrong password.
        var challenge = new byte[32];
        for (var index = 0; index < challenge.Length; index++)
        {
            challenge[index] = (byte)(index * 7);
        }

        var decoded = RoundTrip(HtspMessage.Create("hello").Set("challenge", challenge));

        Assert.Equal(challenge, decoded.GetBinary("challenge"));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(255L)]
    [InlineData(256L)]
    [InlineData(65536L)]
    [InlineData(0x010001L)]
    [InlineData(1786889738L)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    [InlineData(-1786889738L)]
    [InlineData(long.MinValue)]
    public void IntegersSurviveTheirVariableLengthEncoding(long value)
    {
        // Encoded little-endian with trailing zero bytes dropped, so zero occupies no bytes at
        // all and a negative number occupies eight. 0x010001 is the case an earlier
        // implementation got wrong: it dropped the interior zero byte and decoded as 0x0101.
        var decoded = RoundTrip(new HtspMessage().Set("value", value));

        Assert.Equal(value, decoded.GetInt64("value"));
    }

    [Fact]
    public void AFrameLongerThanTheLimitIsRefusedBeforeAnythingIsAllocated()
    {
        // The length prefix is four bytes of whatever arrived. Honouring it would let a damaged
        // stream, or a peer that is not TVHeadend, name an allocation of any size it liked.
        byte[] header = [0x7F, 0xFF, 0xFF, 0xFF];

        Assert.Throws<HtspProtocolException>(() => HtspCodec.ReadBodyLength(header));
    }

    [Fact]
    public void AFieldClaimingMoreDataThanTheMessageHoldsIsRefused()
    {
        // type=string, namelen=1, datalen=0x7FFFFFFF, one byte of name, no data.
        byte[] body = [0x03, 0x01, 0x7F, 0xFF, 0xFF, 0xFF, (byte)'a'];

        Assert.Throws<HtspProtocolException>(() => HtspCodec.Decode(body));
    }

    [Fact]
    public void ATruncatedFieldHeaderIsRefused()
    {
        byte[] body = [0x03, 0x01, 0x00];

        Assert.Throws<HtspProtocolException>(() => HtspCodec.Decode(body));
    }

    [Fact]
    public void AnUnknownFieldTypeIsRefusedRatherThanSkipped()
    {
        // Skipping it would leave the reader guessing at where the next field starts, and the
        // framing has no resynchronisation point.
        byte[] body = [0x63, 0x01, 0x00, 0x00, 0x00, 0x00, (byte)'a'];

        Assert.Throws<HtspProtocolException>(() => HtspCodec.Decode(body));
    }

    [Fact]
    public void AnEmptyBodyDecodesToAnEmptyMessage()
    {
        var decoded = HtspCodec.Decode([]);

        Assert.Equal(0, decoded.Count);
    }

    [Fact]
    public void TheFrameHeaderStatesTheBodyLength()
    {
        var encoded = HtspCodec.Encode(HtspMessage.Create("hello"));

        var length = HtspCodec.ReadBodyLength(encoded);
        Assert.Equal(encoded.Length - HtspCodec.FrameHeaderLength, length);
    }

    private static HtspMessage RoundTrip(HtspMessage message)
    {
        var encoded = HtspCodec.Encode(message);
        return HtspCodec.Decode(encoded.AsSpan(HtspCodec.FrameHeaderLength));
    }
}
