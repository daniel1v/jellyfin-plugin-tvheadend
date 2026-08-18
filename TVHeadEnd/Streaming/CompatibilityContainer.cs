namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// What a compatibility rendering is delivered in.
    /// </summary>
    /// <remarks>
    /// Not a matter of preference. TVHeadend's transcoder cannot currently emit MPEG-TS, so the
    /// server-side roles produce Matroska; the plugin's own transitional encoder has always
    /// produced MPEG-TS and there is no reason to change it. Both work because a compatibility
    /// stream is served through Jellyfin's live stream file endpoint, which takes the content
    /// type from the container it is asked for -- so the one thing that must never happen is a
    /// stream described as one and delivered as the other.
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
