using System;
using System.Collections.Generic;
using System.Linq;

namespace TVHeadEnd.Playback
{
    /// <summary>
    /// A known client defect this plugin works around.
    /// </summary>
    public enum PlaybackQuirk
    {
        /// <summary>
        /// No workaround.
        /// </summary>
        None = 0,

        /// <summary>
        /// The client's decoder cannot cold-start an H.264 stream whose access points are
        /// recovery points rather than IDR frames. It consumes the samples at full rate without
        /// ever emitting a frame, and the player stays in its buffering state indefinitely; no
        /// error is raised anywhere.
        /// </summary>
        /// <remarks>
        /// Established by a differential measurement: two library items from the same broadcast,
        /// identical container and audio, differing only in whether the video carried IDR
        /// frames. The copy without them never rendered a frame in two sessions; the re-encode
        /// with them rendered the first frame in about a fifth of a second.
        /// </remarks>
        H264DvbRecoveryOpenGopColdStart = 1,
    }

    /// <summary>
    /// Which clients need which workaround.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single place in the plugin that knows a client by name, and the reason the rest of it
    /// does not have to. Everything else asks this whether a quirk applies and is otherwise
    /// written as if every client were correct.
    /// </para>
    /// <para>
    /// Entries are meant to be narrowed and eventually removed. Each one names the client, the
    /// version range it applies to, and what was measured, so that a later version can be tested
    /// against it and the entry retired rather than inherited forever.
    /// </para>
    /// </remarks>
    public static class PlaybackQuirkPolicy
    {
        private static readonly IReadOnlyList<QuirkEntry> Entries =
        [
            // Measured on a Pixel 10 with org.jellyfin.mobile 2.7.1, whose c2.google.avc.decoder
            // gates output on an IDR. No upper bound is set because no later version has been
            // tested; when one is, this becomes a range and eventually disappears.
            //
            // The name is what the client puts in its authorization header, and the spellings
            // differ between the Jellyfin Android applications. Guessing at one of them is how
            // this quirk silently did nothing at all.
            new QuirkEntry(
                PlaybackQuirk.H264DvbRecoveryOpenGopColdStart,
                Client: "Jellyfin for Android",
                MinimumVersion: null,
                MaximumVersion: null),
            // Deliberately nothing else. The Android TV application was never measured, and
            // listing a client on the strength of sharing an operating system is how a
            // workaround spreads to callers that never needed it.
        ];

        /// <summary>
        /// Reports whether a quirk applies to the caller.
        /// </summary>
        /// <param name="context">The client context of the request being served.</param>
        /// <param name="quirk">The quirk.</param>
        /// <returns>Whether the workaround is needed.</returns>
        public static bool Applies(PlaybackClientContext? context, PlaybackQuirk quirk)
        {
            // No context means no evidence. Assuming a defect on that basis would degrade every
            // caller the plugin cannot identify, including correct ones.
            if (context is null || !context.IsKnown)
            {
                return false;
            }

            return Entries.Any(entry => entry.Matches(context, quirk));
        }

        /// <summary>
        /// One client, one quirk, one version range.
        /// </summary>
        private sealed record QuirkEntry(
            PlaybackQuirk Quirk,
            string Client,
            Version? MinimumVersion,
            Version? MaximumVersion)
        {
            /// <summary>
            /// Reports whether this entry covers a caller.
            /// </summary>
            /// <param name="context">The client context.</param>
            /// <param name="quirk">The quirk being asked about.</param>
            /// <returns>Whether it matches.</returns>
            public bool Matches(PlaybackClientContext context, PlaybackQuirk quirk)
            {
                if (Quirk != quirk
                    || !string.Equals(Client, context.Client, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (MinimumVersion is null && MaximumVersion is null)
                {
                    return true;
                }

                // An unparseable version is treated as covered: the entry exists because the
                // client is known to be affected, and a version this plugin cannot read is not
                // evidence that it has been fixed.
                if (!Version.TryParse(context.Version, out var version))
                {
                    return true;
                }

                return (MinimumVersion is null || version >= MinimumVersion)
                    && (MaximumVersion is null || version <= MaximumVersion);
            }
        }
    }
}
