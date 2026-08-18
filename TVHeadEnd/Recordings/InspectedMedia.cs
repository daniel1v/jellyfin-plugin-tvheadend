using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace TVHeadEnd.Recordings
{
    /// <summary>
    /// What an analysis of a sample found. Raw facts, before anything interprets them.
    /// </summary>
    /// <param name="Container">The container FFprobe reported, translated for client profiles.</param>
    /// <param name="Streams">The elementary streams, in the order FFprobe reported them.</param>
    /// <param name="Bitrate">The overall bitrate, if one was established.</param>
    /// <param name="Timestamp">The transport stream timestamp form.</param>
    /// <param name="VideoType">The video type.</param>
    /// <param name="Video3DFormat">The stereoscopic format.</param>
    public sealed record InspectedMedia(
        string Container,
        IReadOnlyList<MediaStream> Streams,
        int? Bitrate,
        TransportStreamTimestamp? Timestamp,
        VideoType? VideoType,
        Video3DFormat? Video3DFormat)
    {
        /// <summary>
        /// Gets the first video stream, or <see langword="null"/> when there is none.
        /// </summary>
        public MediaStream? Video => Streams.FirstOrDefault(stream => stream.Type == MediaStreamType.Video);

        /// <summary>
        /// Gets a value indicating whether the analysis found what Jellyfin needs at minimum: it
        /// dereferences the video stream while preparing playback and throws before any fallback
        /// could take effect.
        /// </summary>
        public bool IsUsable => Streams.Count > 0 && Video is not null;
    }
}
