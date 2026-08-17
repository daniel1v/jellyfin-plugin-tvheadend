using System;

namespace TVHeadEnd.Playback
{
    /// <summary>
    /// Who is asking, as far as Jellyfin's authorization context states it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a plain record of four strings. It is filled in at the Jellyfin adapter
    /// boundary and nowhere else, so that no domain, TVHeadend, media or streaming class ever
    /// sees an <c>HttpContext</c> or a Jellyfin authorization type. Everything below the
    /// boundary either receives this or receives nothing.
    /// </para>
    /// <para>
    /// The values come from the session Jellyfin already authenticated. User-agent strings are
    /// deliberately not parsed: they are unreliable, and every client Jellyfin serves identifies
    /// itself properly here.
    /// </para>
    /// </remarks>
    /// <param name="Client">The client name, such as "Jellyfin Android".</param>
    /// <param name="Version">The client version.</param>
    /// <param name="Device">The device name.</param>
    /// <param name="DeviceId">The device identifier.</param>
    public sealed record PlaybackClientContext(string? Client, string? Version, string? Device, string? DeviceId)
    {
        /// <summary>
        /// No request context. Playback policy treats this as "assume nothing", which means the
        /// native stream: a quirk is a statement about a known client, and an unknown caller is
        /// not one.
        /// </summary>
        public static readonly PlaybackClientContext None = new(null, null, null, null);

        /// <summary>
        /// Gets a value indicating whether anything is known about the caller.
        /// </summary>
        public bool IsKnown => !string.IsNullOrEmpty(Client);

        /// <summary>
        /// Returns a short form for logging.
        /// </summary>
        /// <returns>The client and version.</returns>
        public string Describe()
            => IsKnown
                ? $"{Client}{(string.IsNullOrEmpty(Version) ? string.Empty : " " + Version)}"
                : "<no request context>";
    }
}
