using System;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// What a TVHeadend source actually is, and what to call it when telling a client.
    /// </summary>
    /// <remarks>
    /// Neither the live stream nor a recording is guaranteed to be MPEG-TS. The container of a
    /// live stream follows the streaming profile of the TVHeadend access entry, and that of a
    /// recording follows the DVR profile; a server configured for one of the WebTV profiles
    /// serves Matroska. Both settings live on the TVHeadend server, out of this plugin's reach,
    /// so the format has to be established rather than assumed.
    /// </remarks>
    internal static class SourceContainer
    {
        /// <summary>
        /// The two spellings of the MPEG-TS container, reported together.
        /// </summary>
        /// <remarks>
        /// FFprobe calls it <c>mpegts</c> and Jellyfin's <c>ProbeResultNormalizer</c> rewrites
        /// that to <c>ts</c>, but client device profiles are split over both spellings: Jellyfin
        /// for Android only ever lists <c>mpegts</c>. <c>ContainerHelper.ContainsContainer</c>
        /// compares the two sides for exact equality without knowing they are the same container,
        /// and it splits the reported value on commas, so naming both is what lets either kind of
        /// profile match and direct play at all. No other container is known to need this, so no
        /// other one is rewritten.
        /// </remarks>
        public const string TransportStream = "mpegts,ts";

        private const int TransportStreamPacketLength = 188;
        private const byte SyncByte = 0x47;

        /// <summary>
        /// The number of consecutive packet boundaries that must carry a sync byte before a
        /// prefix counts as a transport stream. One sync byte proves nothing -- 0x47 is the
        /// letter 'G' and turns up in any binary -- but a run of them at exactly 188 bytes
        /// apart does not happen by chance.
        /// </summary>
        private const int RequiredConsecutiveSyncBytes = 4;

        /// <summary>
        /// Establishes whether the opening bytes of a source are an MPEG-TS stream.
        /// </summary>
        /// <param name="prefix">The first bytes received from the source.</param>
        /// <returns><see langword="true"/> if the prefix is a transport stream.</returns>
        public static bool IsTransportStream(ReadOnlySpan<byte> prefix)
        {
            // The stream need not begin on a packet boundary, so every offset within one packet
            // is a candidate for where the first packet starts.
            for (var start = 0; start < TransportStreamPacketLength && start < prefix.Length; start++)
            {
                if (prefix[start] != SyncByte)
                {
                    continue;
                }

                var confirmed = 1;
                for (var offset = start + TransportStreamPacketLength;
                     offset < prefix.Length && confirmed < RequiredConsecutiveSyncBytes;
                     offset += TransportStreamPacketLength)
                {
                    if (prefix[offset] != SyncByte)
                    {
                        confirmed = 0;
                        break;
                    }

                    confirmed++;
                }

                if (confirmed >= RequiredConsecutiveSyncBytes)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets the container to report to clients for what an analysis found.
        /// </summary>
        /// <param name="probedContainer">The container FFprobe reported, if any.</param>
        /// <param name="fallback">What to keep when the analysis said nothing.</param>
        /// <returns>The container to report.</returns>
        public static string Describe(string? probedContainer, string fallback)
        {
            if (string.IsNullOrEmpty(probedContainer))
            {
                return fallback;
            }

            return IsTransportStreamName(probedContainer) ? TransportStream : probedContainer;
        }

        private static bool IsTransportStreamName(string container)
            => container.Equals("mpegts", StringComparison.OrdinalIgnoreCase)
                || container.Equals("ts", StringComparison.OrdinalIgnoreCase);
    }
}
