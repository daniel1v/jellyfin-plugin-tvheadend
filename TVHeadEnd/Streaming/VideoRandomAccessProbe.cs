using System;
using System.Diagnostics;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Decides how the video of a transport stream offers random access, choosing the analysis
    /// by the codec the PMT announces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the codec gate. Only H.264 is analysed for IDR frames; MPEG-2 (<c>0x02</c>) and
    /// HEVC (<c>0x24</c>) are reported as <see cref="H264RandomAccessKind.NotApplicable"/> without
    /// ever reaching the H.264 scanner, whose byte pattern they would satisfy by coincidence.
    /// </para>
    /// <para>
    /// The verdict is taken on elapsed time rather than on bytes scanned. Measured across
    /// twelve services, those that send IDRs send the first within 219 to 503 ms and repeat
    /// roughly every second, and those that do not send none at all -- nothing falls in
    /// between. A byte budget would make low bitrate channels wait longest, which is the wrong
    /// way round.
    /// </para>
    /// </remarks>
    public sealed class VideoRandomAccessProbe
    {
        private static readonly TimeSpan DecisionTimeLimit = TimeSpan.FromSeconds(2);

        private readonly H264RandomAccessAnalyzer _h264 = new();

        private long _firstPayloadTimestamp;

        /// <summary>
        /// Gets the PMT stream type of the video, or zero while no PMT has been parsed.
        /// </summary>
        public byte VideoStreamType { get; private set; }

        /// <summary>
        /// Gets the verdict reached so far.
        /// </summary>
        public H264RandomAccessKind Kind { get; private set; } = H264RandomAccessKind.Unknown;

        /// <summary>
        /// Gets a value indicating whether any video payload has been inspected. Without this
        /// a verdict of <see cref="H264RandomAccessKind.RecoveryOpenGop"/> would rest on silence.
        /// </summary>
        public bool HasInspectedVideo => _h264.BytesInspected > 0;

        /// <summary>
        /// Records the video codec the PMT announced.
        /// </summary>
        /// <param name="streamType">The PMT stream type of the video elementary stream.</param>
        public void SetVideoStreamType(byte streamType)
        {
            if (VideoStreamType == streamType)
            {
                return;
            }

            VideoStreamType = streamType;
            if (streamType != H264RandomAccessAnalyzer.StreamType)
            {
                // Nothing here can be established by inspecting bytes, and guessing is what
                // produced a re-encode verdict for MPEG-2 in the first place.
                Kind = H264RandomAccessKind.NotApplicable;
            }
        }

        /// <summary>
        /// Inspects the payload of one video packet.
        /// </summary>
        /// <param name="payload">The packet payload.</param>
        public void Observe(ReadOnlySpan<byte> payload)
        {
            if (VideoStreamType != H264RandomAccessAnalyzer.StreamType || Kind == H264RandomAccessKind.Idr)
            {
                return;
            }

            if (_firstPayloadTimestamp == 0)
            {
                _firstPayloadTimestamp = Stopwatch.GetTimestamp();
            }

            _h264.Inspect(payload);
            if (_h264.HasSeenIdrFrame)
            {
                Kind = H264RandomAccessKind.Idr;
            }
        }

        /// <summary>
        /// Takes the verdict if enough of the stream has been seen.
        /// </summary>
        /// <returns>The verdict, which stays <see cref="H264RandomAccessKind.Unknown"/> until it can be taken.</returns>
        public H264RandomAccessKind Evaluate()
        {
            if (Kind != H264RandomAccessKind.Unknown)
            {
                return Kind;
            }

            if (HasInspectedVideo && Stopwatch.GetElapsedTime(_firstPayloadTimestamp) >= DecisionTimeLimit)
            {
                Kind = H264RandomAccessKind.RecoveryOpenGop;
            }

            return Kind;
        }
    }
}
