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
            Description());

        Assert.False(source.SupportsProbing);
        Assert.Equal(2, source.MediaStreams.Count);
        Assert.Equal(1, source.DefaultAudioStreamIndex);
        Assert.False(source.RequiresOpening);
        Assert.True(source.RequiresClosing);
    }

    [Fact]
    public void AnOpenedSourceWithNoDescriptionLetsJellyfinEstablishItInstead()
    {
        // Nothing trustworthy to say. Handing over an empty stream list and claiming it is
        // complete would have Jellyfin act on a description of nothing.
        var source = LiveMediaSource.CreateOpened(
            ItemId,
            "Das Erste HD",
            "/buffers/tvheadend-abc.ts",
            "http://localhost:8096/LiveTv/LiveStreamFiles/abc/stream.ts",
            description: null);

        Assert.True(source.SupportsProbing);
        Assert.Empty(source.MediaStreams);
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
            Description());

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
            Description());

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
            HasUnclassifiedStream = false,
        };
}
