namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// What a compatibility rendering is delivered in, as a file extension.
    /// </summary>
    /// <remarks>
    /// Not a matter of preference. TVHeadend's transcoder cannot currently emit MPEG-TS, so the
    /// server-side roles produce Matroska; the plugin's own transitional encoder has always
    /// produced MPEG-TS and there is no reason to change it. This names the spool file and the
    /// URL it is served at. How the same container is named to a Jellyfin device profile is a
    /// separate question, answered in the playback mapper.
    /// </remarks>
    public static class CompatibilityContainer
    {
        /// <summary>
        /// What the TVHeadend compatibility profiles produce.
        /// </summary>
        public const string Matroska = "mkv";

        /// <summary>
        /// What the plugin's transitional encoder produces.
        /// </summary>
        public const string TransportStream = "ts";
    }
}
