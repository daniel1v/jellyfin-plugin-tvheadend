using System;

namespace TVHeadEnd.Streaming;

/// <summary>
/// The Program Association Table of the stream being delivered: which PID carries the program
/// map.
/// </summary>
/// <remarks>
/// Held to the same standard as the program map, and for the same reason. A damaged or premature
/// PAT that was acted on would point the reader at the wrong PID, and every table read from there
/// would be a description of something else -- or of nothing, leaving the channel silent while
/// the plugin waited for a map that was never coming.
/// </remarks>
/// <param name="TransportStreamId">The transport stream this table belongs to.</param>
/// <param name="ProgramMapPid">The PID carrying the program map of the first real program.</param>
public sealed record ProgramAssociationTable(int TransportStreamId, int ProgramMapPid)
{
    private const byte TableIdProgramAssociation = 0x00;

    /// <summary>
    /// The section header, before the first program entry.
    /// </summary>
    private const int MinimumBodyLength = 8;

    /// <summary>
    /// Parses a complete PAT section.
    /// </summary>
    /// <param name="section">The reassembled section.</param>
    /// <returns>The table, or <see langword="null"/> when the section is not a usable PAT.</returns>
    public static ProgramAssociationTable? Parse(ReadOnlySpan<byte> section)
    {
        if (!PsiSectionHeader.TryValidate(section, TableIdProgramAssociation, MinimumBodyLength, out var end))
        {
            return null;
        }

        var transportStreamId = (section[3] << 8) | section[4];

        for (var offset = 8; offset < end; offset += 4)
        {
            if (offset + 4 > end)
            {
                // A stub too short to be a program entry: the section length did not match the
                // content.
                return null;
            }

            var programNumber = (section[offset] << 8) | section[offset + 1];
            var pid = ((section[offset + 2] & 0x1F) << 8) | section[offset + 3];

            if (programNumber == 0)
            {
                // The network information table, not a program.
                continue;
            }

            return new ProgramAssociationTable(transportStreamId, pid);
        }

        // A PAT that names no program at all. Valid syntax, but nothing to tune.
        return null;
    }
}
