namespace TVHeadEnd.Streaming;

/// <summary>
/// What a broadcast audio purpose means to Jellyfin.
/// </summary>
public static class AudioPurposeExtensions
{
    /// <summary>
    /// Reports whether a track belongs in the set Jellyfin may choose a default from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Broadcast metadata, not a preference: the tables are being asked what a track is for, and
    /// only a track they call an addition to the programme is withheld. Main and unclassified
    /// both stay in.
    /// </para>
    /// <para>
    /// The asymmetry is deliberate and was measured. Jellyfin narrows its audio candidates to the
    /// tracks marked default whenever the viewer prefers default tracks, which is how a new
    /// account is created. If that narrowing yields nothing, the compatibility check is skipped
    /// rather than failed -- direct play is granted and labelled with the first track of the map,
    /// whatever codec it happens to be. Reading an unclassified track as an addition would put
    /// every programme with no audio descriptors into exactly that state, and a recording with
    /// no default at all lands there without needing any descriptors to be misread.
    /// </para>
    /// <para>
    /// Live TV and recordings both read this. They are the same broadcast, and for a recording
    /// made with TVHeadend's <c>pass</c> profile they are quite literally the same bytes.
    /// </para>
    /// </remarks>
    /// <param name="purpose">What the tables said the track is for.</param>
    /// <returns>Whether Jellyfin may pick it as a default.</returns>
    public static bool BelongsInTheDefaultSet(this AudioPurpose purpose)
        => purpose != AudioPurpose.Supplementary;
}
