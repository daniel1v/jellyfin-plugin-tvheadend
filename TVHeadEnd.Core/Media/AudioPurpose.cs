namespace TVHeadEnd.Core.Media;

/// <summary>
/// What the broadcast says an audio track is for.
/// </summary>
/// <remarks>
/// Three answers, because the tables give three: this is the programme's sound, this is an
/// addition to it, or the tables did not say. The third is not a polite form of the second --
/// DVB leaves the field undefined far more often than it fills it in, and reading silence as
/// "supplementary" would exclude ordinary tracks from every choice made downstream.
/// </remarks>
public enum AudioPurpose
{
    /// <summary>
    /// The tables say nothing conclusive about this track.
    /// </summary>
    /// <remarks>
    /// The default, and deliberately first: an entry nobody classified is unknown, not
    /// supplementary.
    /// </remarks>
    Unknown,

    /// <summary>
    /// The programme's own sound.
    /// </summary>
    Main,

    /// <summary>
    /// An addition alongside the programme sound: an audio description, a clean mix for the
    /// hearing impaired, spoken subtitles.
    /// </summary>
    Supplementary,
}
