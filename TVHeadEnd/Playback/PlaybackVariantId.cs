using System;
using System.Globalization;
using MediaBrowser.Common.Extensions;

namespace TVHeadEnd.Playback
{
    /// <summary>
    /// The identifiers Jellyfin addresses the variants of a channel by.
    /// </summary>
    /// <remarks>
    /// Deterministic, so the identifier a client was given during negotiation still resolves on
    /// the open that follows, and always distinct per variant, so a native stream and a
    /// compatibility stream of the same channel can never be mistaken for one another when an
    /// open is reused. Deliberately never equal to the channel's own item identifier: Jellyfin's
    /// <c>SortMediaSources</c> promotes a source whose identifier matches the item, which would
    /// override the ordering this plugin chose.
    /// </remarks>
    public static class PlaybackVariantId
    {
        private static readonly PlaybackVariant[] All =
        [
            PlaybackVariant.Native,
            PlaybackVariant.Mpeg2H264Compatibility,
            PlaybackVariant.H264IdrNormalization,
        ];

        /// <summary>
        /// Returns the stable identifier of one variant of one channel.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="variant">The variant.</param>
        /// <returns>The identifier, without separators.</returns>
        public static string Create(string channelId, PlaybackVariant variant)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            return ("TVHeadEnd_Channel_" + channelId + "_" + variant.ToString())
                .GetMD5()
                .ToString("N", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns which variant an identifier names.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="mediaSourceId">The identifier Jellyfin passed back.</param>
        /// <returns>The variant, or <see langword="null"/> when it names none of them.</returns>
        public static PlaybackVariant? Resolve(string channelId, string? mediaSourceId)
        {
            if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(mediaSourceId))
            {
                return null;
            }

            foreach (var variant in All)
            {
                if (string.Equals(Create(channelId, variant), mediaSourceId, StringComparison.OrdinalIgnoreCase))
                {
                    return variant;
                }
            }

            return null;
        }
    }
}
