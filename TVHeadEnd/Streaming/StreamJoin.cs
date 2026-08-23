using System.Diagnostics.CodeAnalysis;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Everything a reader needs to start: what to send it first, and where in the buffer to read on
/// from.
/// </summary>
/// <remarks>
/// One value because it is one decision. The tables and the position are taken together under a
/// single lock, so a reader can never hold the tables of one program layout and a position inside
/// another.
/// </remarks>
public sealed class StreamJoin
{
    private readonly byte[] _tables;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamJoin"/> class.
    /// </summary>
    /// <param name="tables">The tables to deliver ahead of the buffer content.</param>
    /// <param name="position">Where to read on from, or <see langword="null"/> for the oldest bytes.</param>
    public StreamJoin(byte[] tables, long? position)
    {
        _tables = tables;
        Position = position;
    }

    /// <summary>
    /// Gets the program tables to deliver ahead of the buffer content, empty when none have been
    /// seen.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1819:Properties should not return arrays",
        Justification = "These are bytes to be written to a stream, built fresh for this reader; a copy per read would be the only effect of hiding them behind a list.")]
    public byte[] Tables => _tables ?? [];

    /// <summary>
    /// Gets the logical position to read on from, or <see langword="null"/> when no access point
    /// survives in the window and the reader has to take the oldest bytes there are.
    /// </summary>
    public long? Position { get; }
}
