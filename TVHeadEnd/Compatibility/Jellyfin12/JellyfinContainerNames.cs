using System;

namespace TVHeadEnd.Compatibility.Jellyfin12;

/// <summary>
/// What Jellyfin calls the container a source arrives in.
/// </summary>
/// <remarks>
/// <para>
/// A naming convention, not a fact about the bytes -- see <c>TransportStreamDetector</c> for the
/// fact. FFprobe calls MPEG-TS <c>mpegts</c> and Jellyfin's own <c>ProbeResultNormalizer</c>
/// rewrites that to <c>ts</c>; both arrivals are reported as <c>ts</c> here, so that a recording
/// and a live channel describe the same container identically and both match the one spelling
/// Jellyfin produces for every other file on the server.
/// </para>
/// <para>
/// It must be a name Jellyfin can hand to FFmpeg, because with hardware acceleration configured
/// the container of a media source is passed as <c>-f</c>. Not verbatim, though:
/// <c>EncodingHelper.GetInputFormat</c> translates on the way, so <c>ts</c> arrives as
/// <c>-f mpegts</c>. <c>-f ts</c> is neither produced nor a demuxer FFmpeg has, and that
/// translation is the whole reason the canonical name can be the one clients use.
/// </para>
/// <para>
/// Naming two spellings at once was tried and is what broke playback outright on such a server:
/// <c>mpegts,ts</c> is not in the translation table, so it reached FFmpeg unchanged as
/// <c>-f mpegts,ts</c>, which is not a demuxer either. One name, stated once, in one place.
/// </para>
/// </remarks>
public static class JellyfinContainerNames
{
    /// <summary>
    /// The one name this plugin gives the MPEG-TS container, whichever spelling it arrived as.
    /// </summary>
    public const string TransportStream = "ts";

    /// <summary>
    /// The container to report to clients for what an analysis found.
    /// </summary>
    /// <param name="probedContainer">The container FFprobe reported, if any.</param>
    /// <param name="fallback">What to keep when the analysis said nothing.</param>
    /// <returns>The container to report.</returns>
    public static string Describe(string? probedContainer, string fallback)
    {
        if (string.IsNullOrEmpty(probedContainer))
        {
            return fallback;
        }

        return IsTransportStreamName(probedContainer) ? TransportStream : probedContainer;
    }

    private static bool IsTransportStreamName(string container)
        => container.Equals("mpegts", StringComparison.OrdinalIgnoreCase)
            || container.Equals("ts", StringComparison.OrdinalIgnoreCase);
}
