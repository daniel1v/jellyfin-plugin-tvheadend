using TVHeadEnd.Streaming;

namespace TVHeadEnd.Recordings
{
    /// <summary>
    /// What a sample of a recording turned out to contain. Facts, and only facts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately says nothing about what should be done with a recording. It does not know
    /// which client is asking, whether direct play is on offer or whether anything needs
    /// re-encoding: those are decisions, they depend on the request, and a decision cached under
    /// a recording's identifier would be handed to the next viewer along with the facts.
    /// </para>
    /// <para>
    /// Everything here is about the sample that was fetched, not about the whole recording.
    /// </para>
    /// </remarks>
    /// <param name="Media">What the probe found, or <see langword="null"/> when the sample yielded nothing usable.</param>
    /// <param name="ProgramMap">The broadcast's own account of its streams, if the sample carried one.</param>
    /// <param name="EntryPointEvidence">What the H.264 access points in the sample open on.</param>
    public sealed record RecordingAnalysis(
        InspectedMedia? Media,
        ProgramMapTable? ProgramMap,
        H264EntryPointEvidence EntryPointEvidence)
    {
        /// <summary>
        /// Gets the analysis of a recording that could not be analysed at all.
        /// </summary>
        public static RecordingAnalysis Nothing { get; } =
            new(null, null, H264EntryPointEvidence.Insufficient);

        /// <summary>
        /// Gets a value indicating whether this analysis describes the recording.
        /// </summary>
        public bool DescribesTheRecording => Media is not null;
    }
}
