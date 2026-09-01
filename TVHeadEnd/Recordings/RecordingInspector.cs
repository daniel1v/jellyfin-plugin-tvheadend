using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Compatibility.Jellyfin12;

namespace TVHeadEnd.Recordings
{
    /// <summary>
    /// Establishes what a source contains, from a local sample of it.
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
    /// This reports facts and nothing else. It does not pick a default audio track, does not
    /// decide about deinterlacing and never invents a runtime: the sample is a slice of a
    /// recording or a ring buffer holding the last few minutes of a channel, and its duration
    /// says nothing about the source. Every audio and subtitle stream is preserved in the order
    /// FFprobe reported, because Jellyfin addresses them by position.
    /// </para>
    /// </remarks>
    public sealed class RecordingInspector
    {
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingInspector"/> class.
        /// </summary>
        /// <param name="mediaEncoder">The Jellyfin media encoder.</param>
        /// <param name="logger">The logger.</param>
        public RecordingInspector(IMediaEncoder mediaEncoder, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(mediaEncoder);
            ArgumentNullException.ThrowIfNull(logger);

            _mediaEncoder = mediaEncoder;
            _logger = logger;
        }

        /// <summary>
        /// Describes the stream a sample was taken from.
        /// </summary>
        /// <param name="samplePath">A local file holding the opening of the stream.</param>
        /// <param name="what">What is being described, for the log.</param>
        /// <param name="fallbackContainer">The container to report if the analysis names none.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>What the sample contains, or <see langword="null"/> when it yielded nothing usable.</returns>
        public async Task<InspectedMedia?> Inspect(
            string samplePath,
            string what,
            string fallbackContainer,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(samplePath);

            var stopwatch = Stopwatch.StartNew();
            var info = await _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaType = DlnaProfileType.Video,
                    MediaSource = new MediaSourceInfo { Path = samplePath, Protocol = MediaProtocol.File },
                    ExtractChapters = false,
                },
                cancellationToken).ConfigureAwait(false);

            var streams = info?.MediaStreams;
            if (info is null || streams is null || streams.Count == 0)
            {
                _logger.LogWarning(
                    "TVHeadend media inspection: {What} yielded no streams after {ElapsedMilliseconds} ms",
                    what,
                    stopwatch.ElapsedMilliseconds);
                return null;
            }

            var inspected = new InspectedMedia(
                JellyfinContainerNames.Describe(info.Container, fallbackContainer),
                streams,
                info.Bitrate,
                info.Timestamp,
                info.VideoType,
                info.Video3DFormat);

            var video = inspected.Video;
            _logger.LogInformation(
                "TVHeadend media inspection: {What} took {ElapsedMilliseconds} ms -- {Container}, {StreamCount} streams ({Streams}); video {Codec} {Width}x{Height} {Profile}@{Level} interlaced={Interlaced}",
                what,
                stopwatch.ElapsedMilliseconds,
                inspected.Container,
                streams.Count,
                string.Join(", ", streams.Select(stream => $"{stream.Index}:{stream.Type.ToString().ToLowerInvariant()}/{stream.Codec}")),
                video?.Codec,
                video?.Width,
                video?.Height,
                video?.Profile,
                video?.Level,
                video?.IsInterlaced);

            return inspected.IsUsable ? inspected : null;
        }
    }
}
