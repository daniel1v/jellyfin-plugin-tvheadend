using System;

namespace TVHeadEnd.Recordings
{
    /// <summary>
    /// Looks for an H.264 IDR frame in the payload of a video elementary stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ever valid for <see cref="StreamType"/>. The scan matches the byte pattern
    /// <c>00 00 01 X</c> where <c>X &amp; 0x1F == 5</c>, which is an IDR NAL header in H.264 --
    /// and, in MPEG-2 video, the start code of slice 5. Handing this an MPEG-2 stream therefore
    /// reports an IDR in a stream that has no NAL units at all, which is why the codec gate
    /// lives in <see cref="VideoRandomAccessProbe"/> and this class is never constructed
    /// without it.
    /// </para>
    /// <para>
    /// Start codes are found across payload boundaries: the last three bytes of each payload
    /// are carried into the next, so an IDR header split over two packets is still seen.
    /// </para>
    /// </remarks>
    internal sealed class H264RandomAccessAnalyzer
    {
        /// <summary>
        /// The PMT stream type this analyzer is valid for.
        /// </summary>
        public const byte StreamType = 0x1B;

        private const int StartCodeLength = 4;
        private const int CarryLength = StartCodeLength - 1;

        private readonly byte[] _carry = new byte[CarryLength];

        private int _carryLength;

        /// <summary>
        /// Gets a value indicating whether an IDR frame has been seen.
        /// </summary>
        public bool HasSeenIdrFrame { get; private set; }

        /// <summary>
        /// Gets how many bytes of video payload have been inspected. Zero means the question
        /// has not been asked of anything yet, and no conclusion may be drawn.
        /// </summary>
        public long BytesInspected { get; private set; }

        /// <summary>
        /// Reports whether <paramref name="data"/> contains an H.264 IDR NAL header.
        /// </summary>
        /// <param name="data">Elementary stream bytes.</param>
        /// <returns>Whether an IDR NAL unit starts within the span.</returns>
        public static bool ContainsIdrNalUnit(ReadOnlySpan<byte> data)
        {
            for (var offset = 0; offset + 3 < data.Length; offset++)
            {
                if (data[offset] == 0x00
                    && data[offset + 1] == 0x00
                    && data[offset + 2] == 0x01
                    && (data[offset + 3] & 0x1F) == 5)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Inspects the next stretch of video payload.
        /// </summary>
        /// <param name="payload">The payload of one video packet.</param>
        public void Inspect(ReadOnlySpan<byte> payload)
        {
            if (payload.IsEmpty)
            {
                return;
            }

            BytesInspected += payload.Length;
            if (HasSeenIdrFrame)
            {
                return;
            }

            if (_carryLength > 0)
            {
                Span<byte> seam = stackalloc byte[CarryLength + StartCodeLength];
                var taken = Math.Min(StartCodeLength, payload.Length);
                _carry.AsSpan(0, _carryLength).CopyTo(seam);
                payload[..taken].CopyTo(seam[_carryLength..]);
                if (ContainsIdrNalUnit(seam[..(_carryLength + taken)]))
                {
                    HasSeenIdrFrame = true;
                    return;
                }
            }

            if (ContainsIdrNalUnit(payload))
            {
                HasSeenIdrFrame = true;
                return;
            }

            var kept = Math.Min(CarryLength, payload.Length);
            payload[^kept..].CopyTo(_carry);
            _carryLength = kept;
        }
    }
}
