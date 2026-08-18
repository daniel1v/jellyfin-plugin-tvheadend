using System;
using System.Globalization;
using MediaBrowser.Common.Extensions;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.Playback
{
    /// <summary>
    /// The stable identifier of one form of one channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jellyfin distinguishes the sources it is offered by identifier, remembers which one a
    /// client chose, and asks for that one again when it opens the stream. So the identifier has
    /// to name both the channel and the role, and it has to be the same on every negotiation.
    /// </para>
    /// <para>
    /// Deliberately never equal to the channel's own item identifier. Jellyfin builds the live
    /// stream open token out of the item identifier and the source identifier, and it sorts a
    /// source carrying the item identifier ahead of the others -- which would settle the choice
    /// before the device profile was ever consulted.
    /// </para>
    /// </remarks>
    public static class ChannelSourceId
    {
        private static readonly StreamProfileRole[] All =
        [
            StreamProfileRole.Native,
            StreamProfileRole.Mpeg2H264Compatibility,
        ];

        /// <summary>
        /// Returns the stable identifier of one role of one channel.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="role">The role.</param>
        /// <returns>The identifier, without separators.</returns>
        public static string Create(string channelId, StreamProfileRole role)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            return ("TVHeadEnd_Channel_" + channelId + "_" + role.ToString())
                .GetMD5()
                .ToString("N", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns which role an identifier names.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="mediaSourceId">The identifier Jellyfin passed back.</param>
        /// <returns>The role, or <see langword="null"/> when it names none of them.</returns>
        public static StreamProfileRole? Resolve(string channelId, string? mediaSourceId)
        {
            if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(mediaSourceId))
            {
                return null;
            }

            foreach (var role in All)
            {
                if (string.Equals(Create(channelId, role), mediaSourceId, StringComparison.OrdinalIgnoreCase))
                {
                    return role;
                }
            }

            return null;
        }
    }
}
