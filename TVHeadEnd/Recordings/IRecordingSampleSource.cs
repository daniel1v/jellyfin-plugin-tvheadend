using System.Threading;
using System.Threading.Tasks;

namespace TVHeadEnd.Recordings
{
    /// <summary>
    /// Gets the opening of a recording onto local disk, where it can be analysed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One capability, not a layer. Everything about <em>reaching</em> a recording -- which server
    /// holds it, how to authenticate, that only the first few megabytes are wanted and that the
    /// limit has to be enforced while reading because a server may ignore the range -- lives
    /// behind this. What is left in front of it is deciding when an analysis is worth running and
    /// who gets to share it, which is a question about time and callers rather than about HTTP.
    /// </para>
    /// <para>
    /// The caller owns what comes back and disposes it. That is the whole reason a
    /// <see cref="RecordingSample"/> is returned rather than a path.
    /// </para>
    /// </remarks>
    public interface IRecordingSampleSource
    {
        /// <summary>
        /// Fetches the opening of one recording.
        /// </summary>
        /// <param name="recordingId">The TVHeadend recording identifier.</param>
        /// <param name="cancellationToken">
        /// The lifetime of the fetch. This is the shared operation's own token, never one caller's.
        /// </param>
        /// <returns>The sample, which the caller owns and must dispose.</returns>
        Task<RecordingSample> FetchAsync(string recordingId, CancellationToken cancellationToken);
    }
}
