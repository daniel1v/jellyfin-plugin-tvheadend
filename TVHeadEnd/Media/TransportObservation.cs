using TVHeadEnd.Streaming;

namespace TVHeadEnd.Media
{
    /// <summary>
    /// What was observed about a stream while it was being received.
    /// </summary>
    /// <param name="IsTransportStream">Whether the source arrived as an MPEG transport stream.</param>
    /// <param name="ProgramSignature">The PMT fingerprint, or <see langword="null"/> when none was parsed.</param>
    /// <param name="VideoStreamType">The PMT stream type of the video, or zero.</param>
    /// <param name="RandomAccess">How the video offers a decoder a place to start.</param>
    public readonly record struct TransportObservation(
        bool IsTransportStream,
        string? ProgramSignature,
        byte VideoStreamType,
        H264RandomAccessKind RandomAccess)
    {
        /// <summary>
        /// Reads the observation out of a conditioner and the probe it fed.
        /// </summary>
        /// <param name="conditioner">The conditioner the stream passed through.</param>
        /// <param name="probe">The probe it fed, or <see langword="null"/>.</param>
        /// <param name="isTransportStream">Whether the source was a transport stream at all.</param>
        /// <returns>The observation.</returns>
        public static TransportObservation From(
            TransportStreamConditioner? conditioner,
            VideoRandomAccessProbe? probe,
            bool isTransportStream)
        {
            if (!isTransportStream || conditioner is null)
            {
                return new TransportObservation(false, null, 0, H264RandomAccessKind.NotApplicable);
            }

            return new TransportObservation(
                true,
                conditioner.ProgramLayout,
                conditioner.VideoStreamType,
                probe?.Kind ?? H264RandomAccessKind.Unknown);
        }
    }
}
