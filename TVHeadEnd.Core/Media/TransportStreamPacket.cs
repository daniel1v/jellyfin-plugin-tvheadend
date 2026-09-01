using System;
using System.IO;

namespace TVHeadEnd.Core.Media
{
    /// <summary>
    /// Reads the fields of a 188-byte MPEG transport stream packet.
    /// </summary>
    /// <remarks>
    /// Shared rather than repeated: the conditioner decides what to forward, the indexer
    /// decides where a late reader may join, and both have to agree on what a packet says.
    /// </remarks>
    public static class TransportStreamPacket
    {
        /// <summary>
        /// The length of a transport stream packet.
        /// </summary>
        public const int Length = 188;

        /// <summary>
        /// The value every packet starts with.
        /// </summary>
        public const byte SyncByte = 0x47;

        /// <summary>
        /// The PID carrying the Program Association Table.
        /// </summary>
        public const int ProgramAssociationTablePid = 0x00;

        /// <summary>
        /// The DVB Event Information Table PID, which FFmpeg exposes as an "epg" data stream.
        /// </summary>
        public const int EventInformationTablePid = 0x12;

        /// <summary>
        /// Returns the PID of <paramref name="packet"/>.
        /// </summary>
        /// <param name="packet">A whole transport stream packet.</param>
        /// <returns>The PID.</returns>
        public static int ReadPid(ReadOnlySpan<byte> packet) => ((packet[1] & 0x1F) << 8) | packet[2];

        /// <summary>
        /// Reports whether <paramref name="packet"/> begins a payload unit.
        /// </summary>
        /// <param name="packet">A whole transport stream packet.</param>
        /// <returns>Whether the payload unit start indicator is set.</returns>
        public static bool StartsPayloadUnit(ReadOnlySpan<byte> packet) => (packet[1] & 0x40) != 0;

        /// <summary>
        /// Reports whether <paramref name="packet"/> carries the random access indicator, which
        /// marks the start of a picture a decoder may begin at.
        /// </summary>
        /// <param name="packet">A whole transport stream packet.</param>
        /// <returns>Whether the random access indicator is set.</returns>
        public static bool SignalsRandomAccess(ReadOnlySpan<byte> packet)
        {
            var adaptationFieldControl = (packet[3] >> 4) & 0x3;
            if (adaptationFieldControl is not (2 or 3))
            {
                return false;
            }

            var adaptationFieldLength = packet[4];
            return adaptationFieldLength > 0 && (packet[5] & 0x40) != 0;
        }

        /// <summary>
        /// Returns the payload of <paramref name="packet"/>, skipping the header and any
        /// adaptation field, or an empty span when the packet carries none.
        /// </summary>
        /// <param name="packet">A whole transport stream packet.</param>
        /// <returns>The payload bytes.</returns>
        public static ReadOnlySpan<byte> ReadPayload(ReadOnlySpan<byte> packet)
        {
            var adaptationFieldControl = (packet[3] >> 4) & 0x3;
            if (adaptationFieldControl is 0 or 2)
            {
                return default;
            }

            var offset = 4;
            if (adaptationFieldControl == 3)
            {
                offset += 1 + packet[4];
            }

            return offset >= packet.Length ? default : packet[offset..];
        }

        /// <summary>
        /// Fills <paramref name="packet"/> with the next whole packet from a stream.
        /// </summary>
        /// <remarks>
        /// A read that returns less than a packet is not the end of anything; only a read that
        /// returns nothing is. Treating a short read as the end would stop a scan part way
        /// through a file for no reason but the size of the operating system's buffer.
        /// </remarks>
        /// <param name="stream">The bytes of a transport stream, aligned to a packet boundary.</param>
        /// <param name="packet">A buffer of <see cref="Length"/> bytes to fill.</param>
        /// <returns><see langword="false"/> when the stream ended before a whole packet.</returns>
        public static bool ReadFrom(Stream stream, byte[] packet)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(packet);

            var filled = 0;
            while (filled < packet.Length)
            {
                var read = stream.Read(packet, filled, packet.Length - filled);
                if (read == 0)
                {
                    return false;
                }

                filled += read;
            }

            return true;
        }
    }
}
