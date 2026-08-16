using System;
using System.Globalization;
using System.Text;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Turns the transport stream TVHeadend delivers mid-broadcast into one a player can
    /// start on, by discarding the DVB EIT PID and everything before the first random
    /// access point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two properties of the raw stream stop clients from playing it. Neither shows up as
    /// an error; playback simply never starts.
    /// </para>
    /// <para>
    /// The EIT PID makes the stream order ambiguous. Jellyfin addresses the stream it wants
    /// FFmpeg to copy by the position of the entry in
    /// <see cref="MediaBrowser.Model.Dto.MediaSourceInfo.MediaStreams"/>, so the probe and the
    /// later transcode have to agree on that order. They do not while the EIT PID is present:
    /// libavformat creates its "epg" stream when the first EIT packet turns up, which lands
    /// either side of the elementary streams announced by the PMT depending on how the
    /// broadcast interleaves them. Everything that remains after dropping it comes from the
    /// PMT, and is therefore created in PMT order every time.
    /// </para>
    /// <para>
    /// Starting mid-GOP stops the client's decoder. A tuner hands over its stream at whatever
    /// packet happens to be next, so the first video samples reference a picture parameter set
    /// that never arrived. FFmpeg tolerates this and resynchronises at the next keyframe, which
    /// is why a server-side transcode still produces a picture. Android's MediaCodec does not:
    /// ExoPlayer feeds it those samples, the decoder consumes them without ever emitting a
    /// frame, and the player stays in its buffering state indefinitely. Withholding the stream
    /// until the first keyframe means the first sample a decoder ever sees is a random access
    /// point.
    /// </para>
    /// </remarks>
    internal sealed class LiveTransportStreamConditioner
    {
        /// <summary>
        /// The DVB Event Information Table PID, which FFmpeg exposes as an "epg" data stream.
        /// </summary>
        public const int EventInformationTablePid = 0x12;

        private const int PacketLength = 188;
        private const byte SyncByte = 0x47;
        private const int ProgramAssociationTablePid = 0x00;

        // A stream that never signals a random access point would otherwise be withheld
        // forever. Roughly two seconds of a high bitrate broadcast is long enough that a
        // keyframe should have been seen, and short enough not to delay a channel change.
        private const int RandomAccessSearchLimit = 4 * 1024 * 1024;

        /// <summary>
        /// Caps the cost of the IDR scan on a long running stream. The decision itself is made
        /// on elapsed time and falls well inside this budget; once it is spent the stream is
        /// simply passed through without further inspection.
        /// </summary>
        internal const int IdrScanLimit = 8 * 1024 * 1024;

        private readonly int _droppedPid;
        private readonly byte[] _partialPacket = new byte[PacketLength];
        private readonly byte[] _programAssociationTable = new byte[PacketLength];
        private readonly byte[] _programMapTable = new byte[PacketLength];

        private int _partialPacketLength;
        private int _programMapTablePid = -1;
        private int _videoPid = -1;
        private bool _hasProgramAssociationTable;
        private bool _hasProgramMapTable;
        private bool _started;
        private int _bytesInspected;

        /// <summary>
        /// Initializes a new instance of the <see cref="LiveTransportStreamConditioner"/> class.
        /// </summary>
        /// <param name="droppedPid">The PID whose packets are removed.</param>
        public LiveTransportStreamConditioner(int droppedPid)
        {
            if (droppedPid is < 0 or > 0x1FFF)
            {
                throw new ArgumentOutOfRangeException(nameof(droppedPid));
            }

            _droppedPid = droppedPid;
        }

        /// <summary>
        /// Gets a value indicating whether a random access point has been reached and the
        /// stream is being passed through.
        /// </summary>
        public bool HasStarted => _started;

        /// <summary>
        /// Gets a value indicating whether an IDR frame has been seen in the video stream.
        /// </summary>
        /// <remarks>
        /// Broadcasters differ here, and it decides whether a client can start at all. ZDF
        /// sends an IDR roughly every 0.6 seconds; the ARD network sends none whatsoever and
        /// signals its random access points as I-frames with a recovery point instead. A
        /// player that may only begin at a sync sample never finds one in the latter, which
        /// is not something re-muxing can repair.
        /// </remarks>
        public bool HasSeenIdrFrame { get; private set; }

        /// <summary>
        /// Gets the number of bytes inspected while looking for an IDR frame.
        /// </summary>
        public int IdrScanBytes { get; private set; }

        /// <summary>
        /// Gets the elementary streams the PMT announces, as "streamtype:pid" in PMT order,
        /// or <see langword="null"/> until the PMT has been parsed.
        /// </summary>
        /// <remarks>
        /// This is what a cached probe result has to be validated against. Jellyfin
        /// addresses the stream FFmpeg should copy by its position in
        /// <see cref="MediaBrowser.Model.Dto.MediaSourceInfo.MediaStreams"/>, so reusing a
        /// probe from an earlier tune is only safe while the broadcast still announces the
        /// same elementary streams in the same order. Services do change their layout --
        /// a second audio track for a film, a subtitle track appearing -- and the fingerprint
        /// catches exactly that, within the first few packets rather than after a probe.
        /// </remarks>
        public string? ProgramLayout { get; private set; }

        /// <summary>
        /// Copies the most recent PAT and PMT into <paramref name="destination"/>.
        /// </summary>
        /// <remarks>
        /// Needed when the conditioned stream is handed to a second reader mid-flight: it has
        /// missed the tables that went out at the start and cannot map the elementary streams
        /// without them.
        /// </remarks>
        /// <param name="destination">The buffer receiving the tables. Two packets are enough.</param>
        /// <returns>The number of bytes written.</returns>
        public int WriteProgramTables(Span<byte> destination)
        {
            var written = 0;
            if (_hasProgramAssociationTable)
            {
                _programAssociationTable.CopyTo(destination);
                written += PacketLength;
            }

            if (_hasProgramMapTable)
            {
                _programMapTable.CopyTo(destination[written..]);
                written += PacketLength;
            }

            return written;
        }

        /// <summary>
        /// Gets the smallest destination size that can hold the conditioned form of a
        /// <paramref name="sourceLength"/> byte chunk.
        /// </summary>
        /// <param name="sourceLength">The length of the chunk about to be conditioned.</param>
        /// <returns>The required destination length.</returns>
        public static int GetMaximumConditionedLength(int sourceLength)
        {
            // A chunk can complete the packet held over from the previous call and, on the
            // chunk that starts the stream, is preceded by the cached PAT and PMT.
            return sourceLength + (3 * PacketLength);
        }

        /// <summary>
        /// Copies <paramref name="source"/> to <paramref name="destination"/>, dropping the
        /// filtered PID and everything ahead of the first random access point. Bytes that do
        /// not complete a packet are held over until the following call.
        /// </summary>
        /// <param name="source">The next chunk of the transport stream.</param>
        /// <param name="destination">The buffer receiving the retained packets.</param>
        /// <returns>The number of bytes written to <paramref name="destination"/>.</returns>
        public int Condition(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            var written = 0;

            if (_partialPacketLength > 0)
            {
                var missing = Math.Min(PacketLength - _partialPacketLength, source.Length);
                source[..missing].CopyTo(_partialPacket.AsSpan(_partialPacketLength));
                _partialPacketLength += missing;
                source = source[missing..];

                if (_partialPacketLength < PacketLength)
                {
                    return 0;
                }

                written += Emit(_partialPacket, destination);
                _partialPacketLength = 0;
            }

            while (!source.IsEmpty)
            {
                if (source[0] != SyncByte)
                {
                    var sync = source.IndexOf(SyncByte);
                    if (sync < 0)
                    {
                        return written;
                    }

                    source = source[sync..];
                    continue;
                }

                if (source.Length < PacketLength)
                {
                    break;
                }

                written += Emit(source[..PacketLength], destination[written..]);
                source = source[PacketLength..];
            }

            source.CopyTo(_partialPacket);
            _partialPacketLength = source.Length;
            return written;
        }

        private static int ReadPid(ReadOnlySpan<byte> packet) => ((packet[1] & 0x1F) << 8) | packet[2];

        private static bool StartsPayloadUnit(ReadOnlySpan<byte> packet) => (packet[1] & 0x40) != 0;

        private static bool SignalsRandomAccess(ReadOnlySpan<byte> packet)
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
        /// Returns the section payload of a PSI packet, or an empty span when the packet does
        /// not begin one.
        /// </summary>
        private static ReadOnlySpan<byte> ReadSection(ReadOnlySpan<byte> packet)
        {
            if (!StartsPayloadUnit(packet))
            {
                return default;
            }

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

            // A packet that starts a section begins with a pointer to it.
            if (offset >= packet.Length)
            {
                return default;
            }

            offset += 1 + packet[offset];
            return offset >= packet.Length ? default : packet[offset..];
        }

        private static bool IsVideoStreamType(byte streamType) => streamType is 0x01 or 0x02 or 0x10 or 0x1B or 0x24;

        /// <summary>
        /// Reports whether the packet payload starts an H.264 IDR NAL unit.
        /// </summary>
        private static bool ContainsIdrNalUnit(ReadOnlySpan<byte> packet)
        {
            for (var offset = 4; offset + 3 < packet.Length; offset++)
            {
                if (packet[offset] != 0x00 || packet[offset + 1] != 0x00 || packet[offset + 2] != 0x01)
                {
                    continue;
                }

                if ((packet[offset + 3] & 0x1F) == 5)
                {
                    return true;
                }
            }

            return false;
        }

        private int Emit(ReadOnlySpan<byte> packet, Span<byte> destination)
        {
            var pid = ReadPid(packet);
            if (pid == _droppedPid)
            {
                return 0;
            }

            if (pid == ProgramAssociationTablePid)
            {
                packet.CopyTo(_programAssociationTable);
                _hasProgramAssociationTable = true;
                ReadProgramMapTablePid(packet);
            }
            else if (pid == _programMapTablePid)
            {
                packet.CopyTo(_programMapTable);
                _hasProgramMapTable = true;
                ReadVideoPid(packet);
            }

            if (pid == _videoPid && !HasSeenIdrFrame && IdrScanBytes < IdrScanLimit)
            {
                IdrScanBytes += PacketLength;
                if (ContainsIdrNalUnit(packet))
                {
                    HasSeenIdrFrame = true;
                }
            }

            if (_started)
            {
                packet.CopyTo(destination);
                return PacketLength;
            }

            _bytesInspected += PacketLength;
            if (!ShouldStartAt(packet, pid))
            {
                return 0;
            }

            _started = true;

            // The player needs the tables before it can make sense of the elementary
            // streams, and both were withheld along with everything else.
            var written = 0;
            if (_hasProgramAssociationTable)
            {
                _programAssociationTable.CopyTo(destination);
                written += PacketLength;
            }

            if (_hasProgramMapTable)
            {
                _programMapTable.CopyTo(destination[written..]);
                written += PacketLength;
            }

            packet.CopyTo(destination[written..]);
            return written + PacketLength;
        }

        private bool ShouldStartAt(ReadOnlySpan<byte> packet, int pid)
        {
            if (!_hasProgramAssociationTable || !_hasProgramMapTable || pid != _videoPid)
            {
                return false;
            }

            if (StartsPayloadUnit(packet) && SignalsRandomAccess(packet))
            {
                return true;
            }

            // Fall back to any access unit boundary rather than withholding a stream whose
            // broadcaster does not set the random access indicator.
            return _bytesInspected >= RandomAccessSearchLimit && StartsPayloadUnit(packet);
        }

        private void ReadProgramMapTablePid(ReadOnlySpan<byte> packet)
        {
            var section = ReadSection(packet);
            if (section.Length < 12 || section[0] != 0x00)
            {
                return;
            }

            var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
            var end = Math.Min(3 + sectionLength - 4, section.Length);
            for (var offset = 8; offset + 4 <= end; offset += 4)
            {
                var programNumber = (section[offset] << 8) | section[offset + 1];
                if (programNumber == 0)
                {
                    // The network information table, not a program.
                    continue;
                }

                _programMapTablePid = ((section[offset + 2] & 0x1F) << 8) | section[offset + 3];
                return;
            }
        }

        private void ReadVideoPid(ReadOnlySpan<byte> packet)
        {
            var section = ReadSection(packet);
            if (section.Length < 16 || section[0] != 0x02)
            {
                return;
            }

            var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
            var end = Math.Min(3 + sectionLength - 4, section.Length);
            var programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
            var offset = 12 + programInfoLength;

            var layout = new StringBuilder();
            var videoPid = -1;
            while (offset + 5 <= end)
            {
                var streamType = section[offset];
                var elementaryPid = ((section[offset + 1] & 0x1F) << 8) | section[offset + 2];
                var elementaryInfoLength = ((section[offset + 3] & 0x0F) << 8) | section[offset + 4];

                if (layout.Length > 0)
                {
                    layout.Append(',');
                }

                layout.Append(CultureInfo.InvariantCulture, $"{streamType:x2}:{elementaryPid:x4}");

                if (videoPid < 0 && IsVideoStreamType(streamType))
                {
                    videoPid = elementaryPid;
                }

                offset += 5 + elementaryInfoLength;
            }

            if (videoPid >= 0)
            {
                _videoPid = videoPid;
            }

            if (layout.Length > 0)
            {
                ProgramLayout = layout.ToString();
            }
        }
    }
}
