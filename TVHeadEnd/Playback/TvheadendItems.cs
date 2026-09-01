using System;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Channels;

namespace TVHeadEnd.Playback;

/// <summary>
/// Whether an item Jellyfin was asked about is one of this plugin's.
/// </summary>
/// <remarks>
/// Asked of the library rather than of the request, because the request cannot answer it: a
/// display name, a path fragment or a prefix would all be coincidences waiting to happen. Both
/// kinds of item this plugin produces record where they came from, and that record is what is
/// read here -- the service for a live channel, the owning channel for a recording.
/// </remarks>
public static class TvheadendItems
{
    /// <summary>
    /// The name this plugin's live TV service is registered under.
    /// </summary>
    /// <remarks>
    /// Stated here rather than on the service, so that reading it does not mean holding the
    /// service. <c>LiveTvService.Name</c> returns this, and it is the value Jellyfin stores on
    /// every channel this plugin produced.
    /// </remarks>
    public const string ServiceName = "TVHclient LiveTvService";

    /// <summary>
    /// The name this plugin's recordings channel is registered under.
    /// </summary>
    /// <remarks>
    /// The value of <c>RecordingsChannel.Name</c>, and the only input to the identifier Jellyfin
    /// derives for the channel entity -- see <see cref="RecordingsChannelId"/>.
    /// </remarks>
    public const string RecordingsChannelName = "TVHeadEnd Recordings";

    /// <summary>
    /// The version suffix Jellyfin's channel manager mixes into every channel item identifier.
    /// </summary>
    /// <remarks>
    /// Its own comment calls it "increment this as needed to force new downloads". It is part of
    /// the hash, so it is part of the identifier, and it is named here because a wrong one is a
    /// silently different item rather than an error.
    /// </remarks>
    private const string ChannelItemIdVersion = "16";

    /// <summary>
    /// Whether the item is one this plugin provides: a live channel or a recording.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="itemId">The item the request names.</param>
    /// <returns><see langword="true"/> when the item is one of ours.</returns>
    public static bool IsOurs(ILibraryManager libraryManager, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);

        if (itemId.Equals(default))
        {
            return false;
        }

        // Anything the library does not know is not ours and is left entirely alone.
        var item = libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return false;
        }

        // A live channel records the service that produced it.
        if (item is LiveTvChannel channel)
        {
            return string.Equals(channel.ServiceName, ServiceName, StringComparison.Ordinal);
        }

        return IsRecording(libraryManager, item);
    }

    /// <summary>
    /// Whether one item Jellyfin holds is a recording this plugin produced.
    /// </summary>
    /// <remarks>
    /// Asked of Jellyfin's own channel manager, which wrote the owning channel onto every item it
    /// stored. A film, an episode or another plugin's channel item carries a different identifier
    /// here, and an ordinary library item carries none at all -- so no display name, path fragment
    /// or media source identifier has to be guessed at.
    /// </remarks>
    /// <param name="libraryManager">Jellyfin's library, which owns the derivation.</param>
    /// <param name="item">The item in question.</param>
    /// <returns><see langword="true"/> when the item is one of this plugin's recordings.</returns>
    public static bool IsRecording(ILibraryManager libraryManager, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(item);

        // A live channel is this plugin's too, and is emphatically not a recording.
        return item is not LiveTvChannel
            && item.ChannelId.Equals(RecordingsChannelId(libraryManager));
    }

    /// <summary>
    /// The identifier Jellyfin's channel manager gives this plugin's recordings channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived exactly as <c>ChannelManager.GetInternalChannelId</c> derives it --
    /// <c>GetNewItemId("Channel " + name, typeof(Channel))</c> -- so this is the same identifier
    /// the channel manager wrote onto every recording it stored, not a guess that resembles it.
    /// The channel name is the only input, which is why it is the only thing named here.
    /// </para>
    /// <para>
    /// Recomputed per call rather than cached: it is a hash of a constant, the library is the one
    /// that owns the derivation, and a cached copy is a copy that can be wrong after an upgrade.
    /// </para>
    /// </remarks>
    /// <param name="libraryManager">Jellyfin's library, which owns the derivation.</param>
    /// <returns>The channel entity's identifier.</returns>
    public static Guid RecordingsChannelId(ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);

        return libraryManager.GetNewItemId("Channel " + RecordingsChannelName, typeof(Channel));
    }

    /// <summary>
    /// The identifier Jellyfin's channel manager gives one recording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived exactly as <c>ChannelManager.GetItemById</c> derives it:
    /// <c>GetNewItemId(externalId + channelName + "16", itemType)</c>, where the trailing "16" is
    /// the channel manager's own version suffix. Not a resemblance -- the same three inputs
    /// through the same library call, so this is the identifier Jellyfin actually stored.
    /// </para>
    /// <para>
    /// Wanted because a media source is addressed by it. Jellyfin gives an ordinary library item a
    /// media source whose identifier <em>is</em> the item's, and clients rely on that: the native
    /// Android app, asked to play something for which it holds no media source, sends the item
    /// identifier as the media source identifier -- and the server keeps only the source that
    /// matches it. A recording identified any other way is a recording that client cannot name.
    /// </para>
    /// </remarks>
    /// <param name="libraryManager">Jellyfin's library, which owns the derivation.</param>
    /// <param name="recordingId">The TVHeadend recording identifier, which is the item's external one.</param>
    /// <param name="itemType">The type the channel manager stores the recording as.</param>
    /// <returns>The recording item's identifier.</returns>
    public static Guid RecordingItemId(ILibraryManager libraryManager, string recordingId, Type itemType)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentException.ThrowIfNullOrEmpty(recordingId);
        ArgumentNullException.ThrowIfNull(itemType);

        return libraryManager.GetNewItemId(recordingId + RecordingsChannelName + ChannelItemIdVersion, itemType);
    }

    /// <summary>
    /// The type Jellyfin's channel manager stores a recording as.
    /// </summary>
    /// <remarks>
    /// The type is part of the identifier, so this has to answer exactly what
    /// <c>ChannelManager.GetChannelItemEntity</c> answers for the same channel item. It reads the
    /// two fields the recordings channel publishes and nothing else, which is what keeps the two
    /// from drifting: the same media type and content type go into both.
    /// </remarks>
    /// <param name="mediaType">The media type the recording is published with.</param>
    /// <param name="contentType">The content type the recording is published with.</param>
    /// <returns>The item type.</returns>
    public static Type RecordingItemType(ChannelMediaType mediaType, ChannelMediaContentType contentType)
    {
        // A radio recording. Podcast is the channel manager's other audio branch and nothing here
        // ever publishes it.
        if (mediaType == ChannelMediaType.Audio)
        {
            return typeof(MediaBrowser.Controller.Entities.Audio.Audio);
        }

        return contentType switch
        {
            ChannelMediaContentType.Episode => typeof(Episode),
            ChannelMediaContentType.Movie => typeof(Movie),
            _ => typeof(Video),
        };
    }
}
