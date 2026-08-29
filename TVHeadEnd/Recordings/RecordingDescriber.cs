using System;
using MediaBrowser.Model.Dto;
using TVHeadEnd.Streaming;

namespace TVHeadEnd.Recordings
{
    /// <summary>
    /// Fills in a media source from an analysis of the recording it stands for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is read here: <see cref="RecordingAnalysisService"/> has already fetched the sample
    /// and answered what is in it, possibly for a different caller entirely. This is only the step
    /// that turns those facts into the description Jellyfin publishes, which is a separate job
    /// with its own rules about what may be claimed.
    /// </para>
    /// <para>
    /// The two rules this plugin has broken in both paths in turn: stream order is never touched,
    /// and the runtime never comes from the sample.
    /// </para>
    /// </remarks>
    public static class RecordingDescriber
    {
        /// <summary>
        /// Describes <paramref name="target"/> from an analysis of the recording.
        /// </summary>
        /// <param name="target">The media source to fill in.</param>
        /// <param name="analysis">What a sample of the recording contains.</param>
        /// <returns>
        /// <see langword="true"/> when the analysis described the recording. On
        /// <see langword="false"/> the target is untouched, so the caller keeps whatever it had; a
        /// source without streams must never reach Jellyfin, which dereferences the video stream
        /// while preparing playback and throws before any fallback could take effect.
        /// </returns>
        public static bool Describe(MediaSourceInfo target, RecordingAnalysis analysis)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(analysis);

            if (analysis.Media is not { } inspected)
            {
                return false;
            }

            // Verbatim, in analysis order: Jellyfin addresses streams by their position.
            target.MediaStreams = [.. inspected.Streams];

            target.Container = inspected.Container;
            target.Bitrate = inspected.Bitrate;
            target.Timestamp = inspected.Timestamp;
            target.VideoType = inspected.VideoType;
            target.Video3DFormat = inspected.Video3DFormat;

            // What the broadcast said about its own audio, which FFprobe does not read. A
            // recording made with the pass profile carries the same program map a live channel
            // does, so the two paths describe the same tracks the same way. After the container
            // is settled, because whether a program map applies at all depends on it.
            if (string.Equals(target.Container, SourceContainer.TransportStream, StringComparison.Ordinal))
            {
                BroadcastAudioFacts.Apply(target.MediaStreams, analysis.ProgramMap);
            }

            // The full result is in hand, including real stream indices. Without this Jellyfin
            // replaces it with its own cached view, whose "-map" arguments land on wrong tracks.
            target.SupportsProbing = false;

            return true;
        }
    }
}
