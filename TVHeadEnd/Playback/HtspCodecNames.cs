using System;

namespace TVHeadEnd.Playback;

/// <summary>
/// Translates TVHeadend's stream type names into the codec names Jellyfin and FFmpeg use.
/// </summary>
/// <remarks>
/// The one place the two vocabularies meet. TVHeadend names come from its own
/// <c>streamtypetab</c>; the names on the other side are what FFmpeg reports and what a device
/// profile is written against, and a profile compares them literally. A type with no honest
/// counterpart yields <see langword="null"/> rather than a plausible guess -- an unknown codec
/// makes Jellyfin transcode, a wrong one makes it direct play something the client cannot decode.
/// </remarks>
public static class HtspCodecNames
{
    /// <summary>
    /// Translates a TVHeadend stream type.
    /// </summary>
    /// <param name="type">The type name as HTSP reports it.</param>
    /// <returns>The codec name, or <see langword="null"/> when there is no clear counterpart.</returns>
    public static string? ToJellyfinCodec(string? type) => type switch
    {
        "MPEG2VIDEO" => "mpeg2video",
        "H264" => "h264",
        "HEVC" => "hevc",
        "VP8" => "vp8",
        "VP9" => "vp9",
        "THEORA" => "theora",

        // MPEG-1 Layer II in practice, which is what DVB broadcasts and what FFmpeg reports as
        // mp2. TVHeadend uses the one name for the whole MPEG audio family.
        "MPEG2AUDIO" => "mp2",
        "AC3" => "ac3",
        "EAC3" => "eac3",

        // Two distinct entries in TVHeadend: the LATM framing used in broadcast, and raw AAC.
        // Both are AAC to a device profile.
        "AAC-LATM" => "aac",
        "AAC" => "aac",
        "VORBIS" => "vorbis",
        "OPUS" => "opus",
        "FLAC" => "flac",
        "AC-4" => "ac4",

        "DVBSUB" => "dvb_subtitle",
        "TELETEXT" => "dvb_teletext",
        "TEXTSUB" => "subrip",

        _ => null,
    };
}
