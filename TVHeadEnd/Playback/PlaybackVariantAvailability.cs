namespace TVHeadEnd.Playback
{
    /// <summary>
    /// Which delivery forms can actually be produced right now.
    /// </summary>
    /// <param name="Mpeg2H264Compatibility">Whether a usable MPEG-2 to H.264 profile is configured.</param>
    /// <param name="H264IdrNormalization">
    /// Whether an IDR-normalizing form can be produced, either by a configured TVHeadend profile
    /// or by the transitional plugin-side encoder.
    /// </param>
    public readonly record struct PlaybackVariantAvailability(
        bool Mpeg2H264Compatibility,
        bool H264IdrNormalization)
    {
        /// <summary>
        /// Nothing but the native stream can be produced.
        /// </summary>
        public static readonly PlaybackVariantAvailability NativeOnly = new(false, false);
    }

    /// <summary>
    /// One variant on offer to Jellyfin.
    /// </summary>
    /// <param name="Variant">Which form of the channel.</param>
    /// <param name="SupportsDirectPlay">Whether a client may be handed this form unmodified.</param>
    public readonly record struct VariantOffer(PlaybackVariant Variant, bool SupportsDirectPlay);
}
