using System;
using System.Globalization;
using MediaBrowser.Common.Extensions;

namespace TVHeadEnd.Playback
{
    /// <summary>
    /// The forms a channel can be delivered in, each backed by one TVHeadend stream profile.
    /// </summary>
    public enum PlaybackVariant
    {
        /// <summary>
        /// The broadcast as TVHeadend delivers it through the native profile. Always offered
        /// unless a source-level fact forbids it, and always first, so a client that can decode
        /// it receives it untouched and a client that can decode nothing transcodes from the
        /// original rather than from something already re-coded once.
        /// </summary>
        Native = 0,

        /// <summary>
        /// An H.264 rendering produced by TVHeadend for broadcasts whose codec many clients
        /// cannot decode. Offered alongside the native stream; the device profile decides.
        /// </summary>
        Mpeg2H264Compatibility = 1,

        /// <summary>
        /// An H.264 re-encode produced by TVHeadend with genuine IDR access points, for
        /// broadcasts that signal random access without ever sending one.
        /// </summary>
        H264IdrNormalization = 2,
    }
}
