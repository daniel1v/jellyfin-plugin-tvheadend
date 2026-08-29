using System.Threading;
using System.Threading.Tasks;

namespace TVHeadEnd.Recordings
{
    /// <summary>
    /// Answers what a recording contains.
    /// </summary>
    /// <remarks>
    /// One method, one implementation -- <see cref="RecordingAnalysisService"/>. It exists so that
    /// the request filter, which decides what to do with the answer, can be exercised without a
    /// TVHeadend server on the other end of it. Everything else takes the service itself.
    /// </remarks>
    public interface IRecordingAnalyser
    {
        /// <summary>
        /// What a sample of one recording contains.
        /// </summary>
        /// <param name="recordingId">The TVHeadend recording identifier.</param>
        /// <param name="recordingHasFinished">
        /// Whether the recording is complete, and its analysis therefore true for good.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The analysis, which describes nothing when the recording could not be read.</returns>
        Task<RecordingAnalysis> AnalyseAsync(
            string recordingId,
            bool recordingHasFinished,
            CancellationToken cancellationToken);
    }
}
