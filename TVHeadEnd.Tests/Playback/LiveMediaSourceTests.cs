using System;
using System.Linq;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Extensions;
using MediaBrowser.Model.MediaInfo;
using TVHeadEnd.Playback;
using Xunit;

namespace TVHeadEnd.Tests.Playback;

/// <summary>
/// The one media source a live channel offers, and what it may claim.
/// </summary>
public class LiveMediaSourceTests
{
    private const string ItemId = "8f14e45fceea167a5a36dedd4bea2543";

    [Fact]
    public void APendingSourceCarriesTheChannelsOwnItemIdentifier()
    {
        // Jellyfin's convention for the single source of an item, and what a client asks for when
        // it has made no choice. Jellyfin for Android sends the item identifier as the media
        // source identifier by default and the server matches it with an ordinal comparison
        // before it will auto-open the stream, so an identifier of the plugin's own invention
        // would simply not be found.
        var source = LiveMediaSource.CreatePending(ItemId, "Das Erste HD");

        Assert.Equal(ItemId, source.Id);
        Assert.Equal("Das Erste HD", source.Name);
        Assert.True(source.RequiresOpening);
        Assert.True(source.IsInfiniteStream);
    }

    [Fact]
    public void AnOpenedSourceWithAKnownDescriptionTellsJellyfinNotToProbe()
    {
        // The streams are known, at the indices FFmpeg will give them. Left probeable, Jellyfin
        // replaces them with its own placeholder view -- one video, one audio, indices unknown --
        // which is exactly the description that makes its "-map" arguments land on wrong tracks.
        var source = LiveMediaSource.CreateOpened(
            ItemId,
            "Das Erste HD",
            "/buffers/tvheadend-abc.ts",
            "http://localhost:8096/LiveTv/LiveStreamFiles/abc/stream.ts",
            Description(),
            requiresVideoReencode: false);

        Assert.False(source.SupportsProbing);
        Assert.Equal(2, source.MediaStreams.Count);
        Assert.False(source.RequiresOpening);
        Assert.True(source.RequiresClosing);
    }

    [Fact]
    public void NoAudioTrackIsNominatedAsTheDefault()
    {
        // Measured: naming one made the Android client pin it in its next question, and a pinned
        // track collapses Jellyfin's candidate list to that track alone (StreamBuilder widens to
        // every audio stream only while none is pinned and no default has a source). The track
        // named was the first of the program map, which on the German broadcasts is MPEG audio, so
        // a device that cannot decode it was made to transcode a stream it could have taken as
        // delivered -- over a preference nobody had expressed.
        var source = LiveMediaSource.CreateOpened(
            ItemId,
            "Das Erste HD",
            "/buffers/tvheadend-abc.ts",
            "http://localhost:8096/LiveTv/LiveStreamFiles/abc/stream.ts",
            Description(),
            requiresVideoReencode: false);

        Assert.Null(source.DefaultAudioStreamIndex);
    }

    [Fact]
    public void ALiveSourceIsNeverProbed()
    {
        // Both ends of the open path, and the point of the whole exercise. Jellyfin probes a
        // source when it supports probing and its streams have no indices yet; either half of
        // that is enough to start a second read of a stream that is already being read, to
        // answer a question the program map has already answered. Neither is ever true here:
        // unopened it offers no streams and refuses probing, opened it states them outright.
        var pending = LiveMediaSource.CreatePending(ItemId, "Das Erste HD");
        var opened = LiveMediaSource.CreateOpened(
            ItemId,
            "Das Erste HD",
            "/buffers/tvheadend-abc.ts",
            "http://localhost:8096/LiveTv/LiveStreamFiles/abc/stream.ts",
            Description(),
            requiresVideoReencode: false);

        Assert.False(pending.SupportsProbing);
        Assert.False(opened.SupportsProbing);
        Assert.Empty(pending.MediaStreams);
        Assert.All(opened.MediaStreams, stream => Assert.True(stream.Index >= 0));
    }

    [Fact]
    public void TheSourceIsPublishedAtTheAddressThatKeepsRunning()
    {
        // Measured: a client sent to the buffer file is served a file, which ends -- 5,434 bytes in
        // 47 ms and a clean 200, which the player reads as a finished medium. The live stream
        // address does not end, because it is served by this plugin waiting for what has not
        // been written yet.
        var source = LiveMediaSource.CreateOpened(
            ItemId,
            "Das Erste HD",
            "/buffers/tvheadend-abc.ts",
            "http://localhost:8096/LiveTv/LiveStreamFiles/abc/stream.ts",
            Description(),
            requiresVideoReencode: false);

        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.Equal("http://localhost:8096/LiveTv/LiveStreamFiles/abc/stream.ts", source.Path);
        Assert.Equal(MediaProtocol.Http, source.EncoderProtocol);
    }

    [Fact]
    public void TheContainerIsTheNameJellyfinsOwnProfilesUse()
    {
        // A device profile lists MPEG-TS as "ts", and a container that does not match it is a
        // container that cannot direct play. Checked against Jellyfin's own comparison rather
        // than a restatement of it -- the profile side is spelled the way Android TV publishes it.
        var source = LiveMediaSource.CreatePending(ItemId, "Das Erste HD");

        Assert.Equal("ts", source.Container);
        Assert.True(ContainerHelper.ContainsContainer("ts", source.Container));
    }

    [Fact]
    public void TheContainerStillReachesFFmpegAsAFormatItHas()
    {
        // The other half of the same value: with hardware acceleration configured Jellyfin passes
        // the container to FFmpeg as -f. It translates on the way, so no FFmpeg spelling is needed
        // here -- but the translation has to exist, which is what this reads. Naming two spellings
        // at once is what got through untranslated once, as "-f mpegts,ts", and played nothing.
        Assert.Equal("mpegts", EncodingHelper.GetInputFormat(LiveMediaSource.Container));
        Assert.DoesNotContain(",", LiveMediaSource.Container, StringComparison.Ordinal);
    }

    [Fact]
    public void AChannelHasNoRuntimeHoweverMuchHasBeenReceived()
    {
        var source = LiveMediaSource.CreateOpened(
            ItemId,
            "Das Erste HD",
            "/buffers/tvheadend-abc.ts",
            "http://localhost:8096/LiveTv/LiveStreamFiles/abc/stream.ts",
            Description(),
            requiresVideoReencode: false);

        Assert.Null(source.RunTimeTicks);
        Assert.Null(source.Size);
    }

    private static LiveStreamDescription Description()
        => new()
        {
            Streams =
            [
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "mp2" },
            ],
        };
}
