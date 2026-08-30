using System;
using System.Collections.Generic;

namespace TVHeadEnd.Core.Broadcast
{
    /// <summary>
    /// Puts the two accounts a broadcast gives of its own genre together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A DVB broadcast classifies itself twice, and the two are not translations of each other.
    /// The content descriptor is a byte from a fixed table, the same everywhere, and it is what
    /// says whether something is a film or the news; the free text a grabber supplies is whatever
    /// the broadcaster wrote, in the broadcaster's own language. "Krimi" and "Detective" are both
    /// true of the same programme, and the viewer searching for either should find it.
    /// </para>
    /// <para>
    /// So neither replaces the other and nothing is translated between them. There is no table
    /// here mapping one vocabulary onto another: a table like that would have to be maintained per
    /// language and per broadcaster, and it would be wrong for the first channel nobody thought of.
    /// </para>
    /// </remarks>
    public static class BroadcastGenres
    {
        /// <summary>
        /// Combines genres from several accounts of the same broadcast.
        /// </summary>
        /// <remarks>
        /// Order is the caller's and is kept, so the broadcaster's own words come first where the
        /// caller passed them first. Duplicates are dropped without regard to case, because
        /// "Drama" and "drama" are one genre and Jellyfin would list them as two.
        /// </remarks>
        /// <param name="sources">The genre lists, in the order they should appear.</param>
        /// <returns>The combined genres.</returns>
        public static IReadOnlyList<string> Combine(params IEnumerable<string>?[] sources)
        {
            ArgumentNullException.ThrowIfNull(sources);

            var combined = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in sources)
            {
                if (source is null)
                {
                    continue;
                }

                foreach (var genre in source)
                {
                    var trimmed = genre?.Trim();
                    if (string.IsNullOrEmpty(trimmed) || !seen.Add(trimmed))
                    {
                        continue;
                    }

                    combined.Add(trimmed);
                }
            }

            return combined;
        }
    }
}
