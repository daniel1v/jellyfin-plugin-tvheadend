using TVHeadEnd.Streaming;

namespace TVHeadEnd.Media
{
    /// <summary>
    /// What was observed about a stream while it was being received.
    /// </summary>
    /// <param name="IsTransportStream">Whether the source arrived as an MPEG transport stream.</param>
    /// <param name="ProgramSignature">The PMT fingerprint, or <see langword="null"/> when none was parsed.</param>
    /// <param name="VideoStreamType">The PMT stream type of the video, or zero.</param>
    public readonly record struct TransportObservation(
        bool IsTransportStream,
        string? ProgramSignature,
        byte VideoStreamType)
    {
        /// <summary>
        /// Reads the observation out of a conditioner.
        /// </summary>
        /// <param name="conditioner">The conditioner the stream passed through.</param>
        /// <param name="isTransportStream">Whether the source was a transport stream at all.</param>
        /// <returns>The observation.</returns>
        public static TransportObservation From(
            TransportStreamConditioner? conditioner,
            bool isTransportStream)
        {
            if (!isTransportStream || conditioner is null)
            {
                return new TransportObservation(false, null, 0);
            }

            return new TransportObservation(
                true,
                conditioner.ProgramLayout,
                conditioner.VideoStreamType);
        }
    }
}
