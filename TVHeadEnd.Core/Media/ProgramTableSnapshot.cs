using System;
using System.Collections.Generic;

namespace TVHeadEnd.Core.Media;

/// <summary>
/// The program tables as they stood at one moment, together with which layout they describe.
/// </summary>
/// <remarks>
/// Exists so the tables, the access points found beside them and the layout all travel as one
/// thing. The conditioner keeps mutating its own copies as the stream goes past; a reader has to
/// be given a set that belonged to each other.
/// </remarks>
public sealed class ProgramTableSnapshot
{
    private readonly byte[][] _programAssociationPackets;
    private readonly byte[][] _programMapPackets;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgramTableSnapshot"/> class.
    /// </summary>
    /// <param name="programAssociationPackets">The packets carrying the Program Association Table.</param>
    /// <param name="programMapPackets">The packets carrying the Program Map Table.</param>
    /// <param name="generation">Which program layout these tables describe.</param>
    /// <param name="generationStartOffset">
    /// Where in this chunk that layout begins, or -1 when it began before this chunk.
    /// </param>
    public ProgramTableSnapshot(
        byte[][] programAssociationPackets,
        byte[][] programMapPackets,
        int generation,
        int generationStartOffset = -1)
    {
        ArgumentNullException.ThrowIfNull(programAssociationPackets);
        ArgumentNullException.ThrowIfNull(programMapPackets);

        _programAssociationPackets = programAssociationPackets;
        _programMapPackets = programMapPackets;
        Generation = generation;
        GenerationStartOffset = generationStartOffset;
    }

    /// <summary>
    /// Gets an empty snapshot, for a stream whose tables have not both arrived.
    /// </summary>
    public static ProgramTableSnapshot Empty { get; } = new([], [], -1);

    /// <summary>
    /// Gets where in this chunk the current layout begins, or -1 when it began before it.
    /// </summary>
    /// <remarks>
    /// A layout change happens where the broadcaster put the table, which is somewhere inside a
    /// chunk and almost never at its start. Everything emitted before that point is still the
    /// programme before, so a reader must not be sent there behind the new tables -- and the chunk
    /// boundary, which is all the buffer knows by itself, would say it may.
    /// </remarks>
    public int GenerationStartOffset { get; }

    /// <summary>
    /// Gets which program layout these tables describe.
    /// </summary>
    /// <remarks>
    /// Rises whenever the broadcaster changes what the stream contains. It is what lets the
    /// bootstrap index tell a join point that belongs with these tables from one that was found
    /// under the layout before.
    /// </remarks>
    public int Generation { get; }

    /// <summary>
    /// Gets a value indicating whether both tables are present.
    /// </summary>
    public bool HasBoth => _programAssociationPackets.Length > 0 && _programMapPackets.Length > 0;

    /// <summary>
    /// Gets the packets carrying the Program Association Table.
    /// </summary>
    public IReadOnlyList<byte[]> ProgramAssociationPackets => _programAssociationPackets;

    /// <summary>
    /// Gets the packets carrying the Program Map Table.
    /// </summary>
    public IReadOnlyList<byte[]> ProgramMapPackets => _programMapPackets;
}
