using TVHeadEnd.Core.Media;

namespace TVHeadEnd.Compatibility;

/// <summary>
/// The single rule that decides when this plugin asks Jellyfin to re-encode video.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and deliberately tiny. Live TV and recordings reach Jellyfin through entirely different
/// hooks -- one has a stream being opened, the other a request being answered -- but the decision
/// they make is the same one, and having it written twice is how the two would end up disagreeing
/// about the same broadcast on the same evening.
/// </para>
/// <para>
/// It takes a fact about the client and a fact about the material, and nothing else: no
/// configuration, no channel history, no broadcaster names. Both inputs default to the harmless
/// answer, so anything unknown produces no workaround at all.
/// </para>
/// </remarks>
public static class PlaybackCompatibilityPolicy
{
    /// <summary>
    /// Whether the video has to be re-encoded for this client to be able to start on it.
    /// </summary>
    /// <remarks>
    /// True only where both halves are actually established: a client whose decoder will not
    /// start without an IDR picture, and material that was examined and found to offer none.
    /// <see cref="H264EntryPointEvidence.Insufficient"/> is not evidence of absence and never
    /// triggers this -- a stream nobody has read enough of is delivered untouched, as is one that
    /// is not H.264 at all.
    /// </remarks>
    /// <param name="clientNeedsIdrEntryPoint">Whether this client's decoder needs an IDR to start.</param>
    /// <param name="evidence">What the access points of the material were found to open on.</param>
    /// <returns>Whether Jellyfin should be asked to re-encode the video.</returns>
    public static bool RequiresVideoReencode(bool clientNeedsIdrEntryPoint, H264EntryPointEvidence evidence)
        => clientNeedsIdrEntryPoint && evidence == H264EntryPointEvidence.RecoveryOnlyObserved;
}
