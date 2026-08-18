using System;
using MediaBrowser.Controller.Library;
using TVHeadEnd.Media;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// What the Jellyfin adapter needs from an open stream, whichever way it was produced.
    /// </summary>
    /// <remarks>
    /// The two implementations have almost nothing in common below this line -- one conditions a
    /// shared transport stream into a ring buffer, the other spools a private rendering -- and
    /// deliberately so. This is the short list of questions the adapter asks either of them.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1711:Identifiers should not have incorrect suffix",
        Justification = "This is a Jellyfin ILiveStream, and 'stream' is what both Jellyfin and the domain call it.")]
    public interface ITvheadendStream : ILiveStream, IAsyncDisposable
    {
        /// <summary>
        /// Gets the TVHeadend channel identifier.
        /// </summary>
        string ChannelId { get; }

        /// <summary>
        /// Gets which form of the channel this serves.
        /// </summary>
        StreamProfileRole Role { get; }

        /// <summary>
        /// Gets the file the stream can be inspected as.
        /// </summary>
        string MediaPath { get; }

        /// <summary>
        /// Gets what the transport layer observed while receiving the stream, where it looked.
        /// </summary>
        TransportObservation Observation { get; }
    }
}
