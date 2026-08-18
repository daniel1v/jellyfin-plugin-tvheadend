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
    public void SomethingOtherThanWhatIsPublishedSatisfiesNoRole()
    {
        // The compatibility roles are published as Matroska because that is what TVHeadend can
        // produce. A profile that emitted MPEG-TS instead would be described to clients as
        // Matroska and decode as nothing, so it is rejected rather than quietly served.
        var observed = Observed("h264", H264RandomAccessKind.Idr) with
        {
            Container = "mpegts,ts",
            IsTransportStream = true,
        };

        Assert.False(JellyfinMediaSourceMapper.SatisfiesContract(PlaybackVariant.Mpeg2H264Compatibility, observed));
        Assert.False(JellyfinMediaSourceMapper.SatisfiesContract(PlaybackVariant.H264IdrNormalization, observed));
    }

    [Fact]
    public void MatroskaWithoutProvenAccessPointsDoesNotSatisfyTheNormalizingRole()
    {
        // Nothing here can show that a Matroska stream carries real IDR frames -- the scanner
        // reads PMT-declared stream types and Matroska has none. Marking the role proven anyway
        // would stand the transitional encoder down on evidence that was never gathered.
        var observed = Observed("h264", H264RandomAccessKind.Unknown);

        Assert.True(JellyfinMediaSourceMapper.SatisfiesContract(PlaybackVariant.Mpeg2H264Compatibility, observed));
        Assert.False(JellyfinMediaSourceMapper.SatisfiesContract(PlaybackVariant.H264IdrNormalization, observed));
    }

    [Fact]
    public void AContainerWithoutH264SatisfiesNothing()
    {
        var observed = Observed("mpeg2video", H264RandomAccessKind.NotApplicable);

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
            native,
            observedVariant: null,
            itemId: null,
            describeStreams: true);

        var video = System.Linq.Enumerable.First(source.MediaStreams, stream => stream.Type == MediaStreamType.Video);

        Assert.Equal("h264", video.Codec);
        Assert.Equal(720, video.Width);
        Assert.Equal(576, video.Height);
        Assert.Null(video.Profile);
        Assert.Null(video.Level);
        Assert.Null(video.BitRate);
    }


    [Fact]
    public void AnUnprovenCompatibilityVariantClaimsNoAudioFactsAProfileCouldChange()
    {
        // A compatibility profile may copy the broadcast audio or re-encode it, and which it
        // does is not knowable before one has been produced. Codec, channel count, layout and
        // sample rate are exactly the facts a client decides on, so stating the broadcast's
        // values would be stating something that may not be true of this output.
        var native = Observed("mpeg2video", H264RandomAccessKind.NotApplicable) with
        {
            Streams =
            [
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "mpeg2video", Width = 720, Height = 576 },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "mp2",
                    Language = "deu",
                    Title = "Deutsch",
                    Channels = 2,
                    ChannelLayout = "stereo",
                    SampleRate = 48000,
                },
            ],
        };

        var source = JellyfinMediaSourceMapper.CreatePending(
            "42",
            new VariantOffer(PlaybackVariant.Mpeg2H264Compatibility, true),
            native,
            observedVariant: null,
            itemId: null,
            describeStreams: true);

        var audio = System.Linq.Enumerable.Single(source.MediaStreams, stream => stream.Type == MediaStreamType.Audio);

        // The track exists and is still recognisable to a viewer choosing between languages.
        Assert.Equal("deu", audio.Language);
        Assert.Equal("Deutsch", audio.Title);

        Assert.Null(audio.Codec);
        Assert.Null(audio.Channels);
        Assert.Null(audio.ChannelLayout);
        Assert.Null(audio.SampleRate);
    }
    private static ChannelMediaDescriptor Observed(string codec, H264RandomAccessKind randomAccess)
        => new()
        {
            ChannelId = "42",
            VariantRole = "Mpeg2H264Compatibility",
            Container = "matroska,webm",
            RandomAccess = randomAccess,
            IsTransportStream = false,
            Streams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = codec }],
        };
}
