namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// How the video of a source offers a decoder a place to start.
    /// </summary>
    /// <remarks>
    /// A property of the coded video, established by observation and nothing else. It is
    /// deliberately not a statement about what should be done: which clients can cope with
    /// <see cref="RecoveryOpenGop"/> and what to serve them instead is playback policy.
    /// </remarks>
    public enum H264RandomAccessKind
    {
        /// <summary>
        /// Not enough of the stream has been seen to say.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// The video carries IDR frames, so any decoder can start at one.
        /// </summary>
        Idr = 1,

        /// <summary>
        /// The video signals random access with recovery points and I-frames but sends no IDR.
        /// Conformant, and FFmpeg starts on it, but the access point is an open GOP whose
        /// leading pictures reference frames from before it, and some device decoders consume
        /// it without ever emitting a frame.
        /// </summary>
        RecoveryOpenGop = 2,

        /// <summary>
        /// The question does not apply -- the video is not H.264, or the source is not a
        /// transport stream. Deliberately distinct from <see cref="Idr"/>: nothing was
        /// established, so nothing may be concluded.
        /// </summary>
        NotApplicable = 3,
    }
}
