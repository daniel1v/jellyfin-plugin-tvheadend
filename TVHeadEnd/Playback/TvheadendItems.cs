using System;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;

namespace TVHeadEnd.Playback;

/// <summary>
/// Whether an item Jellyfin was asked about is one of this plugin's.
/// </summary>
/// <remarks>
/// Asked of the library rather than of the request, because the request cannot answer it: a
/// display name, a path fragment or a prefix would all be coincidences waiting to happen. A live
/// channel records the service that produced it, and that is the identity used here.
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
    /// Whether the item is a live channel this plugin provides.
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

        // Anything the library does not know, and anything it knows as something else, is not
        // ours and is left entirely alone.
        return libraryManager.GetItemById(itemId) is LiveTvChannel channel
            && string.Equals(channel.ServiceName, ServiceName, StringComparison.Ordinal);
    }
}
