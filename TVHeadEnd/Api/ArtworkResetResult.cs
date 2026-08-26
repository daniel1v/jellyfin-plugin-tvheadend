namespace TVHeadEnd.Api
{
    /// <summary>
    /// What a reset of the stored recording artwork did.
    /// </summary>
    public sealed class ArtworkResetResult
    {
        /// <summary>
        /// Gets or sets how many recordings had their artwork cleared.
        /// </summary>
        public int Cleared { get; set; }

        /// <summary>
        /// Gets or sets how many recordings were looked at.
        /// </summary>
        public int Total { get; set; }
    }
}
