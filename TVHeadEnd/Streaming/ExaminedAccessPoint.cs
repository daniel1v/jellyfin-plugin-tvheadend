namespace TVHeadEnd.Streaming;

/// <summary>
/// An access point whose picture has been read to the end, and what that picture turned out to be.
/// </summary>
/// <param name="Position">Where the access point is, in whatever coordinates the caller supplied.</param>
/// <param name="CarriesIdr">Whether the picture there begins on an IDR.</param>
public readonly record struct ExaminedAccessPoint(long Position, bool CarriesIdr);
