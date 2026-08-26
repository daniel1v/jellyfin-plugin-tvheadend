using System;
using System.Collections.Generic;
using System.Linq;

namespace TVHeadEnd.Playback;

/// <summary>
/// Widens a client's transport stream capability to cover both names it might be written under.
/// </summary>
/// <remarks>
/// <para>
/// MPEG-TS has two spellings in Jellyfin's world -- <c>ts</c>, which its probe normaliser produces
/// and Android TV lists, and <c>mpegts</c>, which FFprobe reports and the mobile app lists -- and
/// the comparison between a device profile and a media source is literal. A source can only be one
/// of them, so it is the profile that has to say both.
/// </para>
/// <para>
/// This adds the missing spelling to a capability that already names one of them, and does nothing
/// else. No container is invented, no other container is touched, a list that already names both
/// is left as it is, and the meaning of every condition attached to the profile is unchanged: the
/// two names are the same container, which is the whole of the claim being made.
/// </para>
/// </remarks>
public static class TransportStreamAliases
{
    private const string Ts = "ts";
    private const string Mpegts = "mpegts";

    /// <summary>
    /// The same container list, with the other spelling of MPEG-TS added where one is present.
    /// </summary>
    /// <param name="containers">The comma-separated list a profile states.</param>
    /// <returns>The widened list, or the original where nothing was missing.</returns>
    public static string? Widen(string? containers)
    {
        if (string.IsNullOrEmpty(containers))
        {
            return containers;
        }

        // A leading minus makes the list a negative one -- everything except these. Widening that
        // would take a capability away rather than add one, so it is left alone.
        if (containers.StartsWith('-'))
        {
            return containers;
        }

        var named = containers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var hasTs = named.Any(name => string.Equals(name, Ts, StringComparison.OrdinalIgnoreCase));
        var hasMpegts = named.Any(name => string.Equals(name, Mpegts, StringComparison.OrdinalIgnoreCase));

        if (hasTs == hasMpegts)
        {
            // Both named, or neither. Nothing to add either way.
            return containers;
        }

        var widened = new List<string>(named) { hasTs ? Mpegts : Ts };
        return string.Join(',', widened);
    }
}
