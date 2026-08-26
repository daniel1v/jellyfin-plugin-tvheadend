using System;

namespace TVHeadEnd.Streaming;

/// <summary>
/// The checks every PSI section has to pass before anything is read out of it.
/// </summary>
/// <remarks>
/// The program tables are the only description of a live stream this plugin has, so a damaged or
/// premature one would be believed exactly as readily as a good one. Both tables are held to the
/// same standard rather than each growing its own idea of what is acceptable.
/// </remarks>
internal static class PsiSectionHeader
{
    /// <summary>
    /// Checks a section and returns the extent of its body.
    /// </summary>
    /// <param name="section">The reassembled section, including its trailing CRC.</param>
    /// <param name="tableId">The table identifier the section has to carry.</param>
    /// <param name="minimumBodyLength">
    /// The smallest body the table's own syntax can have, measured from the start of the section.
    /// </param>
    /// <param name="bodyEnd">Where the body ends and the CRC begins.</param>
    /// <returns>Whether the section may be read.</returns>
    internal static bool TryValidate(
        ReadOnlySpan<byte> section,
        byte tableId,
        int minimumBodyLength,
        out int bodyEnd)
    {
        bodyEnd = 0;

        if (section.Length < minimumBodyLength + 4 || section[0] != tableId)
        {
            return false;
        }

        // The section_syntax_indicator has to be set: a PSI table without it carries no CRC and
        // none of the fields read here.
        if ((section[1] & 0x80) == 0)
        {
            return false;
        }

        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var end = 3 + sectionLength - 4;
        if (end > section.Length || end < minimumBodyLength)
        {
            return false;
        }

        // A table announced for later must not be acted on now. current_next_indicator is the
        // broadcaster saying "this describes the program as it will be", and applying it to the
        // stream as it is would describe streams that are not there yet.
        if ((section[5] & 0x01) == 0)
        {
            return false;
        }

        if (!HasValidCrc(section[..(end + 4)]))
        {
            return false;
        }

        bodyEnd = end;
        return true;
    }

    /// <summary>
    /// Checks the MPEG-2 systems CRC-32 that every PSI section ends with.
    /// </summary>
    /// <remarks>
    /// Computed over the section including its own CRC, which leaves zero when the section is
    /// intact. The polynomial is the one ISO 13818-1 specifies; it is not the same CRC-32 as
    /// zlib's, so a general-purpose implementation cannot stand in for it.
    /// </remarks>
    /// <param name="section">The section, including its trailing CRC.</param>
    /// <returns>Whether the section is intact.</returns>
    private static bool HasValidCrc(ReadOnlySpan<byte> section)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in section)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
            }
        }

        return crc == 0;
    }
}
