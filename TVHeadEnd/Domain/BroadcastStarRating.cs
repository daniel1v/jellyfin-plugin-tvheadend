using System;

namespace TVHeadEnd.Domain
{
    /// <summary>
    /// Translates TVHeadend's star rating into the scale Jellyfin's community rating is read on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two are not the same number. TVHeadend stores a <em>percentage</em>: the only thing in
    /// its tree that produces a star rating is the XMLTV grabber, which parses "3.3/5" and stores
    /// <c>(100 * 3.3) / 5</c> -- 66 -- in a byte. Jellyfin's <c>CommunityRating</c> is the number
    /// its clients render as "x out of ten", the same scale IMDb and TMDb ratings arrive on.
    /// </para>
    /// <para>
    /// So one divides into the other, and it is done here rather than at either end because
    /// getting it wrong is invisible: 66 handed over unchanged is not an error anywhere, it is
    /// simply a programme rated 66 out of 10.
    /// </para>
    /// <para>
    /// Above 100 the percentage reading no longer holds, and there is nothing else it could be:
    /// the field is a byte, so a larger number means the sender is using a scale this does not
    /// know. Saying nothing is the honest answer to that -- and it is the answer that leaves
    /// Jellyfin free to fill the rating in from its own metadata providers.
    /// </para>
    /// </remarks>
    public static class BroadcastStarRating
    {
        /// <summary>
        /// The value TVHeadend sends for a programme it has no rating for. It omits the field
        /// entirely in that case, so this is only ever seen from a sender that does not.
        /// </summary>
        private const long Unrated = 0;

        /// <summary>
        /// The highest percentage there is, and the largest value this recognises.
        /// </summary>
        private const long FullMarks = 100;

        /// <summary>
        /// The community rating a TVHeadend star rating amounts to.
        /// </summary>
        /// <param name="starRating">The <c>starRating</c> field, as a percentage.</param>
        /// <returns>
        /// The rating out of ten, or <see langword="null"/> where the field was absent, said the
        /// programme is unrated, or carried a number no percentage could be.
        /// </returns>
        public static float? ToCommunityRating(long? starRating)
        {
            if (starRating is not { } percentage || percentage <= Unrated || percentage > FullMarks)
            {
                return null;
            }

            return (float)Math.Round(percentage / 10.0, 1, MidpointRounding.AwayFromZero);
        }
    }
}
