using System;
using System.Linq;
using System.Text;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

public class SourceContainerTests
{
    private const int PacketLength = 188;

    [Fact]
    public void ATransportStreamIsRecognised()
    {
        Assert.True(SourceContainer.IsTransportStream(TransportStream(packets: 8, startOffset: 0)));
    }

    [Fact]
    public void ATransportStreamIsRecognisedWhenTheFirstPacketIsIncomplete()
    {
        // A stream joined mid-flight does not begin on a packet boundary.
        Assert.True(SourceContainer.IsTransportStream(TransportStream(packets: 8, startOffset: 61)));
    }

    [Fact]
    public void MatroskaIsNotMistakenForATransportStream()
    {
        // What a TVHeadend server running one of the WebTV profiles delivers. Conditioning it as
        // a transport stream would silently mangle it.
        var matroska = new byte[4096];
        matroska[0] = 0x1A;
        matroska[1] = 0x45;
        matroska[2] = 0xDF;
        matroska[3] = 0xA3;
        FillPseudoRandom(matroska.AsSpan(4));

        Assert.False(SourceContainer.IsTransportStream(matroska));
    }

    [Fact]
    public void TextThatHappensToContainSyncBytesIsNotATransportStream()
    {
        // 0x47 is the letter 'G'. A single occurrence, or several at the wrong spacing, must not
        // be enough -- otherwise any payload could pass for a transport stream.
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("GOOD GRIEF, GEORGE! ", 200)));

        Assert.False(SourceContainer.IsTransportStream(text));
    }

    [Fact]
    public void ASingleSyncByteIsNotEnough()
    {
        var almost = new byte[4096];
        FillPseudoRandom(almost);
        almost[0] = 0x47;
        almost[PacketLength] = 0x00;

        Assert.False(SourceContainer.IsTransportStream(almost));
    }

    [Fact]
    public void TooLittleDataDecidesNothingRatherThanGuessing()
    {
        Assert.False(SourceContainer.IsTransportStream(new byte[] { 0x47, 0x40, 0x00 }));
    }

    [Theory]
    [InlineData("mpegts")]
    [InlineData("ts")]
    [InlineData("TS")]
    public void TheTransportStreamIsReportedUnderBothItsSpellings(string probed)
    {
        // FFprobe says one, Android's device profile says the other, and Jellyfin compares them
        // as plain strings.
        Assert.Equal("mpegts", SourceContainer.Describe(probed, "mpegts"));
    }

    [Fact]
    public void AnyOtherContainerIsReportedAsFound()
    {
        Assert.Equal("matroska,webm", SourceContainer.Describe("matroska,webm", "mpegts"));
    }

    [Fact]
    public void AnAnalysisThatFoundNothingLeavesTheAssumptionInPlace()
    {
        Assert.Equal("mpegts", SourceContainer.Describe(null, "mpegts"));
        Assert.Equal("mpegts", SourceContainer.Describe(string.Empty, "mpegts"));
    }

    private static byte[] TransportStream(int packets, int startOffset)
    {
        var stream = new byte[startOffset + (packets * PacketLength)];
        FillPseudoRandom(stream);
        for (var packet = 0; packet < packets; packet++)
        {
            stream[startOffset + (packet * PacketLength)] = 0x47;
        }

        // Whatever preceded the first whole packet must not itself look like a boundary.
        for (var i = 0; i < startOffset; i++)
        {
            if (stream[i] == 0x47)
            {
                stream[i] = 0x11;
            }
        }

        return stream;
    }

    private static void FillPseudoRandom(Span<byte> destination)
    {
        // Deterministic, and never 0x47 so only the bytes the test places are sync bytes.
        for (var i = 0; i < destination.Length; i++)
        {
            var value = (byte)((i * 31) % 251);
            destination[i] = value == 0x47 ? (byte)0x48 : value;
        }
    }
}
