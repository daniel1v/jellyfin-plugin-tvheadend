using System;
using System.Linq;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using TVHeadEnd;
using Xunit;

namespace TVHeadEnd.Tests;

public class RecordingsChannelTests
{
    [Fact]
    public void TheChannelAnswersTheMediaInfoCallback()
    {
        // What a listing reports is a placeholder, so playback has to be able to ask for the
        // real description. Without this interface there is nothing to ask.
        Assert.True(typeof(IRequiresMediaInfoCallback).IsAssignableFrom(typeof(RecordingsChannel)));
    }

    [Fact]
    public void APlaceholderCarriesNoStreamsAtAll()
    {
        // Inventing them is worse than saying nothing: Jellyfin maps streams by their position
        // in this list, so made-up entries send FFmpeg's map arguments to the wrong tracks.
        var placeholder = RecordingsChannel.BuildPlaceholderSource("1312160563");

        Assert.Empty(placeholder.MediaStreams);
        Assert.Equal(MediaSourceType.Placeholder, placeholder.Type);
    }

    [Fact]
    public void APlaceholderIsTheSameSourceAsTheOneItStandsFor()
    {
        // One recording, one identifier. The placeholder and the described source are the same
        // source in two states, and only Type says which state it is in.
        //
        // Giving them separate identifiers is what broke playback. Jellyfin drops placeholders
        // before deciding, then keeps only the source whose identifier the client sent back -- so
        // a client that had stored the listing named a source that no longer existed, and the
        // described one was discarded for not matching it.
        var placeholder = RecordingsChannel.BuildPlaceholderSource("1312160563");

        Assert.Equal(RecordingsChannel.RecordingMediaSourceId("1312160563"), placeholder.Id);
        Assert.Equal(MediaSourceType.Placeholder, placeholder.Type);
    }

    [Fact]
    public void APlaceholderIsIdentifiedByTheSameGuidOnEveryCall()
    {
        // It reaches the client through a stored channel item and comes back much later, from a
        // different process. Both halves matter: readable as a GUID, and the same one every time.
        var first = RecordingsChannel.BuildPlaceholderSource("1312160563");
        var second = RecordingsChannel.BuildPlaceholderSource("1312160563");

        Assert.Equal(first.Id, second.Id);
        Assert.True(Guid.TryParse(first.Id, out _));
        Assert.NotEqual(first.Id, RecordingsChannel.BuildPlaceholderSource("962787396").Id);
    }

    [Fact]
    public void TheDescribedSourceIsIdentifiedByAGuid()
    {
        // DynamicHlsHelper.GetMasterPlaylistInternal parses this as a GUID unconditionally, and
        // StreamingHelpers.GetStreamingState does when its lookup finds nothing. Anything else
        // fails the request with "Unrecognized Guid format" before playback starts.
        Assert.True(Guid.TryParse(RecordingsChannel.RecordingMediaSourceId("1312160563"), out _));
    }

