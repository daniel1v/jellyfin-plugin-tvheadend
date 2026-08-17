using System.Collections.Generic;

namespace TVHeadEnd.Legacy
{
    /// <summary>
    /// The plugin's own H.264 encoder, kept only until TVHeadend can do this itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transitional, and deliberately isolated. Producing a compatible rendering of a broadcast
    /// is TVHeadend's job -- it already has the transcoding machinery, it can use the host's
    /// hardware, and it does not require this plugin to run a process per stream. This exists
    /// because a broadcast that signals random access without IDR frames does not cold-start on
    /// some device decoders, and that could not wait for an administrator to configure a
    /// profile for it.
    /// </para>
    /// <para>
    /// Once a TVHeadend <c>H264IdrNormalization</c> profile has been configured and validated,
    /// this file and its callers can be deleted without touching playback policy, the transport
    /// layer or the live stream implementation.
    /// </para>
    /// </remarks>
    public static class LegacyH264Encoder
    {
        /// <summary>
        /// Builds the FFmpeg argument list that re-encodes video to H.264 with genuine IDR
        /// access points while copying every audio track.
        /// </summary>
        /// <remarks>
        /// The encoder is always fed through a pipe rather than pointed at the source. For a
        /// live channel that avoids opening a second TVHeadend subscription for a channel
        /// already being received; for a recording it is what stops FFmpeg seeking back after
        /// its analysis, which TVHeadend answers by dropping the connection.
        /// </remarks>
        /// <param name="input">What FFmpeg reads. A pipe unless the source is a local file.</param>
        /// <returns>The argument list, one argument per element.</returns>
        public static IReadOnlyList<string> BuildArguments(string input = "pipe:0")
        {
            return
            [
                "-hide_banner",
                "-loglevel", "warning",
                "-fflags", "+genpts",

                // FFmpeg would otherwise spend up to its five second default deciding what a
                // transport stream contains. The PMT names every elementary stream within the
                // first packets, which is all the encoder needs.
                "-analyzeduration", "1000000",
                "-probesize", "4000000",

                "-f", "mpegts",
                "-i", string.IsNullOrEmpty(input) ? "pipe:0" : input,
                "-map", "0:v:0",
                "-map", "0:a?",
                "-dn", "-sn",
                "-c:a", "copy",
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-crf", "21",
                "-maxrate", "10M",
                "-bufsize", "14M",

                // Closed GOPs whose keyframes are IDR: exactly the property the source lacks and
                // device decoders refuse to start without.
                "-x264-params", "keyint=50:min-keyint=25:scenecut=0",

                // Passes progressive frames through untouched and deinterlaces the rest, so
                // interlaced services do not come out combed.
                "-vf", "yadif=deint=interlaced",

                "-f", "mpegts",
                "pipe:1",
            ];
        }
    }
}
