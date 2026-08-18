using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Streaming;

namespace TVHeadEnd.Recordings
{
    /// <summary>
    /// Describes a recording from a sample of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A thin adapter over <see cref="RecordingInspector"/> for the recordings path, which needs the
    /// same facts a channel does but fills in an existing <see cref="MediaSourceInfo"/> rather
    /// than building a channel descriptor. It exists so live and recordings share one analysis
    /// and one set of rules about what may be claimed.
    /// </para>
    /// <para>
    /// The two rules this plugin has broken in both paths in turn: stream order is never touched,
    /// and the runtime never comes from the sample.
    /// </para>
    /// </remarks>
    public sealed class RecordingDescriber
    {
        private readonly RecordingInspector _inspector;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingDescriber"/> class.
        /// </summary>
        /// <param name="mediaEncoder">The Jellyfin media encoder.</param>
        /// <param name="logger">The logger.</param>
        public RecordingDescriber(IMediaEncoder mediaEncoder, ILogger logger)
        {
            _inspector = new RecordingInspector(mediaEncoder, logger);
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
        public async Task<bool> DescribeFromSample(
            MediaSourceInfo target,
            string samplePath,
            string what,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(target);

            var inspected = await _inspector
                .Inspect(samplePath, what, target.Container ?? SourceContainer.TransportStream, cancellationToken)
                .ConfigureAwait(false);
            if (inspected is null)
            {
                return false;
            }

            // Verbatim, in analysis order: Jellyfin addresses streams by their position.
            target.MediaStreams = [.. inspected.Streams];
            target.Container = inspected.Container;
            target.Bitrate = inspected.Bitrate;
            target.Timestamp = inspected.Timestamp;
            target.VideoType = inspected.VideoType;
            target.Video3DFormat = inspected.Video3DFormat;

            // The full result is in hand, including real stream indices. Without this Jellyfin
            // replaces it with its own cached view, whose "-map" arguments land on wrong tracks.
            target.SupportsProbing = false;

            return true;
        }
    }
}