    [Fact]
    public void TheDescribedSourceKeepsItsIdentifierAcrossCalls()
    {
        // Jellyfin stores it and every later request carries it back, including after a restart.
        var first = RecordingsChannel.RecordingMediaSourceId("867835561");
        var second = RecordingsChannel.RecordingMediaSourceId("867835561");

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentRecordingsGetDifferentIdentifiers()
    {
        Assert.NotEqual(
            RecordingsChannel.RecordingMediaSourceId("867835561"),
            RecordingsChannel.RecordingMediaSourceId("962787396"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ARecordingWithoutAnIdentifierIsRefused(string? id)
    {
        Assert.ThrowsAny<ArgumentException>(() => RecordingsChannel.RecordingMediaSourceId(id!));
        Assert.ThrowsAny<ArgumentException>(() => RecordingsChannel.BuildPlaceholderSource(id!));
    }

    [Fact]
    public void TheChannelNameIsTheOneTheOwnershipCheckDerivesFrom()
    {
        // Jellyfin derives the channel entity's identifier from this name alone, and writes that
        // identifier onto every recording it stores. Recognising our own recordings later means
        // deriving the very same identifier, so the two must be one string rather than two that
        // happen to agree -- changing it here alone would orphan every stored recording silently.
        var channel = (TVHeadEnd.RecordingsChannel)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(TVHeadEnd.RecordingsChannel));

        Assert.Equal(TVHeadEnd.Playback.TvheadendItems.RecordingsChannelName, channel.Name);
    }

    [Fact]
    public void APlaceholderStatesTheContainerButPromisesNothingElse()
    {
        var placeholder = RecordingsChannel.BuildPlaceholderSource("1312160563");

        Assert.Equal("ts", placeholder.Container);
        Assert.Null(placeholder.Path);
        Assert.Null(placeholder.RunTimeTicks);
    }

    [Fact]
    public void ARecordingSurvivesAClientAskingForItByTheIdentifierItWasListedUnder()
    {
        // The failure, played out through the two steps Jellyfin actually takes.
        //
        // A client that browsed the library holds the stored item, whose one source is the
        // placeholder. When it starts playback it names that source. Jellyfin then drops every
        // placeholder from the candidates and keeps only the one the client named -- so if the
        // described source carries a different identifier, nothing is left and playback fails
        // with no compatible stream before the recording is ever opened.
        const string RecordingId = "2061373994";

        var stored = RecordingsChannel.BuildPlaceholderSource(RecordingId);
        var described = RecordingsChannel.BuildRecordingSource(RecordingId, "http://host:8096/x");
        described.MediaStreams =
        [
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264" },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "mp2", IsDefault = true },
        ];

        // What the client sends back is what it was given in the listing.
        var requested = stored.Id;

        var chosen = AsJellyfinWouldChoose([stored, described], requested);

        var only = Assert.Single(chosen);
        Assert.Equal(requested, only.Id);
        Assert.NotEqual(MediaSourceType.Placeholder, only.Type);
        Assert.Contains(only.MediaStreams, stream => stream.Type == MediaStreamType.Video);
    }

    [Fact]
    public void AClientThatNamesNoSourceStillGetsTheDescribedOne()
    {
        // The other route into playback, which worked all along and must keep working: the client
        // asks without naming a source and is handed whatever is left once placeholders are gone.
        const string RecordingId = "2061373994";

        var chosen = AsJellyfinWouldChoose(
            [
                RecordingsChannel.BuildPlaceholderSource(RecordingId),
                RecordingsChannel.BuildRecordingSource(RecordingId, "http://host:8096/x"),
            ],
            requestedMediaSourceId: null);

        var only = Assert.Single(chosen);
        Assert.NotEqual(MediaSourceType.Placeholder, only.Type);
    }

    /// <summary>
    /// The two steps Jellyfin takes between a playback request and a chosen source.
    /// </summary>
    /// <remarks>
    /// Written out rather than reasoned about, and no more of Jellyfin than these two lines:
    /// <c>MediaSourceManager.SortMediaSources</c> ends by discarding every placeholder, and
    /// <c>MediaInfoHelper.GetPlaybackMediaSources</c> then keeps only the source whose identifier
    /// the client sent, when it sent one.
    /// </remarks>
    /// <param name="sources">Everything the item and the plugin between them offer.</param>
    /// <param name="requestedMediaSourceId">What the client named, if anything.</param>
    /// <returns>What playback would be decided against.</returns>
    private static MediaSourceInfo[] AsJellyfinWouldChoose(
        MediaSourceInfo[] sources,
        string? requestedMediaSourceId)
    {
        var playable = sources.Where(source => source.Type != MediaSourceType.Placeholder);

        return string.IsNullOrWhiteSpace(requestedMediaSourceId)
            ? [.. playable]
            : [.. playable.Where(source => string.Equals(source.Id, requestedMediaSourceId, StringComparison.OrdinalIgnoreCase))];
    }
}
