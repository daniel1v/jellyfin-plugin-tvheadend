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
    /// Establishes what a recording contains, from a local sample of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recordings only. A live channel is described from its PAT and PMT and is never probed --
    /// see the architecture notes -- so nothing on the live path reaches this type.
    /// </para>
    /// <para>
    /// The remote recording is not analysed where it lives; the sample is its opening, fetched by
    /// a range request and written to a local file, and Jellyfin's <c>IMediaEncoder</c> (FFprobe)
    /// is pointed at that. Analysing the remote stream directly is slow: TVHeadend answers range
    /// requests but does not advertise Accept-Ranges, so FFmpeg reads a recording end to end.
    /// </para>
    /// <para>
    /// This reports facts and nothing else. It does not pick a default audio track, does not
    /// decide about deinterlacing and never derives a runtime: the sample is a slice of the
    /// opening, and its duration says nothing about the recording. Every audio and subtitle
    /// stream is preserved in the order FFprobe reported, because Jellyfin addresses them by
    /// position.
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
