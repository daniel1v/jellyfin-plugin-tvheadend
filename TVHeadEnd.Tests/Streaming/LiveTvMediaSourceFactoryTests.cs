using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

public class LiveTvMediaSourceFactoryTests
{
    [Fact]
    public void CreatePendingReturnsTicketFreeServerMediatedSource()
    {
        const string internalChannelId = "f586be12201f194ac90fdc57268b0d2e";

        var source = LiveTvMediaSourceFactory.CreatePending(internalChannelId);

        Assert.Equal(internalChannelId, source.Id);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.Null(source.Path);
        Assert.True(source.RequiresOpening);
        Assert.False(source.RequiresClosing);
        Assert.False(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.False(source.SupportsProbing);
        Assert.Empty(source.MediaStreams);
    }

    [Fact]
    public void CreateOpenedReturnsProbeableServerMediatedSource()
    {
        const string internalChannelId = "f586be12201f194ac90fdc57268b0d2e";
        const string streamUrl = "http://tvheadend.invalid/stream/channel/1?ticket=redacted";

        var source = LiveTvMediaSourceFactory.CreateOpened(internalChannelId, streamUrl);

        Assert.Equal(internalChannelId, source.Id);
        Assert.Equal(streamUrl, source.Path);
        Assert.False(source.RequiresOpening);
        Assert.True(source.RequiresClosing);
        Assert.False(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsProbing);
        Assert.Empty(source.MediaStreams);
    }

    [Fact]
    public void PreferCompatibleAudioTrackSelectsDolbyOverMpegAudio()
    {
        var video = new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Index = 0 };
        var mp2 = new MediaStream { Type = MediaStreamType.Audio, Codec = "mp2", Index = 1 };
        var ac3 = new MediaStream { Type = MediaStreamType.Audio, Codec = "ac3", Index = 5 };
        var data = new MediaStream { Type = MediaStreamType.Data, Codec = "epg", Index = 7 };
        var source = new MediaSourceInfo { MediaStreams = [video, mp2, ac3, data] };

        LiveTvMediaSourceFactory.PreferCompatibleAudioTrack(source);

        Assert.Equal(5, source.DefaultAudioStreamIndex);
        Assert.True(ac3.IsDefault);
        Assert.False(mp2.IsDefault);
    }

    [Fact]
    public void PreferCompatibleAudioTrackLeavesStreamOrderUntouched()
    {
        // Jellyfin's EncodingHelper.GetMapArgs builds "-map 0:N" from the position in this
        // list, so reordering it would make FFmpeg copy a different track than the one the
        // manifest describes.
        var source = new MediaSourceInfo
        {
            MediaStreams =
            [
                new MediaStream { Type = MediaStreamType.Data, Codec = "epg", Index = 0 },
                new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Index = 1 },
                new MediaStream { Type = MediaStreamType.Audio, Codec = "mp2", Index = 2 },
                new MediaStream { Type = MediaStreamType.Audio, Codec = "ac3", Index = 6 },
            ],
        };

        LiveTvMediaSourceFactory.PreferCompatibleAudioTrack(source);

        Assert.Equal([0, 1, 2, 6], source.MediaStreams.Select(stream => stream.Index));
        Assert.Equal(6, source.DefaultAudioStreamIndex);
    }

    [Fact]
    public void PreferCompatibleAudioTrackOverridesAnUndecodableBroadcastDefault()
    {
        var mp2 = new MediaStream { Type = MediaStreamType.Audio, Codec = "mp2", Index = 1, IsDefault = true };
        var ac3 = new MediaStream { Type = MediaStreamType.Audio, Codec = "ac3", Index = 2 };
        var source = new MediaSourceInfo { MediaStreams = [mp2, ac3] };

        LiveTvMediaSourceFactory.PreferCompatibleAudioTrack(source);

        Assert.False(mp2.IsDefault);
        Assert.True(ac3.IsDefault);
        Assert.Equal(2, source.DefaultAudioStreamIndex);
    }

    [Fact]
    public void PreferCompatibleAudioTrackFallsBackToTheOnlyTrackAvailable()
    {
        var mp2 = new MediaStream { Type = MediaStreamType.Audio, Codec = "mp2", Index = 3 };
        var source = new MediaSourceInfo { MediaStreams = [mp2] };

        LiveTvMediaSourceFactory.PreferCompatibleAudioTrack(source);

        Assert.True(mp2.IsDefault);
        Assert.Equal(3, source.DefaultAudioStreamIndex);
    }

    [Fact]
    public void CreatedSourcesNameBothSpellingsOfTheTransportStreamContainer()
    {
        // Client profiles are split over "mpegts" and "ts", and ContainerHelper compares
        // them for exact equality.
        var pending = LiveTvMediaSourceFactory.CreatePending("f586be12201f194ac90fdc57268b0d2e");
        var opened = LiveTvMediaSourceFactory.CreateOpened("f586be12201f194ac90fdc57268b0d2e", "http://tvheadend.invalid/stream");

        Assert.Equal("mpegts,ts", pending.Container);
        Assert.Equal("mpegts,ts", opened.Container);
    }
}
