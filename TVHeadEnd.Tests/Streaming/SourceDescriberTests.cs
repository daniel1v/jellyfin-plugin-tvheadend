using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

public class SourceDescriberTests
{
    [Fact]
    public void TheAudioTrackMostClientsCanDecodeIsPreferredOverMpegAudio()
    {
        var video = new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Index = 0 };
        var mp2 = new MediaStream { Type = MediaStreamType.Audio, Codec = "mp2", Index = 1 };
        var ac3 = new MediaStream { Type = MediaStreamType.Audio, Codec = "ac3", Index = 5 };
        var data = new MediaStream { Type = MediaStreamType.Data, Codec = "epg", Index = 7 };
        var source = new MediaSourceInfo { MediaStreams = [video, mp2, ac3, data] };

        SourceDescriber.PreferCompatibleAudioTrack(source);

        Assert.Equal(5, source.DefaultAudioStreamIndex);
        Assert.True(ac3.IsDefault);
        Assert.False(mp2.IsDefault);
    }

    [Fact]
    public void StreamOrderIsLeftUntouched()
    {
        // Jellyfin's EncodingHelper.GetMapArgs builds "-map 0:N" from the position in this list,
        // so reordering it would make FFmpeg copy a different track than the one the manifest
        // describes. This holds for recordings as much as for live TV, which is why the rule
        // lives with the description rather than with either path.
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

        SourceDescriber.PreferCompatibleAudioTrack(source);

        Assert.Equal([0, 1, 2, 6], source.MediaStreams.Select(stream => stream.Index));
        Assert.Equal(6, source.DefaultAudioStreamIndex);
    }

    [Fact]
    public void AnUndecodableBroadcastDefaultIsOverridden()
    {
        var mp2 = new MediaStream { Type = MediaStreamType.Audio, Codec = "mp2", Index = 1, IsDefault = true };
        var ac3 = new MediaStream { Type = MediaStreamType.Audio, Codec = "ac3", Index = 2 };
        var source = new MediaSourceInfo { MediaStreams = [mp2, ac3] };

        SourceDescriber.PreferCompatibleAudioTrack(source);

        Assert.False(mp2.IsDefault);
        Assert.True(ac3.IsDefault);
        Assert.Equal(2, source.DefaultAudioStreamIndex);
    }

    [Fact]
    public void TheOnlyTrackAvailableIsUsedEvenIfNoClientFavoursIt()
    {
        var mp2 = new MediaStream { Type = MediaStreamType.Audio, Codec = "mp2", Index = 3 };
        var source = new MediaSourceInfo { MediaStreams = [mp2] };

        SourceDescriber.PreferCompatibleAudioTrack(source);

        Assert.True(mp2.IsDefault);
        Assert.Equal(3, source.DefaultAudioStreamIndex);
    }

    [Fact]
    public void ASourceWithoutAudioIsLeftAlone()
    {
        var source = new MediaSourceInfo
        {
            MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Index = 0 }],
        };

        SourceDescriber.PreferCompatibleAudioTrack(source);

        Assert.Null(source.DefaultAudioStreamIndex);
    }
}
