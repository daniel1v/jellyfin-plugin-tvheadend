using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// Finding IDR pictures in an H.264 elementary stream.
/// </summary>
/// <remarks>
/// The whole of the plugin's bitstream analysis. It decides whether one client gets the broadcast
/// as it is or gets it re-encoded, so both of its answers matter and neither may depend on where
/// a transport stream packet happened to end.
/// </remarks>
public class H264IdrScannerTests
{
    [Fact]
    public void AnIdrNalUnitIsFound()
    {
        var scanner = new H264IdrScanner();

        Assert.True(scanner.Scan([0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00, 0x01, 0x65, 0x88]));
        Assert.True(scanner.HasSeenIdr);
    }

    [Fact]
    public void AFourByteStartCodeIsFoundToo()
    {
        // Both spellings are legal and encoders emit both.
        Assert.True(new H264IdrScanner().Scan([0x00, 0x00, 0x00, 0x01, 0x65]));
    }

    [Fact]
    public void APictureOfOtherNalUnitsIsNot()
    {
        // An access unit delimiter, a recovery point message and a non-IDR slice: a valid DVB
        // access point, and the thing that will not cold-start on some decoders.
        var scanner = new H264IdrScanner();

        Assert.False(scanner.Scan([0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00, 0x01, 0x06, 0x06, 0x00, 0x00, 0x01, 0x61]));
        Assert.False(scanner.HasSeenIdr);
    }

    [Fact]
    public void AStartCodeSplitAcrossTwoPacketsIsStillFound()
    {
        // The reason this keeps three bytes of state. A 188 byte packet boundary falls wherever it
        // falls, and an IDR missed because of one would re-encode a channel that needs nothing.
        var scanner = new H264IdrScanner();

        Assert.False(scanner.Scan([0x41, 0x00, 0x00]));
        Assert.True(scanner.Scan([0x01, 0x65, 0x88]));
    }

    [Fact]
    public void OnlyTheLowFiveBitsOfTheHeaderNameTheType()
    {
        // The top three carry the reference indicator, which every real IDR sets and which says
        // nothing about the type. 0x25 and 0x65 are both type five; 0x61 is a non-IDR slice.
        Assert.True(new H264IdrScanner().Scan([0x00, 0x00, 0x01, 0x25]));
        Assert.True(new H264IdrScanner().Scan([0x00, 0x00, 0x01, 0x65]));
        Assert.False(new H264IdrScanner().Scan([0x00, 0x00, 0x01, 0x61]));
    }

    [Fact]
    public void AResetForgetsBothTheFindingAndTheBytesBeforeIt()
    {
        var scanner = new H264IdrScanner();
        scanner.Scan([0x00, 0x00, 0x01, 0x65]);

        scanner.Reset();

        Assert.False(scanner.HasSeenIdr);

        // And the carried bytes went with it, so the next picture cannot inherit half a start code.
        Assert.False(scanner.Scan([0x01, 0x65]));
    }
}
