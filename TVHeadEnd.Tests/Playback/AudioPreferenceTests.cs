using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using TVHeadEnd.Playback;
using Xunit;

namespace TVHeadEnd.Tests.Playback;

/// <summary>
/// German broadcasts typically carry MPEG audio first and a Dolby track after it. Without a
/// preference Jellyfin selects the first, finds the client cannot decode it, and transcodes a
/// stream whose video it was perfectly happy to pass through -- measured as
/// <c>-codec:v:0 copy -codec:a:0 libmp3lame</c>.
/// </summary>
public class AudioPreferenceTests
{
    [Fact]
    public void ADolbyTrackIsPreferredOverMpegAudio()
    {
        var source = SourceWith(("mp2", 1), ("mp2", 2), ("ac3", 3));

        JellyfinMediaSourceMapper.PreferWidelyDecodableAudio(source);

        Assert.Equal(3, source.DefaultAudioStreamIndex);
        Assert.True(source.MediaStreams.Single(stream => stream.Index == 3).IsDefault);
    }

    [Fact]
    public void AacWinsWhenItIsThere()
    {
        var source = SourceWith(("ac3", 1), ("aac", 2));

        JellyfinMediaSourceMapper.PreferWidelyDecodableAudio(source);

        Assert.Equal(2, source.DefaultAudioStreamIndex);
    }

    [Fact]
    public void TheStreamOrderIsNeverChanged()
    {
        // Jellyfin addresses the track to copy by its position in this list, so reordering makes
        // -map point at something other than the track the manifest describes.
        var source = SourceWith(("mp2", 1), ("ac3", 2));
        var before = source.MediaStreams.Select(stream => stream.Index).ToArray();

        JellyfinMediaSourceMapper.PreferWidelyDecodableAudio(source);

        Assert.Equal(before, source.MediaStreams.Select(stream => stream.Index).ToArray());
    }

    [Fact]
    public void EveryTrackIsKept()
    {
        var source = SourceWith(("mp2", 1), ("mp2", 2), ("mp2", 3), ("ac3", 4));

        JellyfinMediaSourceMapper.PreferWidelyDecodableAudio(source);

        Assert.Equal(4, source.MediaStreams.Count(stream => stream.Type == MediaStreamType.Audio));
        Assert.Single(source.MediaStreams.Where(stream => stream.Type == MediaStreamType.Audio && stream.IsDefault));
    }

    [Fact]
    public void WithOnlyMpegAudioTheFirstTrackIsUsed()
    {
        var source = SourceWith(("mp2", 1), ("mp2", 2));

        JellyfinMediaSourceMapper.PreferWidelyDecodableAudio(source);

        Assert.Equal(1, source.DefaultAudioStreamIndex);
    }

    [Fact]
    public void ASourceWithoutAudioIsLeftAlone()
    {
        var source = new MediaSourceInfo
        {
            MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" }],
        };

        JellyfinMediaSourceMapper.PreferWidelyDecodableAudio(source);

        Assert.Null(source.DefaultAudioStreamIndex);
    }

    private static MediaSourceInfo SourceWith(params (string Codec, int Index)[] audio)
        => new()
        {
            MediaStreams =
            [
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                .. audio.Select(track => new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = track.Index,
                    Codec = track.Codec,
                }),
            ],
        };
}
