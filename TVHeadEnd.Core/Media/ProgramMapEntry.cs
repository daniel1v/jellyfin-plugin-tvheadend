namespace TVHeadEnd.Core.Media;

/// <summary>
/// What medium an elementary stream carries.
/// </summary>
public enum ElementaryStreamKind
{
    /// <summary>
    /// The table says what this is and it is not a medium a player renders: a carousel, a
    /// private section, a splice information stream.
    /// </summary>
    Data,

    /// <summary>
    /// The table does not say what this is.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Data"/> on purpose. Data is an answer; this is the absence of
    /// one, and a description containing it is incomplete rather than complete-with-a-data-track.
    /// The usual cause is stream type 0x06 -- private data in PES packets -- with no descriptor
    /// naming what is inside it.
    /// </remarks>
    Unknown,

    /// <summary>
    /// Video.
    /// </summary>
    Video,

    /// <summary>
    /// Audio.
    /// </summary>
    Audio,

    /// <summary>
    /// A subtitle or teletext track.
    /// </summary>
    Subtitle,
}

/// <summary>
/// One elementary stream as the Program Map Table announces it.
/// </summary>
/// <remarks>
/// Everything here is read from the table itself. The stream type alone is not enough for DVB:
/// type 0x06 is "private data carried in PES packets" and is the usual home of AC-3, E-AC-3,
/// DVB subtitles and teletext, which are told apart only by the descriptors that follow it.
/// </remarks>
public sealed record ProgramMapEntry
{
    /// <summary>
    /// Gets the MPEG stream type.
    /// </summary>
    public required byte StreamType { get; init; }

    /// <summary>
    /// Gets the PID the stream is carried on.
    /// </summary>
    public required int Pid { get; init; }

    /// <summary>
    /// Gets what medium the stream carries.
    /// </summary>
    public required ElementaryStreamKind Kind { get; init; }

    /// <summary>
    /// Gets the codec name FFmpeg and Jellyfin device profiles use, or <see langword="null"/>
    /// when the table does not identify one.
    /// </summary>
    public string? Codec { get; init; }

    /// <summary>
    /// Gets the ISO 639 language, or <see langword="null"/> when the table declares none.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Gets a value indicating whether the track is flagged for the hearing impaired.
    /// </summary>
    /// <remarks>
    /// From the audio type of the ISO 639 language descriptor for audio, and from the
    /// subtitling type of the subtitling descriptor for subtitles.
    /// </remarks>
    public bool IsHearingImpaired { get; init; }

    /// <summary>
    /// Gets what the tables say this audio track is for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two descriptors can answer this, and they do not carry equal weight. DVB's supplementary
    /// audio descriptor states the track's editorial role outright and is taken as final wherever
    /// it says something this understands. Where it does not -- a user defined or reserved
    /// classification -- it stays out of the way rather than erasing the other answer, and the
    /// audio type of the ISO 639 language descriptor is read instead.
    /// </para>
    /// <para>
    /// Audio types zero and one are both the programme's own sound: DVB uses either for main
    /// audio, and reading "clean effects" as an addition would withhold ordinary tracks. Two and
    /// three are the additions -- a hearing impaired mix, a commentary for the visually impaired.
    /// Anything above them is reserved and is not read as anything at all.
    /// </para>
    /// <para>
    /// Everything left over is <see cref="AudioPurpose.Unknown"/>, which is not a quieter way of
    /// saying supplementary: the tables did not answer, and a track nobody classified is not an
    /// addition to the programme.
    /// </para>
    /// </remarks>
    public AudioPurpose AudioPurpose { get; init; }

    /// <summary>
    /// Gets a value indicating whether the stream type names video.
    /// </summary>
    public bool IsVideo => Kind == ElementaryStreamKind.Video;
}
