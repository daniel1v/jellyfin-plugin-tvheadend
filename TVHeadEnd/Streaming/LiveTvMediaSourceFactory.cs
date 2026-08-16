using System;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Creates the media source descriptions used before and after a live TV stream is opened.
    /// </summary>
    internal static class LiveTvMediaSourceFactory
    {
        /// <summary>
        /// What a channel is assumed to be before any of it has been received. Nearly every
        /// TVHeadend server streams MPEG-TS, so this is the useful guess -- but it is only a
        /// guess, and <see cref="SourceContainer.Describe"/> replaces it with what the analysis
        /// of the buffer actually found.
        /// </summary>
        public const string Container = SourceContainer.TransportStream;

        private const int AnalyzeDurationMs = 2000;

        /// <summary>
        /// Creates the ticket-free source returned during playback negotiation.
        /// </summary>
        /// <param name="mediaSourceId">The internal Jellyfin live TV channel identifier.</param>
        /// <returns>An unopened live TV media source.</returns>
        public static MediaSourceInfo CreatePending(string mediaSourceId)
        {
            ArgumentException.ThrowIfNullOrEmpty(mediaSourceId);

            return Create(mediaSourceId, null, true, false, false);
        }

        /// <summary>
        /// Creates the source backed by a current TVHeadend access ticket.
        /// </summary>
        /// <param name="mediaSourceId">The stable Jellyfin media source identifier used during playback negotiation.</param>
        /// <param name="path">The authenticated TVHeadend stream URL.</param>
        /// <returns>An opened live TV media source.</returns>
        public static MediaSourceInfo CreateOpened(string mediaSourceId, string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(mediaSourceId);
            ArgumentException.ThrowIfNullOrEmpty(path);

            return Create(mediaSourceId, path, false, true, true);
        }

        /// <summary>
        /// Marks the audio track that the widest range of clients can decode, so that a
        /// broadcast carrying MPEG audio alongside a Dolby track does not end up on a
        /// device without an MP2 decoder.
        /// </summary>
        /// <remarks>
        /// The order of <see cref="MediaSourceInfo.MediaStreams"/> is deliberately left
        /// alone. Jellyfin's <c>EncodingHelper.GetMapArgs</c> addresses the stream it wants
        /// FFmpeg to copy by its position in this list, so the list has to stay in the order
        /// FFprobe reported. Reordering it makes <c>-map</c> point at a different track than
        /// the one described in the manifest, which surfaces on the client as a decoder
        /// failure for a codec the manifest never advertised.
        /// </remarks>
        /// <param name="mediaSource">The probed live TV media source.</param>
        internal static void PreferCompatibleAudioTrack(MediaSourceInfo mediaSource)
        {
            ArgumentNullException.ThrowIfNull(mediaSource);

            var audioStreams = mediaSource.MediaStreams
                .Where(stream => stream.Type == MediaStreamType.Audio)
                .ToList();
            if (audioStreams.Count == 0)
            {
                return;
            }

            string[] preferredCodecs = ["aac", "ac3", "eac3", "mp3"];
            var preferredAudio = preferredCodecs
                .Select(codec => audioStreams.FirstOrDefault(stream => string.Equals(stream.Codec, codec, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(stream => stream is not null)
                ?? audioStreams.FirstOrDefault(stream => stream.IsDefault)
                ?? audioStreams[0];

            foreach (var stream in audioStreams)
            {
                stream.IsDefault = ReferenceEquals(stream, preferredAudio);
            }

            mediaSource.DefaultAudioStreamIndex = preferredAudio.Index;
        }

        private static MediaSourceInfo Create(
            string mediaSourceId,
            string? path,
            bool requiresOpening,
            bool requiresClosing,
            bool supportsProbing)
        {
            return new MediaSourceInfo
            {
                Id = mediaSourceId,
                Path = path,
                Protocol = MediaProtocol.Http,
                AnalyzeDurationMs = AnalyzeDurationMs,
                Container = Container,
                IsInfiniteStream = true,
                RequiresOpening = requiresOpening,
                RequiresClosing = requiresClosing,
                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = supportsProbing,
                MediaStreams = [],
            };
        }
    }
}
