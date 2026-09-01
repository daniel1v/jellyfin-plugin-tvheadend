namespace TVHeadEnd.Core.Media;

/// <summary>
/// What an elementary stream is encoded with, as the broadcast itself says.
/// </summary>
/// <remarks>
/// <para>
/// Named rather than spelled. This used to be the string FFmpeg happens to print and Jellyfin
/// happens to match device profiles against -- which meant a table reading DVB descriptors was
/// quietly stating a fact about a media player, and changing what a player calls AC-3 would have
/// meant editing a transport stream parser.
/// </para>
/// <para>
/// Only what the broadcasts this plugin reads actually carry. It is not a codec database and must
/// not become one: a value belongs here when a program map can name it, and nowhere else.
/// </para>
/// </remarks>
public enum ElementaryStreamCodec
{
    /// <summary>
    /// The table said nothing this understands. Distinct from a stream that is known to be data:
    /// this is the absence of an answer rather than an answer.
    /// </summary>
    Unknown = 0,

    /// <summary>MPEG-2 video, and MPEG-1 video, which DVB carries under the same reading.</summary>
    Mpeg2Video,

    /// <summary>MPEG-4 part 2 video.</summary>
    Mpeg4Video,

    /// <summary>H.264, which is most of what European DVB carries.</summary>
    H264,

    /// <summary>HEVC.</summary>
    Hevc,

    /// <summary>
    /// MPEG audio. DVB mandates Layer II for it (ETSI TS 101 154), which is what this names.
    /// </summary>
    MpegAudioLayer2,

    /// <summary>AAC in ADTS.</summary>
    Aac,

    /// <summary>AAC in LATM.</summary>
    AacLatm,

    /// <summary>AC-3.</summary>
    Ac3,

    /// <summary>Enhanced AC-3.</summary>
    Eac3,

    /// <summary>DTS.</summary>
    Dts,

    /// <summary>DVB subtitles.</summary>
    DvbSubtitle,

    /// <summary>DVB teletext.</summary>
    DvbTeletext,
}
