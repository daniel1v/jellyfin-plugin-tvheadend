using System;
using System.Collections.Generic;

namespace TVHeadEnd.Streaming;

/// <summary>
/// The program tables as they stood at one moment, ready to be published with the access points
/// they describe.
/// </summary>
/// <remarks>
/// Exists so the two can be handed over together. The conditioner keeps mutating its own copies
/// as the stream goes past; a reader has to be given a pair that belonged to each other.
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
    public ProgramTableSnapshot(byte[][] programAssociationPackets, byte[][] programMapPackets)
    {
        ArgumentNullException.ThrowIfNull(programAssociationPackets);
        ArgumentNullException.ThrowIfNull(programMapPackets);

        _programAssociationPackets = programAssociationPackets;
        _programMapPackets = programMapPackets;
    }

    /// <summary>
    /// Gets an empty snapshot, for a stream whose tables have not both arrived.
    /// </summary>
    public static ProgramTableSnapshot Empty { get; } = new([], []);

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
