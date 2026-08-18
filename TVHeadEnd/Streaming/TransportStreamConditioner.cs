using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Turns the transport stream TVHeadend delivers mid-broadcast into one a player can start
    /// on, by discarding the DVB EIT PID and everything before the first random access point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two properties of the raw stream stop clients from playing it. Neither shows up as an
    /// error; playback simply never starts.
    /// </para>
    /// <para>
    /// The EIT PID makes the stream order ambiguous. Jellyfin addresses the stream it wants
    /// FFmpeg to copy by the position of the entry in
    /// <see cref="MediaBrowser.Model.Dto.MediaSourceInfo.MediaStreams"/>, so the
    /// later transcode have to agree on that order. They do not while the EIT PID is present:
    /// libavformat creates its "epg" stream when the first EIT packet turns up, which lands
    /// either side of the elementary streams announced by the PMT depending on how the
    /// broadcast interleaves them. Everything that remains after dropping it comes from the
    /// PMT, and is therefore created in PMT order every time.
    /// </para>
    /// <para>
    /// Starting mid-GOP stops the client's decoder. A tuner hands over its stream at whatever
    /// packet happens to be next, so the first video samples reference a picture parameter set
    /// that never arrived. Withholding the stream until the first random access point means the
    /// first sample a decoder ever sees is one it may begin at.
    /// </para>
    /// <para>
    /// What this class deliberately does not do is judge the video. It reports the codec the
    /// PMT announces;
    /// deciding what that codec implies is not a transport stream question.
    /// </para>
    /// </remarks>
    public sealed class TransportStreamConditioner
    {
        /// <summary>
        /// The DVB Event Information Table PID, which FFmpeg exposes as an "epg" data stream.
        /// </summary>
        public const int EventInformationTablePid = TransportStreamPacket.EventInformationTablePid;

        // A stream that never signals a random access point would otherwise be withheld forever.
        // The wait is bounded by time rather than by volume: four megabytes is about two seconds
        // of a high bitrate broadcast but sixteen of a standard definition one, which turned a
        // safety net into the slowest thing about tuning such a channel. The byte limit is kept
        // as a second bound so a very high bitrate stream cannot buffer without end.
        private const int RandomAccessSearchLimit = 4 * 1024 * 1024;

        private static readonly TimeSpan RandomAccessSearchTimeLimit = TimeSpan.FromSeconds(2);

        private readonly int _droppedPid;
        private readonly byte[] _partialPacket = new byte[TransportStreamPacket.Length];
        private readonly byte[] _programAssociationTable = new byte[TransportStreamPacket.Length];
        private readonly byte[] _programMapTable = new byte[TransportStreamPacket.Length];
        private readonly List<int> _randomAccessOffsets = [];

        private int _partialPacketLength;
        private int _programMapTablePid = -1;
        private bool _hasProgramAssociationTable;
        private bool _hasProgramMapTable;
        private bool _started;
        private int _bytesInspected;
        private long _firstInspectedTimestamp;

        /// <summary>
        /// Initializes a new instance of the <see cref="TransportStreamConditioner"/> class.
        /// </summary>
        /// <param name="droppedPid">The PID whose packets are removed.</param>
        /// <param name="startImmediately">
        /// Whether to forward from the first packet instead of withholding until a random access
        /// point.
        /// </param>
        public TransportStreamConditioner(
            int droppedPid,
            bool startImmediately = false)
        {
            if (droppedPid is < -1 or > 0x1FFF)
            {
                throw new ArgumentOutOfRangeException(nameof(droppedPid));
            }

            _droppedPid = droppedPid;
            _started = startImmediately;
        }

        /// <summary>
        /// Gets a value indicating whether a random access point has been reached and the
        /// stream is being passed through.
        /// </summary>
        public bool HasStarted => _started;

        /// <summary>
        /// Gets how far into the source the first packet passed through was found.
        /// </summary>
        public int StartOffset => _started ? Math.Max(0, _bytesInspected - TransportStreamPacket.Length) : 0;

        /// <summary>
        /// Gets the PID of the video elementary stream, or -1 until the PMT has been parsed.
        /// </summary>
        public int VideoPid { get; private set; } = -1;

        /// <summary>
        /// Gets the PMT stream type of the video, or zero until the PMT has been parsed.
        /// </summary>
        /// <remarks>
        /// Kept because it decides which analysis of the video is even meaningful. Losing it is
        /// what let an H.264 IDR scan run over MPEG-2 slice start codes.
        /// </remarks>
        public byte VideoStreamType { get; private set; }

        /// <summary>
        /// Gets the offsets within the destination of the last <see cref="Condition"/> call at
        /// which a decoder may start.
        /// </summary>
        public IReadOnlyList<int> RandomAccessOffsets => _randomAccessOffsets;

        /// <summary>
        /// Gets the elementary streams the PMT announces, as "streamtype:pid" in PMT order,
        /// or <see langword="null"/> until the PMT has been parsed.
        /// </summary>
        /// <remarks>
        /// A fingerprint of the program layout, available within the first packets. It is proof
        /// that a broadcast still announces the same elementary streams in the same order --
        /// and nothing more. In particular it says nothing about the random access properties
        /// of the video, which is why it must never be used to skip that check.
        /// </remarks>
        public string? ProgramLayout { get; private set; }

        /// <summary>
        /// Gets a value indicating whether both program tables have been seen.
        /// </summary>
        public bool HasProgramTables => _hasProgramAssociationTable && _hasProgramMapTable;

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
            return sourceLength + (3 * TransportStreamPacket.Length);
        }

        /// <summary>
        /// Copies the most recent PAT and PMT into <paramref name="destination"/>.
        /// </summary>
        /// <param name="destination">The buffer receiving the tables. Two packets are enough.</param>
        /// <returns>The number of bytes written.</returns>
        public int WriteProgramTables(Span<byte> destination)
        {
            var written = 0;
            if (_hasProgramAssociationTable)
            {
                _programAssociationTable.CopyTo(destination);
                written += TransportStreamPacket.Length;
            }

            if (_hasProgramMapTable)
            {
                _programMapTable.CopyTo(destination[written..]);
                written += TransportStreamPacket.Length;
            }

            return written;
        }

        /// <summary>
        /// Records the tables seen so far into <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The bootstrap index of the buffer being filled.</param>
        public void PublishProgramTables(StreamBootstrapIndex index)
        {
            ArgumentNullException.ThrowIfNull(index);

            if (_hasProgramAssociationTable)
            {
                index.RecordProgramAssociationTable(_programAssociationTable);
            }

            if (_hasProgramMapTable)
            {
                index.RecordProgramMapTable(_programMapTable);
            }
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
            _randomAccessOffsets.Clear();

            if (_partialPacketLength > 0)
            {
                var missing = Math.Min(TransportStreamPacket.Length - _partialPacketLength, source.Length);
                source[..missing].CopyTo(_partialPacket.AsSpan(_partialPacketLength));
                _partialPacketLength += missing;
                source = source[missing..];

                if (_partialPacketLength < TransportStreamPacket.Length)
                {
                    return 0;
                }

                written += Emit(_partialPacket, destination, written);
                _partialPacketLength = 0;
            }

            while (!source.IsEmpty)
            {
                if (source[0] != TransportStreamPacket.SyncByte)
                {
                    var sync = source.IndexOf(TransportStreamPacket.SyncByte);
                    if (sync < 0)
                    {
                        return written;
                    }

                    source = source[sync..];
                    continue;
                }

                if (source.Length < TransportStreamPacket.Length)
                {
                    break;
                }

                written += Emit(source[..TransportStreamPacket.Length], destination[written..], written);
                source = source[TransportStreamPacket.Length..];
            }

            source.CopyTo(_partialPacket);
            _partialPacketLength = source.Length;
            return written;
        }

        private int Emit(ReadOnlySpan<byte> packet, Span<byte> destination, int destinationOffset)
        {
            var pid = TransportStreamPacket.ReadPid(packet);
            if (pid == _droppedPid)
            {
                return 0;
            }

            if (pid == TransportStreamPacket.ProgramAssociationTablePid)
            {
                packet.CopyTo(_programAssociationTable);
                _hasProgramAssociationTable = true;
                ReadProgramMapTablePid(packet);
            }
            else if (pid == _programMapTablePid)
            {
                packet.CopyTo(_programMapTable);
                _hasProgramMapTable = true;
                ReadVideoStream(packet);
            }

            var isRandomAccessPoint = pid == VideoPid
                && TransportStreamPacket.StartsPayloadUnit(packet)
                && TransportStreamPacket.SignalsRandomAccess(packet);

            if (_started)
            {
                packet.CopyTo(destination);
                if (isRandomAccessPoint)
                {
                    _randomAccessOffsets.Add(destinationOffset);
                }

                return TransportStreamPacket.Length;
            }

            if (_firstInspectedTimestamp == 0)
            {
                _firstInspectedTimestamp = Stopwatch.GetTimestamp();
            }

            _bytesInspected += TransportStreamPacket.Length;
            if (!ShouldStartAt(packet, pid))
            {
                return 0;
            }

            _started = true;

            // The player needs the tables before it can make sense of the elementary
            // streams, and both were withheld along with everything else.
            var written = WriteProgramTables(destination);
            packet.CopyTo(destination[written..]);
            _randomAccessOffsets.Add(destinationOffset + written);
            return written + TransportStreamPacket.Length;
        }

        private bool ShouldStartAt(ReadOnlySpan<byte> packet, int pid)
        {
            if (!_hasProgramAssociationTable || !_hasProgramMapTable || pid != VideoPid)
            {
                return false;
            }

            if (TransportStreamPacket.StartsPayloadUnit(packet) && TransportStreamPacket.SignalsRandomAccess(packet))
            {
                return true;
            }

            // Fall back to any access unit boundary rather than withholding a stream whose
            // broadcaster does not set the random access indicator.
            if (!TransportStreamPacket.StartsPayloadUnit(packet))
            {
                return false;
            }

            return _bytesInspected >= RandomAccessSearchLimit
                || Stopwatch.GetElapsedTime(_firstInspectedTimestamp) >= RandomAccessSearchTimeLimit;
        }

        private void ReadProgramMapTablePid(ReadOnlySpan<byte> packet)
        {
            var section = TransportStreamPacket.ReadSection(packet);
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

        private void ReadVideoStream(ReadOnlySpan<byte> packet)
        {
            var section = TransportStreamPacket.ReadSection(packet);
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
            byte videoStreamType = 0;
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

                if (videoPid < 0 && TransportStreamPacket.IsVideoStreamType(streamType))
                {
                    videoPid = elementaryPid;
                    videoStreamType = streamType;
                }

                offset += 5 + elementaryInfoLength;
            }

            if (videoPid >= 0)
            {
                VideoPid = videoPid;
                VideoStreamType = videoStreamType;
            }

            if (layout.Length > 0)
            {
                ProgramLayout = layout.ToString();
            }
        }
    }
}
