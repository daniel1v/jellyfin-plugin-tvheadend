namespace TVHeadEnd.Api
{
    /// <summary>
    /// The state of one stream profile role, as the settings page sees it.
    /// </summary>
    public class StreamProfileStatusDto
    {
        /// <summary>
        /// Gets or sets which role this is.
        /// </summary>
        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the configured TVHeadend profile name.
        /// </summary>
        public string? ProfileName { get; set; }

        /// <summary>
        /// Gets or sets how far the role has been established.
        /// </summary>
        public string State { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a short explanation, if any.
        /// </summary>
        public string? Detail { get; set; }
    }
}
