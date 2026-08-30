namespace TVHeadEnd.Core.Dvr
{
    /// <summary>
    /// The state TVHeadend reports for a DVR entry.
    /// </summary>
    /// <remarks>
    /// These are the values of the HTSP <c>state</c> field and nothing more. TVHeadend has no
    /// cancelled or failed state: cancelling removes the entry, and a failure shows as
    /// <see cref="Missed"/> or through the separate <c>error</c> field, which
    /// <see cref="DvrEntry.Error"/> carries. Modelling states the server never sends would
    /// invent a distinction that could not be filled.
    /// </remarks>
    public enum DvrState
    {
        /// <summary>
        /// TVHeadend sent a state this plugin does not know, or none at all.
        /// </summary>
        Unknown,

        /// <summary>
        /// Planned, not started.
        /// </summary>
        Scheduled,

        /// <summary>
        /// Being recorded right now.
        /// </summary>
        Recording,

        /// <summary>
        /// Finished; a file exists unless <see cref="DvrEntry.Error"/> says otherwise.
        /// </summary>
        Completed,

        /// <summary>
        /// The broadcast was not recorded, for instance because no tuner was free.
        /// </summary>
        Missed,

        /// <summary>
        /// The entry is no longer valid, for instance because its channel is gone.
        /// </summary>
        Invalid,
    }
}
