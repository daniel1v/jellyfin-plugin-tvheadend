namespace TVHeadEnd.Core.Broadcast
{
    /// <summary>
    /// The year a broadcast says the programme was made in, where it says one at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the copyright year the broadcast carries. It is deliberately never derived from
    /// anything else that looks like a date: the start time is when the programme is being shown,
    /// and the first-aired date is when it was first shown -- a 1962 film premiering on this
    /// channel in 2019 is still a 1962 film, and either substitute would state something the
    /// broadcaster never said.
    /// </para>
    /// <para>
    /// Shared by the guide and the recordings because it is the same field of the same broadcast,
    /// arriving by two routes.
    /// </para>
    /// </remarks>
    public static class BroadcastProductionYear
    {
        /// <summary>
        /// The earliest year a moving picture could have been made in.
        /// </summary>
        private const int FirstPlausible = 1850;

        /// <summary>
        /// A bound far enough out that nothing real reaches it, so that only a field filled in
        /// with something other than a year is rejected.
        /// </summary>
        private const int LastPlausible = 2200;

        /// <summary>
        /// The production year a <c>copyrightYear</c> field amounts to.
        /// </summary>
        /// <param name="copyrightYear">The field, as TVHeadend sent it.</param>
        /// <returns>The year, or <see langword="null"/> when there is no plausible one.</returns>
        public static int? FromCopyrightYear(int? copyrightYear)
            => copyrightYear is >= FirstPlausible and <= LastPlausible ? copyrightYear : null;
    }
}
