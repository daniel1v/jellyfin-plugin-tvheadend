using TVHeadEnd.Core.Media;

namespace TVHeadEnd.Compatibility.Jellyfin12;

/// <summary>
/// What Jellyfin and FFmpeg call the codecs a broadcast carries.
/// </summary>
/// <remarks>
/// <para>
/// The one translation from what the program map said into the spelling a device profile is
/// matched against. It is a Jellyfin fact, not a broadcast one: FFmpeg picked these names, device
/// profiles are written against them, and a name this gets wrong is not an error anywhere -- it is
/// a channel that quietly transcodes because no profile matched a codec nobody recognised.
/// </para>
/// <para>
/// A codec this cannot name is left unnamed rather than guessed at. Jellyfin reads that as a track
/// it cannot match, which for a stream nothing has identified is the truth.
/// </para>
/// </remarks>
public static class JellyfinCodecNames
{
    /// <summary>
    /// The name Jellyfin knows a codec by.
    /// </summary>
    /// <param name="codec">What the broadcast said the stream is.</param>
    /// <returns>The name, or <see langword="null"/> where there is none to give.</returns>
    public static string? For(ElementaryStreamCodec codec) => codec switch
    {
        ElementaryStreamCodec.Mpeg2Video => "mpeg2video",
        ElementaryStreamCodec.Mpeg4Video => "mpeg4",
        ElementaryStreamCodec.H264 => "h264",
        ElementaryStreamCodec.Hevc => "hevc",
        ElementaryStreamCodec.MpegAudioLayer2 => "mp2",
        ElementaryStreamCodec.Aac => "aac",
        ElementaryStreamCodec.AacLatm => "aac_latm",
        ElementaryStreamCodec.Ac3 => "ac3",
        ElementaryStreamCodec.Eac3 => "eac3",
        ElementaryStreamCodec.Dts => "dts",
        ElementaryStreamCodec.DvbSubtitle => "dvb_subtitle",
        ElementaryStreamCodec.DvbTeletext => "dvb_teletext",
        _ => null,
    };
}
