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
        private const int ScanChunkLength = 65536;

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
        /// Reports how the video of a sample offers a decoder a place to start.
        /// </summary>
        /// <remarks>
        /// The same question the live path asks, answered by the same scanner, because it is a
        /// property of the broadcast and not of how it is delivered. The scan is bounded by the
        /// sample, and the H.264 analysis only ever runs for H.264: an earlier version fed MPEG-2
        /// into it, where the slice start code <c>00 00 01 05</c> satisfies the IDR pattern by
        /// coincidence.
        /// </remarks>
        /// <param name="samplePath">A local file holding the opening of the stream.</param>
        /// <returns>How the video offers random access.</returns>
        public static H264RandomAccessKind ScanRandomAccess(string samplePath)
        {
            ArgumentException.ThrowIfNullOrEmpty(samplePath);

            // The probe lives here rather than inside the conditioner: this is the only remaining
            // caller, and it is a recording concern. The conditioner does the transport work and
            // names the video PID; the video payload is handed on from the output it produces.
            var probe = new VideoRandomAccessProbe();
            var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
            var buffer = ArrayPool<byte>.Shared.Rent(ScanChunkLength);
            var conditioned = ArrayPool<byte>.Shared.Rent(
                TransportStreamConditioner.GetMaximumConditionedLength(ScanChunkLength));

            try
            {
                using var sample = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var first = true;

                int read;
                while ((read = sample.Read(buffer, 0, ScanChunkLength)) > 0)
                {
                    if (first)
                    {
                        first = false;
                        if (!SourceContainer.IsTransportStream(buffer.AsSpan(0, read)))
                        {
                            return H264RandomAccessKind.NotApplicable;
                        }
                    }

                    var length = conditioner.Condition(buffer.AsSpan(0, read), conditioned);
                    Inspect(conditioner, probe, conditioned.AsSpan(0, length));
                    if (probe.Kind == H264RandomAccessKind.Idr)
                    {
                        return H264RandomAccessKind.Idr;
                    }
                }

                // The sample ran out. For H.264 that is a real answer -- nothing in it offered an
                // IDR -- and for anything else the question never applied.
                return probe.VideoStreamType == H264RandomAccessAnalyzer.StreamType && probe.HasInspectedVideo
                    ? H264RandomAccessKind.RecoveryOpenGop
                    : H264RandomAccessKind.NotApplicable;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                ArrayPool<byte>.Shared.Return(conditioned);
            }
        }

        /// <summary>
        /// Hands the video payload of a conditioned run of packets to the probe.
        /// </summary>
        private static void Inspect(
            TransportStreamConditioner conditioner,
            VideoRandomAccessProbe probe,
            ReadOnlySpan<byte> conditioned)
        {
            if (conditioner.VideoPid <= 0)
            {
                return;
            }

            probe.SetVideoStreamType(conditioner.VideoStreamType);

            for (var offset = 0; offset + TransportStreamPacket.Length <= conditioned.Length; offset += TransportStreamPacket.Length)
            {
                var packet = conditioned.Slice(offset, TransportStreamPacket.Length);
                if (TransportStreamPacket.ReadPid(packet) == conditioner.VideoPid)
                {
                    probe.Observe(TransportStreamPacket.ReadPayload(packet));
                }
            }

            probe.Evaluate();
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
