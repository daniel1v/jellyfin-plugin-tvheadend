using System;

namespace Tvheadend.Htsp.Model;

/// <summary>
/// The stream type names TVHeadend puts on the wire, and which medium each one is.
/// </summary>
/// <remarks>
/// The names come from the server's own <c>streamtypetab</c>. They are TVHeadend's vocabulary,
/// not FFmpeg's and not any client's: <c>AAC-LATM</c> and <c>AAC</c> are two different entries
/// there, and <c>MPEG2AUDIO</c> covers both MPEG-1 Layer II and MPEG-2 audio. Translating them
/// is a consumer's job, so this only classifies them.
/// </remarks>
public static class HtspStreamTypes
{
    /// <summary>
    /// H.264 video.
    /// </summary>
    public const string H264 = "H264";

    /// <summary>
    /// MPEG-2 video.
    /// </summary>
    public const string Mpeg2Video = "MPEG2VIDEO";

    /// <summary>
    /// HEVC video.
    /// </summary>
    public const string Hevc = "HEVC";

    /// <summary>
    /// Reports whether a type names a video stream.
    /// </summary>
    /// <param name="type">The type name.</param>
    /// <returns>Whether it is video.</returns>
    public static bool IsVideo(string? type)
        => type is "MPEG2VIDEO" or "H264" or "VP8" or "HEVC" or "VP9" or "THEORA";

    /// <summary>
    /// Reports whether a type names an audio stream.
    /// </summary>
    /// <param name="type">The type name.</param>
    /// <returns>Whether it is audio.</returns>
    public static bool IsAudio(string? type)
        => type is "MPEG2AUDIO" or "AC3" or "AAC-LATM" or "EAC3" or "AAC" or "VORBIS"
            or "OPUS" or "FLAC" or "AC-4";

    /// <summary>
    /// Reports whether a type names a subtitle stream.
    /// </summary>
    /// <param name="type">The type name.</param>
    /// <returns>Whether it is a subtitle track.</returns>
    public static bool IsSubtitle(string? type)
        => type is "DVBSUB" or "TEXTSUB" or "TELETEXT";
}
