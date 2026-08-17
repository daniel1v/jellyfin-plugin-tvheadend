using MediaBrowser.Model.Entities;
using TVHeadEnd.Media;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Playback;

/// <summary>
/// A compatibility role is only worth offering if the TVHeadend profile behind it delivers what
/// the role promises. Rather than trust the configuration, the output is checked once.
/// </summary>
public class CompatibilityContractTests
{
    [Fact]
    public void AProfileThatCopiesTheVideoDoesNotSatisfyTheMpeg2Role()
    {
        var observed = Observed("mpeg2video", H264RandomAccessKind.NotApplicable);

        Assert.False(JellyfinMediaSourceMapper.SatisfiesContract(PlaybackVariant.Mpeg2H264Compatibility, observed));
    }

    [Fact]
    public void AProfileThatProducesH264SatisfiesTheMpeg2Role()
    {
        var observed = Observed("h264", H264RandomAccessKind.Idr);

        Assert.True(JellyfinMediaSourceMapper.SatisfiesContract(PlaybackVariant.Mpeg2H264Compatibility, observed));
    }

    [Fact]
    public void NormalizationWithoutRealIdrFramesDoesNotSatisfyItsRole()
    {
        // The whole point is that a decoder can cold-start on it. An output that re-wraps the
        // same recovery-point video is no better than the broadcast.
        var observed = Observed("h264", H264RandomAccessKind.RecoveryOpenGop);

        Assert.False(JellyfinMediaSourceMapper.SatisfiesContract(PlaybackVariant.H264IdrNormalization, observed));
    }

    [Fact]
    public void NormalizationWithRealIdrFramesSatisfiesItsRole()
    {
        var observed = Observed("h264", H264RandomAccessKind.Idr);

        Assert.True(JellyfinMediaSourceMapper.SatisfiesContract(PlaybackVariant.H264IdrNormalization, observed));
    }

    [Fact]
    public void MatroskaSatisfiesBothRolesWhenTheVideoIsRight()
    {
        // The container is the means, not the end. Measured on a real installation, the Matroska
        // output runs at real time where the transport stream one collapsed to 7 %, and it keeps
        // more of the original audio tracks. What the roles are for is video a client can decode.
        var observed = Observed("h264", H264RandomAccessKind.NotApplicable) with
        {
            Container = "matroska,webm",
            IsTransportStream = false,
        };

        Assert.True(JellyfinMediaSourceMapper.SatisfiesContract(PlaybackVariant.Mpeg2H264Compatibility, observed));
        Assert.True(JellyfinMediaSourceMapper.SatisfiesContract(PlaybackVariant.H264IdrNormalization, observed));
    }

    [Fact]
    public void AContainerWithoutH264SatisfiesNothing()
    {
        var observed = Observed("mpeg2video", H264RandomAccessKind.NotApplicable) with
        {
            Container = "matroska,webm",
            IsTransportStream = false,
        };

        Assert.False(JellyfinMediaSourceMapper.SatisfiesContract(PlaybackVariant.Mpeg2H264Compatibility, observed));
    }

    [Fact]
    public void AnUnobservedOutputSatisfiesNothing()
    {
        Assert.False(JellyfinMediaSourceMapper.SatisfiesContract(PlaybackVariant.Mpeg2H264Compatibility, null));
    }

    [Fact]
    public void TheNativeRoleIsAlwaysSatisfied()
    {
        // There is no contract to break: it is whatever the broadcast is.
        Assert.True(JellyfinMediaSourceMapper.SatisfiesContract(PlaybackVariant.Native, null));
    }

    [Fact]
    public void AnUnprovenCompatibilityVariantClaimsOnlyWhatItsRoleGuarantees()
    {
        // Profile, level and bitrate are not part of any role contract, and a client makes
        // decisions on them. Leaving them unset is honest; guessing them is not.
        var native = Observed("mpeg2video", H264RandomAccessKind.NotApplicable) with
        {
            Streams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "mpeg2video",
                    Width = 720,
                    Height = 576,
                    Profile = "Main",
                    Level = 8,
                    BitRate = 4_710_991,
                    IsInterlaced = true,
                },
            ],
        };

        var source = JellyfinMediaSourceMapper.CreatePending(
            "42",
            new VariantOffer(PlaybackVariant.Mpeg2H264Compatibility, true),
            native);

        var video = System.Linq.Enumerable.First(source.MediaStreams, stream => stream.Type == MediaStreamType.Video);

        Assert.Equal("h264", video.Codec);
        Assert.Equal(720, video.Width);
        Assert.Equal(576, video.Height);
        Assert.Null(video.Profile);
        Assert.Null(video.Level);
        Assert.Null(video.BitRate);
    }

    private static ChannelMediaDescriptor Observed(string codec, H264RandomAccessKind randomAccess)
        => new()
        {
            ChannelId = "42",
            VariantRole = "Mpeg2H264Compatibility",
            Container = "mpegts,ts",
            RandomAccess = randomAccess,
            IsTransportStream = true,
            Streams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = codec }],
        };
}
