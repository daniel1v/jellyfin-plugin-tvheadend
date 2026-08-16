using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Fills in what a TVHeadend source contains, from a local sample of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A live channel and a recording pose the same question and are answered the same way. The
    /// remote source is never analysed: for a channel the sample is the shared buffer, for a
    /// recording it is the opening fetched by a range request. Analysing the source itself is
    /// both slow -- TVHeadend answers range requests but does not advertise Accept-Ranges, so
    /// FFmpeg reads a recording from end to end -- and, for a channel, a second subscription.
    /// </para>
    /// <para>
    /// The two rules that follow are the ones this plugin has broken before, in both paths, and
    /// they are why the description belongs in one place: stream order is never touched, and the
    /// runtime never comes from the sample.
    /// </para>
    /// </remarks>
    internal sealed class SourceDescriber
    {
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILogger _logger;

        internal SourceDescriber(IMediaEncoder mediaEncoder, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(mediaEncoder);
            ArgumentNullException.ThrowIfNull(logger);

            _mediaEncoder = mediaEncoder;
            _logger = logger;
        }

        /// <summary>
        /// Marks the audio track that the widest range of clients can decode, so that a broadcast
        /// carrying MPEG audio alongside a Dolby track does not end up on a device without an MP2
        /// decoder.
        /// </summary>
        /// <remarks>
        /// The order of <see cref="MediaSourceInfo.MediaStreams"/> is deliberately left alone.
        /// Jellyfin's <c>EncodingHelper.GetMapArgs</c> addresses the stream it wants FFmpeg to
        /// copy by its position in this list, so the list has to stay in the order FFprobe
        /// reported. Reordering it makes <c>-map</c> point at a different track than the one
        /// described in the manifest, which surfaces on the client as a decoder failure for a
        /// codec the manifest never advertised.
        /// </remarks>
        /// <param name="mediaSource">The described media source.</param>
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

        /// <summary>
        /// Describes <paramref name="target"/> from a local sample of the stream it stands for.
        /// </summary>
        /// <param name="target">The media source to fill in.</param>
        /// <param name="samplePath">A local file holding the opening of the stream.</param>
        /// <param name="what">What is being described, for the log.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// <see langword="true"/> when the sample yielded streams. On <see langword="false"/> the
        /// target is untouched, so the caller keeps whatever it had; a source without streams
        /// must never reach Jellyfin, which dereferences the video stream while preparing
        /// playback and throws before any fallback could take effect.
        /// </returns>
        internal async Task<bool> DescribeFromSample(
            MediaSourceInfo target,
            string samplePath,
            string what,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentException.ThrowIfNullOrEmpty(samplePath);

            var stopwatch = Stopwatch.StartNew();
            var info = await _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaType = MediaBrowser.Model.Dlna.DlnaProfileType.Video,
                    MediaSource = new MediaSourceInfo { Path = samplePath, Protocol = MediaProtocol.File },
                    ExtractChapters = false,
                },
                cancellationToken).ConfigureAwait(false);

            var streams = info?.MediaStreams;
            if (info is null || streams is null || streams.Count == 0)
            {
                _logger.LogWarning(
                    "TVHeadend source description: {What} yielded no streams after {ElapsedMilliseconds} ms",
                    what,
                    stopwatch.ElapsedMilliseconds);
                return false;
            }

            // Verbatim, in the order FFprobe reported. See PreferCompatibleAudioTrack.
            target.MediaStreams = streams;

            target.Container = SourceContainer.Describe(info.Container, target.Container);
            target.Bitrate = info.Bitrate;
            target.Timestamp = info.Timestamp;
            target.Video3DFormat = info.Video3DFormat;
            target.VideoType = info.VideoType;

            // Deliberately not taken from the analysis. It describes the sample -- a slice of a
            // recording, or a ring buffer holding the last few minutes of a channel -- and never
            // the source. A caller that knows the real runtime sets it; one that has none, such
            // as live TV, leaves it empty.

            // The full result is in hand, including real stream indices. Without this Jellyfin
            // replaces it with its own cached live TV view: first video, first audio, indices
            // unknown -- which is exactly the description that makes its "-map" arguments land
            // on the wrong tracks.
            target.SupportsProbing = false;

            PreferCompatibleAudioTrack(target);

            var video = streams.FirstOrDefault(stream => stream.Type == MediaStreamType.Video);
            _logger.LogInformation(
                "TVHeadend source description: {What} took {ElapsedMilliseconds} ms -- {Container}, {StreamCount} streams ({Streams}); video {Width}x{Height} {Profile}@{Level} {FrameRate}fps",
                what,
                stopwatch.ElapsedMilliseconds,
                target.Container,
                streams.Count,
                Summarise(streams),
                video?.Width,
                video?.Height,
                video?.Profile,
                video?.Level,
                video?.RealFrameRate);

            return true;
        }

        private static string Summarise(IReadOnlyList<MediaStream> streams)
            => string.Join(", ", streams.Select(stream => $"{stream.Index}:{stream.Codec}{(stream.IsDefault ? "*" : string.Empty)}"));
    }
}
