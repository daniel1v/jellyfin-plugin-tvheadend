using System;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;

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
    /// Named rather than referenced to keep this free of the service itself. It is the value of
    /// <c>LiveTvService.Name</c>, and the one Jellyfin stores on every channel it produced.
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

        // Everything else is a recording only if Jellyfin's own channel manager says it belongs
        // to this plugin's channel. A film, an episode or another plugin's channel item carries a
        // different identifier here, and an ordinary library item carries none at all.
        return item.ChannelId.Equals(RecordingsChannelId(libraryManager));
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
}
