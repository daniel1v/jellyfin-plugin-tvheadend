using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
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
    public void TheSourceIsReadAsAFileAndTranscodedFromTheHttpEndpoint()
    {
        // The buffer is on this server's disk, so the direct route reads it straight off disk;
        // anything that cannot fetches the same bytes over HTTP.
        var source = LiveMediaSource.CreateOpened(
            ItemId,
            "Das Erste HD",
            "/buffers/tvheadend-abc.ts",
            "http://localhost:8096/LiveTv/LiveStreamFiles/abc/stream.ts",
            Description(),
            requiresVideoReencode: false);

        Assert.Equal(MediaProtocol.File, source.Protocol);
        Assert.Equal("/buffers/tvheadend-abc.ts", source.Path);
        Assert.Equal(MediaProtocol.Http, source.EncoderProtocol);
    }

    [Fact]
    public void TheContainerIsNamedInBothSpellingsADeviceProfileMightUse()
    {
        // Jellyfin compares the two sides literally and splits each on commas, without knowing
        // that mpegts and ts are the same container. Jellyfin for Android only ever lists
        // mpegts; Jellyfin's own probe normaliser only ever produces ts.
        var source = LiveMediaSource.CreatePending(ItemId, "Das Erste HD");

        Assert.Contains("mpegts", source.Container!.Split(','));
        Assert.Contains("ts", source.Container!.Split(','));
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
