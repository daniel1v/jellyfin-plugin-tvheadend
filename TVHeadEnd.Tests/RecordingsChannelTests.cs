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
    public void APlaceholderIsNotIdentifiedByAGuid()
    {
        // Measured, and the reason is entirely Jellyfin's. A placeholder is a *saved* source, and
        // MediaSourceManager.GetStaticMediaSources keeps a saved source only when its identifier
        // fails to parse as a GUID, or parses to the item's own identifier, or names a library
        // item the user can see. A GUID derived from the recording is none of the three.
        //
        // Giving the placeholder the described source's GUID -- which looks like the tidier
        // answer, one recording and one identifier -- therefore removes the item's only static
        // source, and GetPlaybackMediaSources throws on mediaSources[0] before this plugin is
        // reached at all. Every PlaybackInfo request answered 500.
        var placeholder = RecordingsChannel.BuildPlaceholderSource("1312160563");

        Assert.False(Guid.TryParse(placeholder.Id, out _));
        Assert.NotEqual(RecordingsChannel.RecordingMediaSourceId("1312160563"), placeholder.Id);
        Assert.Equal(MediaSourceType.Placeholder, placeholder.Type);
    }

    [Fact]
    public void APlaceholderKeepsItsIdentifierAcrossCalls()
    {
        // It reaches the client through a stored channel item and comes back much later, from a
        // different process.
        Assert.Equal(
            RecordingsChannel.BuildPlaceholderSource("1312160563").Id,
            RecordingsChannel.BuildPlaceholderSource("1312160563").Id);

        Assert.NotEqual(
            RecordingsChannel.BuildPlaceholderSource("1312160563").Id,
            RecordingsChannel.BuildPlaceholderSource("962787396").Id);
    }

    [Fact]
    public void ASavedPlaceholderSurvivesTheFilterJellyfinPutsItThrough()
    {
        // The filter itself, written out because it is the whole reason the identifier looks the
        // way it does. A saved source is kept only when one of three things holds, and for a
        // recording only the first of them can.
        var placeholder = RecordingsChannel.BuildPlaceholderSource("2061373994");
        var itemId = Guid.NewGuid();

        Assert.True(SurvivesTheStaticSourceFilter(placeholder.Id, itemId));

        // What the GUID form would have done to it.
        Assert.False(SurvivesTheStaticSourceFilter(RecordingsChannel.RecordingMediaSourceId("2061373994"), itemId));
    }

    /// <summary>
    /// The test <c>MediaSourceManager.GetStaticMediaSources</c> puts every saved source through.
    /// </summary>
    /// <remarks>
    /// No more of Jellyfin than the one predicate: an identifier that is not a GUID passes, one
    /// that is the item's own passes, and any other GUID has to name a library item the user can
    /// see -- which a recording's derived identifier never does.
    /// </remarks>
    /// <param name="sourceId">The saved source's identifier.</param>
    /// <param name="itemId">The item the source belongs to.</param>
    /// <returns>Whether the source is kept.</returns>
    private static bool SurvivesTheStaticSourceFilter(string sourceId, Guid itemId)
        => !Guid.TryParse(sourceId, out var parsed) || parsed.Equals(itemId);

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
    public void AClientThatNamesNoSourceGetsTheDescribedOne()
    {
        // How playback is actually reached. The client asks without naming a source, Jellyfin
        // drops the placeholder, and what is left is the described source -- whose identifier the
        // client then uses for everything after.
        const string RecordingId = "2061373994";

        var chosen = AsJellyfinWouldChoose(
            [
                RecordingsChannel.BuildPlaceholderSource(RecordingId),
                Described(RecordingId),
            ],
            requestedMediaSourceId: null);

        var only = Assert.Single(chosen);
        Assert.NotEqual(MediaSourceType.Placeholder, only.Type);
        Assert.Equal(RecordingsChannel.RecordingMediaSourceId(RecordingId), only.Id);
        Assert.Contains(only.MediaStreams, stream => stream.Type == MediaStreamType.Video);
    }

    [Fact]
    public void AClientThatNamesTheDescribedSourceGetsIt()
    {
        // The second request of every playback: the client returns the identifier it was handed.
        const string RecordingId = "2061373994";

        var chosen = AsJellyfinWouldChoose(
            [
                RecordingsChannel.BuildPlaceholderSource(RecordingId),
                Described(RecordingId),
            ],
            RecordingsChannel.RecordingMediaSourceId(RecordingId));

        var only = Assert.Single(chosen);
        Assert.NotEqual(MediaSourceType.Placeholder, only.Type);
    }

    private static MediaSourceInfo Described(string recordingId)
    {
        var described = RecordingsChannel.BuildRecordingSource(recordingId, "http://host:8096/x");
        described.MediaStreams =
        [
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264" },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "mp2", IsDefault = true },
        ];

        return described;
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
