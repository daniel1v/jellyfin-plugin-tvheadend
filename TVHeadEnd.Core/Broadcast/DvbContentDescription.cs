using System.Collections.Generic;

namespace TVHeadEnd.Core.Broadcast;

/// <summary>
/// What a DVB content type says a programme is.
/// </summary>
/// <param name="Genres">The genres to report.</param>
/// <param name="IsMovie">Whether the programme is a film.</param>
/// <param name="IsSports">Whether the programme is sport.</param>
/// <param name="IsNews">Whether the programme is news.</param>
/// <param name="IsKids">Whether the programme is for children.</param>
public readonly record struct DvbContentDescription(
    IReadOnlyList<string> Genres,
    bool IsMovie,
    bool IsSports,
    bool IsNews,
    bool IsKids);
