using System.Diagnostics.CodeAnalysis;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Whether a reader can start, and where.
/// </summary>
public enum StreamJoinKind
{
    /// <summary>
    /// There is nowhere in the buffer this reader can safely begin. It happens between a program
    /// layout change and the first access point under the new layout: the tables that describe the
    /// stream now do not describe any of the bytes still held.
    /// </summary>
    NotYet,

    /// <summary>
    /// No access point survives, but the oldest bytes still held belong to the layout the tables
    /// describe, so reading on from them is sound -- the opening may not decode until the decoder
    /// resynchronises, which it can, because the tables tell it what the streams are.
    /// </summary>
    FromOldest,

    /// <summary>
    /// A recorded access point of the current layout.
    /// </summary>
    AtPosition,
}

/// <summary>
/// Everything a reader needs to start: what to send it first, and where in the buffer to read on
/// from.
/// </summary>
/// <remarks>
/// One value because it is one decision. The tables and the position are taken together under a
/// single lock, so a reader can never hold the tables of one program layout and a position inside
/// another -- including through the case where there is no position at all, which is a state of
/// its own here rather than a null that every caller had to guess the meaning of.
/// </remarks>
public sealed class StreamJoin
{
    private readonly byte[] _tables;

    private StreamJoin(StreamJoinKind kind, byte[] tables, long position)
    {
        Kind = kind;
        _tables = tables;
        Position = position;
    }

    /// <summary>
    /// Gets the join that says a reader has to wait.
    /// </summary>
    public static StreamJoin NotYet { get; } = new(StreamJoinKind.NotYet, [], 0);

    /// <summary>
    /// Gets what kind of start this is.
    /// </summary>
    public StreamJoinKind Kind { get; }

    /// <summary>
    /// Gets the position to read on from, meaningful only for <see cref="StreamJoinKind.AtPosition"/>.
    /// </summary>
    public long Position { get; }

    /// <summary>
    /// Gets the program tables to deliver ahead of the buffer content, empty when there are none.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1819:Properties should not return arrays",
        Justification = "These are bytes to be written to a stream, built fresh for this reader; a copy per read would be the only effect of hiding them behind a list.")]
    public byte[] Tables => _tables;

    /// <summary>
    /// Creates a join at a recorded access point.
    /// </summary>
    /// <param name="tables">The tables valid there.</param>
    /// <param name="position">The position.</param>
    /// <returns>The join.</returns>
    public static StreamJoin At(byte[] tables, long position)
        => new(StreamJoinKind.AtPosition, tables, position);

    /// <summary>
    /// Creates a join that reads on from the oldest bytes still held.
    /// </summary>
    /// <param name="tables">The tables valid for those bytes.</param>
    /// <returns>The join.</returns>
    public static StreamJoin FromOldest(byte[] tables)
        => new(StreamJoinKind.FromOldest, tables, 0);
}
