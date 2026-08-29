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
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingDescriber"/> class.
        /// </summary>
        /// <param name="mediaEncoder">The Jellyfin media encoder.</param>
        /// <param name="logger">The logger.</param>
        public RecordingDescriber(IMediaEncoder mediaEncoder, ILogger logger)
        {
            _inspector = new RecordingInspector(mediaEncoder, logger);
            _logger = logger;
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

            // What the broadcast said about its own audio, which FFprobe does not read. A
            // recording made with the pass profile carries the same program map a live channel
            // does, so the two paths describe the same tracks the same way. After the container
            // is settled, because whether there is a program map to read depends on it.
            ApplyBroadcastAudioFacts(target, samplePath);

            // The full result is in hand, including real stream indices. Without this Jellyfin
            // replaces it with its own cached view, whose "-map" arguments land on wrong tracks.
            target.SupportsProbing = false;

            return true;
        }

        /// <summary>
        /// Reads the recording's own program map and marks its audio tracks accordingly.
        /// </summary>
        /// <remarks>
        /// Failure is not an error here. A recording in a container that is not MPEG-TS has no
        /// program map, an opening that never carried a complete pair of tables has none to find,
        /// and an unreadable sample is one the analysis has already finished with. In every one
        /// of those the probe's own account of the streams stands, unaltered.
        /// </remarks>
        /// <param name="target">The media source being described.</param>
        /// <param name="samplePath">The local sample the description came from.</param>
        private void ApplyBroadcastAudioFacts(MediaSourceInfo target, string samplePath)
        {
            // The one spelling this plugin normalises every transport stream to.
            if (!string.Equals(target.Container, SourceContainer.TransportStream, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                BroadcastAudioFacts.Apply(target.MediaStreams, RecordedProgramMap.ReadFrom(samplePath));
            }
            catch (IOException exception)
            {
                _logger.LogDebug(exception, "The recording sample could not be read for its program map");
            }
            catch (UnauthorizedAccessException exception)
            {
                _logger.LogDebug(exception, "The recording sample could not be read for its program map");
            }
        }
    }
}
