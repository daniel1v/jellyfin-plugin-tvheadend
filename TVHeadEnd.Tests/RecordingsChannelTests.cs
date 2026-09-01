using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using TVHeadEnd;
using TVHeadEnd.Playback;
using TVHeadEnd.Recordings;
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
        var placeholder = RecordingMediaSourceFactory.BuildPlaceholderSource(SomeItemId);

        Assert.Empty(placeholder.MediaStreams);
        Assert.Equal(MediaSourceType.Placeholder, placeholder.Type);
    }

    [Fact]
    public void APlaceholderStatesTheContainerButPromisesNothingElse()
    {
        var placeholder = RecordingMediaSourceFactory.BuildPlaceholderSource(SomeItemId);

        Assert.Equal("ts", placeholder.Container);
        Assert.Null(placeholder.Path);
        Assert.Null(placeholder.RunTimeTicks);
    }

    [Fact]
    public void ARecordingIsIdentifiedByTheIdentifierJellyfinGaveItsItem()
    {
        // The whole of the fix, and it is not a preference. Jellyfin gives an ordinary library
        // item a media source whose identifier is the item's own -- BaseItem.GetVersionInfo writes
        // item.Id.ToString("N") -- and clients are built on that. Measured on Jellyfin for Android
        // 2.7.1: asked to play a recording it holds no media source for, it sends the item
        // identifier as the media source identifier, the server keeps only the source that matches
        // it, and with any other identifier the answer carries no sources and no play session.
        var library = new RecordingLibraryManager();
        var recording = Recording("2061373994");

        var mediaSourceId = RecordingMediaSourceFactory.RecordingMediaSourceId(library.AsLibraryManager, recording);

        Assert.Equal(library.ItemIdFor("2061373994", typeof(Video)).ToString("N", CultureInfo.InvariantCulture), mediaSourceId);
    }

    [Fact]
    public void TheIdentifierIsDerivedTheWayJellyfinDerivesAChannelItemIdentifier()
    {
        // Not a resemblance: the same three inputs through the same library call that
        // ChannelManager.GetItemById makes -- GetNewItemId(externalId + channelName + "16", type).
        // The "16" is the channel manager's own version suffix. If any of the three drifts, the
        // identifier names an item Jellyfin never stored, so the inputs are what is pinned here.
        var library = new RecordingLibraryManager();

        TvheadendItems.RecordingItemId(library.AsLibraryManager, "2061373994", typeof(Video));

        var call = Assert.Single(library.Calls);
        Assert.Equal("2061373994" + TvheadendItems.RecordingsChannelName + "16", call.Key);
        Assert.Equal(typeof(Video), call.Type);
    }

    [Theory]
    [InlineData(ChannelMediaType.Video, ChannelMediaContentType.Movie, typeof(Movie))]
    [InlineData(ChannelMediaType.Video, ChannelMediaContentType.Episode, typeof(Episode))]
    [InlineData(ChannelMediaType.Video, ChannelMediaContentType.Clip, typeof(Video))]
    [InlineData(ChannelMediaType.Audio, ChannelMediaContentType.Clip, typeof(MediaBrowser.Controller.Entities.Audio.Audio))]
    public void TheItemTypeMatchesTheBranchTheChannelManagerTakes(
        ChannelMediaType mediaType,
        ChannelMediaContentType contentType,
        Type expected)
    {
        // The type is part of the identifier, so it has to be the type ChannelManager actually
        // stored the recording as -- its audio branch first, then episode, movie, and video for
        // everything else.
        Assert.Equal(expected, TvheadendItems.RecordingItemType(mediaType, contentType));
    }

    [Fact]
    public void TheContentTypePublishedAndTheOneDerivedFromAreOneAnswer()
    {
        // Two spellings of this would be two different items: the channel item is published with
        // it, and the identifier is derived from it.
        Assert.Equal(ChannelMediaContentType.Movie, RecordingItemMapper.ContentTypeFor(new MyRecordingInfo { Id = "1", IsMovie = true }));
        Assert.Equal(ChannelMediaContentType.Episode, RecordingItemMapper.ContentTypeFor(new MyRecordingInfo { Id = "1", IsSeries = true }));
        Assert.Equal(ChannelMediaContentType.Clip, RecordingItemMapper.ContentTypeFor(new MyRecordingInfo { Id = "1" }));

        // A film that is also flagged as a series is a film, as the published value has always
        // read it.
        Assert.Equal(
            ChannelMediaContentType.Movie,
            RecordingItemMapper.ContentTypeFor(new MyRecordingInfo { Id = "1", IsMovie = true, IsSeries = true }));
    }

    [Fact]
    public void ThePlaceholderAndTheDescribedSourceShareOneIdentifier()
    {
        // One recording, one identifier. What tells the two apart is Type, which is what it is
        // for -- Jellyfin drops every placeholder before playback is decided.
        var library = new RecordingLibraryManager();
        var recording = Recording("2061373994");
        var id = RecordingMediaSourceFactory.RecordingMediaSourceId(library.AsLibraryManager, recording);

        var placeholder = RecordingMediaSourceFactory.BuildPlaceholderSource(id);
        var described = RecordingMediaSourceFactory.BuildRecordingSource("2061373994", id, "http://host:8096/x");

        Assert.Equal(placeholder.Id, described.Id);
        Assert.Equal(MediaSourceType.Placeholder, placeholder.Type);
        Assert.NotEqual(MediaSourceType.Placeholder, described.Type);
    }

    [Fact]
    public void TheIdentifierIsAGuidBecauseTwoPlacesDownstreamParseItAsOne()
    {
        // DynamicHlsHelper.GetMasterPlaylistInternal parses it unconditionally, and
        // StreamingHelpers.GetStreamingState does when its lookup finds nothing. An item
        // identifier is a GUID, so this comes for free -- which is part of why it is the right
        // identifier to use.
        var library = new RecordingLibraryManager();

        Assert.True(Guid.TryParse(RecordingMediaSourceFactory.RecordingMediaSourceId(library.AsLibraryManager, Recording("1312160563")), out _));
    }

    [Fact]
    public void ASavedPlaceholderSurvivesTheFilterJellyfinPutsItThrough()
    {
        // The filter that broke the previous attempt at one identifier.
        // MediaSourceManager.GetStaticMediaSources keeps a saved source only when its identifier
        // fails to parse as a GUID, or parses to the item's own identifier, or names a library
        // item the user can see. The item's own identifier passes on the second branch -- a GUID
        // derived from the recording number passed on none, which left the item with no static
        // source at all and made GetPlaybackMediaSources throw on mediaSources[0].
        var library = new RecordingLibraryManager();
        var recording = Recording("2061373994");
        var itemId = library.ItemIdFor("2061373994", typeof(Video));

        var placeholder = RecordingMediaSourceFactory.BuildPlaceholderSource(
            RecordingMediaSourceFactory.RecordingMediaSourceId(library.AsLibraryManager, recording));

        Assert.True(SurvivesTheStaticSourceFilter(placeholder.Id, itemId));

        // What any other GUID would have done to it.
        Assert.False(SurvivesTheStaticSourceFilter(Guid.NewGuid().ToString("N"), itemId));
    }

    [Fact]
    public void AClientThatNamesTheItemGetsTheDescribedSource()
    {
        // The measured failure, played out through the two steps Jellyfin takes. The native
        // Android app sends the item identifier; Jellyfin drops every placeholder and then keeps
        // only the source that matches what was sent. With the identifiers apart, nothing was
        // left: no sources, no play session, and a black screen with no error anywhere.
        var library = new RecordingLibraryManager();
        var recording = Recording("2061373994");
        var itemId = library.ItemIdFor("2061373994", typeof(Video)).ToString("N", CultureInfo.InvariantCulture);

        var chosen = AsJellyfinWouldChoose(Sources(library, recording), requestedMediaSourceId: itemId);

        var only = Assert.Single(chosen);
        Assert.Equal(itemId, only.Id);
        Assert.NotEqual(MediaSourceType.Placeholder, only.Type);
        Assert.Contains(only.MediaStreams, stream => stream.Type == MediaStreamType.Video);
    }

    [Fact]
    public void AClientThatNamesNoSourceGetsTheDescribedOne()
    {
        // The other route in, which worked all along and must keep working.
        var library = new RecordingLibraryManager();

        var chosen = AsJellyfinWouldChoose(Sources(library, Recording("2061373994")), requestedMediaSourceId: null);

        var only = Assert.Single(chosen);
        Assert.NotEqual(MediaSourceType.Placeholder, only.Type);
    }

    [Fact]
    public void TheIdentifierIsTheSameOnEveryCallAndDiffersPerRecording()
    {
        // Jellyfin stores it and every later request carries it back, including after a restart.
        var library = new RecordingLibraryManager();

        Assert.Equal(
            RecordingMediaSourceFactory.RecordingMediaSourceId(library.AsLibraryManager, Recording("867835561")),
            RecordingMediaSourceFactory.RecordingMediaSourceId(library.AsLibraryManager, Recording("867835561")));

        Assert.NotEqual(
            RecordingMediaSourceFactory.RecordingMediaSourceId(library.AsLibraryManager, Recording("867835561")),
            RecordingMediaSourceFactory.RecordingMediaSourceId(library.AsLibraryManager, Recording("962787396")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ARecordingWithoutAnIdentifierIsRefused(string? id)
    {
        var library = new RecordingLibraryManager();

        Assert.ThrowsAny<ArgumentException>(
            () => RecordingMediaSourceFactory.RecordingMediaSourceId(library.AsLibraryManager, new MyRecordingInfo { Id = id }));
        Assert.ThrowsAny<ArgumentException>(() => RecordingMediaSourceFactory.BuildPlaceholderSource(id!));
    }

    [Fact]
    public void TheChannelNameIsTheOneTheOwnershipCheckDerivesFrom()
    {
        // Jellyfin derives the channel entity's identifier from this name alone, and writes that
        // identifier onto every recording it stores. Recognising our own recordings later means
        // deriving the very same identifier, so the two must be one string rather than two that
        // happen to agree -- changing it here alone would orphan every stored recording silently.
        var channel = (RecordingsChannel)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(RecordingsChannel));

        Assert.Equal(TvheadendItems.RecordingsChannelName, channel.Name);
    }

    private const string SomeItemId = "1f6cf027e0f2168c8ffaab722d151bb1";

    private static MyRecordingInfo Recording(string id)
        => new() { Id = id, ChannelType = MediaBrowser.Model.LiveTv.ChannelType.TV };

    private static MediaSourceInfo[] Sources(RecordingLibraryManager library, MyRecordingInfo recording)
    {
        var id = RecordingMediaSourceFactory.RecordingMediaSourceId(library.AsLibraryManager, recording);
        var described = RecordingMediaSourceFactory.BuildRecordingSource(recording.Id!, id, "http://host:8096/x");
        described.MediaStreams =
        [
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264" },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "mp2", IsDefault = true },
        ];

        return [RecordingMediaSourceFactory.BuildPlaceholderSource(id), described];
    }

    /// <summary>
    /// The test <c>MediaSourceManager.GetStaticMediaSources</c> puts every saved source through.
    /// </summary>
    /// <remarks>
    /// No more of Jellyfin than the one predicate: an identifier that is not a GUID passes, one
    /// that is the item's own passes, and any other GUID has to name a library item the user can
    /// see -- which a recording's identifier never does.
    /// </remarks>
    private static bool SurvivesTheStaticSourceFilter(string sourceId, Guid itemId)
        => !Guid.TryParse(sourceId, out var parsed) || parsed.Equals(itemId);

    /// <summary>
    /// The two steps Jellyfin takes between a playback request and a chosen source.
    /// </summary>
    /// <remarks>
    /// Written out rather than reasoned about, and no more of Jellyfin than these two lines:
    /// <c>MediaSourceManager.SortMediaSources</c> ends by discarding every placeholder, and
    /// <c>MediaInfoHelper.GetPlaybackMediaSources</c> then keeps only the source whose identifier
    /// the client sent, when it sent one.
    /// </remarks>
    private static MediaSourceInfo[] AsJellyfinWouldChoose(
        MediaSourceInfo[] sources,
        string? requestedMediaSourceId)
    {
        var playable = sources.Where(source => source.Type != MediaSourceType.Placeholder);

        return string.IsNullOrWhiteSpace(requestedMediaSourceId)
            ? [.. playable]
            : [.. playable.Where(source => string.Equals(source.Id, requestedMediaSourceId, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// An <see cref="ILibraryManager"/> that answers one method and records how it was asked.
    /// </summary>
    /// <remarks>
    /// The point of the test is the <em>inputs</em> to <c>GetNewItemId</c> -- the key and the type
    /// -- because those are what have to match Jellyfin's own derivation. The hash itself is
    /// Jellyfin's and is deliberately not reimplemented here; any stable function of the two
    /// inputs serves, so this uses one.
    /// </remarks>
    private sealed class RecordingLibraryManager
    {
        public RecordingLibraryManager()
        {
            var proxy = DispatchProxy.Create<ILibraryManager, IdRecordingProxy>();
            AsLibraryManager = proxy;
            Recorder = (IdRecordingProxy)(object)proxy;
        }

        public ILibraryManager AsLibraryManager { get; }

        public IReadOnlyList<(string Key, Type Type)> Calls => Recorder.Calls;

        private IdRecordingProxy Recorder { get; }

        public Guid ItemIdFor(string recordingId, Type type)
            => IdRecordingProxy.Hash(recordingId + TvheadendItems.RecordingsChannelName + "16", type);
    }

    public class IdRecordingProxy : DispatchProxy
    {
        private readonly List<(string Key, Type Type)> _calls = [];

        public IReadOnlyList<(string Key, Type Type)> Calls => _calls;

        internal static Guid Hash(string key, Type type)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(key + "|" + type.FullName));

            return new Guid(bytes);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ILibraryManager.GetNewItemId))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            var key = (string)args![0]!;
            var type = (Type)args[1]!;
            _calls.Add((key, type));

            return Hash(key, type);
        }
    }
}
