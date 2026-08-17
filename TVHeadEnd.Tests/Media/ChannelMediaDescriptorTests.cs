using MediaBrowser.Model.Entities;
using TVHeadEnd.Media;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Media;

public class ChannelMediaDescriptorTests
{
    [Fact]
    public void ADescriptorFromAnOlderAnalysisIsNotCurrent()
    {
        var descriptor = Usable() with { SchemaVersion = ChannelMediaDescriptor.CurrentSchemaVersion - 1 };

        Assert.False(descriptor.IsCurrentFor("pass"));
    }

    [Fact]
    public void ChangingTheNativeProfileInvalidatesADescriptor()
    {
        // A different profile can change the container and the elementary streams, so what was
        // observed through one says nothing about the other.
        var descriptor = Usable() with { NativeProfile = "pass" };

        Assert.True(descriptor.IsCurrentFor("pass"));
        Assert.False(descriptor.IsCurrentFor("webtv-h264"));
    }

    [Fact]
    public void ADescriptorWithoutVideoIsNotUsable()
    {
        // Jellyfin dereferences the video stream while preparing playback and throws before any
        // fallback could take effect.
        var descriptor = Usable() with
        {
            Streams = [new MediaStream { Type = MediaStreamType.Audio, Index = 0, Codec = "ac3" }],
        };

        Assert.False(descriptor.IsUsable);
        Assert.False(descriptor.IsCurrentFor("pass"));
    }

    [Fact]
    public void AChangedProgramLayoutIsNoticed()
    {
        var descriptor = Usable() with { ProgramSignature = "1b:13ed,03:13ee" };

        Assert.True(descriptor.MatchesProgram("1b:13ed,03:13ee"));
        Assert.False(descriptor.MatchesProgram("1b:13ed,03:13ee,03:13ef"));
        Assert.False(descriptor.MatchesProgram(null));
    }

    [Fact]
    public void Mpeg2IsRecognisedFromTheStreamTypeOrTheCodec()
    {
        Assert.True((Usable() with { VideoStreamType = 0x02 }).IsMpeg2Video);
        Assert.True(Usable("mpeg2video").IsMpeg2Video);
        Assert.False(Usable().IsMpeg2Video);
    }

    [Fact]
    public void EveryVariantIsStoredUnderItsOwnKey()
    {
        Assert.NotEqual(
            ChannelMediaDescriptor.Key("42", null),
            ChannelMediaDescriptor.Key("42", "Mpeg2H264Compatibility"));
    }

    private static ChannelMediaDescriptor Usable(string codec = "h264")
        => new()
        {
            ChannelId = "42",
            NativeProfile = "pass",
            Container = "mpegts,ts",
            VideoStreamType = 0x1B,
            RandomAccess = H264RandomAccessKind.Idr,
            IsTransportStream = true,
            Streams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = codec }],
        };
}
