using System;
using System.Globalization;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;

namespace TVHeadEnd.LiveTv
{
    /// <summary>
    /// Derives the internal Jellyfin item identifier of a live TV channel.
    /// </summary>
    public sealed class ChannelItemIds
    {
        // Jellyfin.LiveTv.LiveTvDtoService uses this private version when it creates
        // channel item IDs. Jellyfin Android sends that item ID as MediaSourceId, so
        // the pending source must use the same value for auto-open to find it.
        private const string JellyfinLiveTvInternalVersion = "4";

        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChannelItemIds"/> class.
        /// </summary>
        /// <param name="libraryManager">The Jellyfin library manager.</param>
        public ChannelItemIds(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Gets the internal Jellyfin item identifier for a provider channel.
        /// </summary>
        /// <param name="serviceName">The live TV service name.</param>
        /// <param name="externalChannelId">The provider's channel identifier.</param>
        /// <returns>The internal identifier without separators.</returns>
        public string GetInternalChannelId(string serviceName, string externalChannelId)
        {
            ArgumentException.ThrowIfNullOrEmpty(serviceName);
            ArgumentException.ThrowIfNullOrEmpty(externalChannelId);

            var key = (serviceName + externalChannelId + JellyfinLiveTvInternalVersion).ToLowerInvariant();
            return _libraryManager
                .GetNewItemId(key, typeof(LiveTvChannel))
                .ToString("N", CultureInfo.InvariantCulture);
        }
    }
}
