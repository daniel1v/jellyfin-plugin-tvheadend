using System;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Reads the fields of a 188-byte MPEG transport stream packet.
    /// </summary>
    /// <remarks>
    /// Shared rather than repeated: the conditioner decides what to forward, the indexer
    /// decides where a late reader may join, and both have to agree on what a packet says.
    /// </remarks>
    internal static class TransportStreamPacket
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
        /// Returns the section payload of a PSI packet, or an empty span when the packet does
        /// not begin one.
        /// </summary>
        /// <param name="packet">A whole transport stream packet.</param>
        /// <returns>The section bytes.</returns>
        public static ReadOnlySpan<byte> ReadSection(ReadOnlySpan<byte> packet)
        {
            if (!StartsPayloadUnit(packet))
            {
                return default;
            }

            var payload = ReadPayload(packet);

            // A packet that starts a section begins with a pointer to it.
            if (payload.IsEmpty)
            {
                return default;
            }

            var offset = 1 + payload[0];
            return offset >= payload.Length ? default : payload[offset..];
        }

        /// <summary>
        /// Reports whether <paramref name="streamType"/> names a video elementary stream.
        /// </summary>
        /// <param name="streamType">A PMT stream type.</param>
        /// <returns>Whether it is video.</returns>
        public static bool IsVideoStreamType(byte streamType)
            => streamType is 0x01 or 0x02 or 0x10 or 0x1B or 0x24;
    }
}
