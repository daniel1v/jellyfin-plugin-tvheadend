using System;
using System.Collections.Generic;

namespace TVHeadEnd.Tests.Core;

/// <summary>
/// Builds PSI sections the way a broadcaster does, including the CRC the parser checks.
/// </summary>
/// <remarks>
/// The plugin refuses a section whose CRC does not match, because the program map is the only
/// description of a live stream it has and a damaged one would be believed as readily as a good
/// one. A fixture that left the CRC blank would therefore test nothing but the rejection path.
/// </remarks>
internal static class PsiSectionBytes
{
    /// <summary>
    /// Appends the MPEG-2 systems CRC-32 to a section body.
    /// </summary>
    /// <param name="body">The section without its CRC. The section length must already be set.</param>
    /// <returns>The complete section.</returns>
    internal static byte[] WithCrc(IReadOnlyList<byte> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var crc = Compute(body);
        return
        [
            .. body,
            (byte)(crc >> 24),
            (byte)(crc >> 16),
            (byte)(crc >> 8),
            (byte)crc,
        ];
    }

    /// <summary>
    /// Computes the MPEG-2 systems CRC-32 over a span.
    /// </summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The CRC.</returns>
    internal static uint Compute(IReadOnlyList<byte> data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
            }
        }

        return crc;
    }
}
