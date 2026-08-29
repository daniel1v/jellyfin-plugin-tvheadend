namespace TVHeadEnd.Streaming;

/// <summary>
/// What has been seen so far about the kind of picture an H.264 broadcast offers to begin at.
/// </summary>
/// <remarks>
/// <para>
/// A statement about the material examined and nothing else. It says what the access points read
/// so far carried; it does not say which client is asking, whether anything should be re-encoded,
/// or what the broadcaster habitually does. Those are decisions made elsewhere, from this.
/// </para>
/// <para>
/// The distinction that matters is between "no IDR seen" and "not enough seen to say". A stream
/// examined for two access points has told nobody anything yet, and treating its silence as an
/// answer would apply a workaround to broadcasts that never needed one.
/// </para>
/// </remarks>
public enum H264EntryPointEvidence
{
    /// <summary>
    /// Too little has been examined to say anything. Fewer than three access points have been
    /// read to the end, and none of the ones read carried an IDR.
    /// </summary>
    Insufficient = 0,

    /// <summary>
    /// Three or more access points were read to the end and every one of them opened on a
    /// recovery point rather than an IDR.
    /// </summary>
    RecoveryOnlyObserved = 1,

    /// <summary>
    /// At least one access point read opened on an IDR. Settled the moment it is seen, and never
    /// weakened by whatever follows.
    /// </summary>
    IdrObserved = 2,
}
