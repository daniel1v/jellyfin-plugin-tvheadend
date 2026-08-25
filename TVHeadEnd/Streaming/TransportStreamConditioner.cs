using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Turns the transport stream TVHeadend delivers mid-broadcast into one a player can start on.
/// </summary>
/// <remarks>
/// <para>
/// Three jobs, and deliberately no fourth. It drops the DVB Event Information Table, it withholds
/// the stream until a point a decoder may begin at, and it captures the program tables so a
/// reader joining later can be given them. What the stream <em>contains</em> is not its business:
/// that is the program map, which this parses on the way past and hands on unchanged.
/// </para>
/// <para>
/// The EIT has to go because it makes the stream order ambiguous. libavformat creates its "epg"
/// stream the moment the first EIT packet turns up, which lands either side of the elementary
/// streams depending on how the broadcast happens to interleave them, and every stream index
/// after it shifts. What remains once it is dropped comes from the program map alone, and is
/// therefore numbered in program map order every time.
/// </para>
/// <para>
/// Starting mid-picture stops the decoder. A tuner hands its stream over at whatever packet is
/// next, so the first video samples reference a parameter set that never arrived. Withholding
/// everything until the first random access point means the first sample a decoder sees is one
/// it may begin at.
/// </para>
/// </remarks>
public sealed class TransportStreamConditioner
{
    /// <summary>
    /// The DVB Event Information Table PID, which FFmpeg exposes as an "epg" data stream.
    /// </summary>
    public const int EventInformationTablePid = TransportStreamPacket.EventInformationTablePid;

    /// <summary>
    /// A stream that never signals a random access point would otherwise be withheld for ever.
    /// The wait is bounded by time first: four megabytes is about two seconds of a high bitrate
    /// broadcast but sixteen of a standard definition one, which turned a safety net into the
    /// slowest thing about tuning such a channel. The byte limit stays as a second bound so a
    /// very high bitrate stream cannot buffer without end.
    /// </summary>
    private const int RandomAccessSearchLimit = 4 * 1024 * 1024;

    /// <summary>
    /// The PMT stream type of H.264 video. The only one the IDR question applies to.
    /// </summary>
    private const byte H264StreamType = 0x1B;

    private static readonly TimeSpan RandomAccessSearchTimeLimit = TimeSpan.FromSeconds(2);

    private readonly int _droppedPid;
    private readonly byte[] _partialPacket = new byte[TransportStreamPacket.Length];
    private readonly List<int> _randomAccessOffsets = [];

    private readonly H264AccessUnitScanner _accessUnitScanner = new();
    private readonly PsiSectionAssembler _programAssociationSection = new();
    private readonly PsiSectionAssembler _programMapSection = new();

    private byte[][] _programAssociationPackets = [];
    private byte[][] _programMapPackets = [];

    private int _partialPacketLength;
    private int _programMapTablePid = -1;
    private bool _started;
    private bool _readingStartAccessUnit;
    private int _videoUnitsSinceStart;
    private int _generationStartOffset = -1;
    private int _bytesInspected;
    private long _firstInspectedTimestamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransportStreamConditioner"/> class.
    /// </summary>
    /// <param name="droppedPid">The PID whose packets are removed, or -1 to drop none.</param>
    /// <param name="startImmediately">
    /// Whether to forward from the first packet instead of withholding until a random access
    /// point. For reading a file that already starts at one.
    /// </param>
    public TransportStreamConditioner(int droppedPid, bool startImmediately = false)
    {
        if (droppedPid is < -1 or > 0x1FFF)
        {
            throw new ArgumentOutOfRangeException(nameof(droppedPid));
        }

        _droppedPid = droppedPid;
        _started = startImmediately;
    }

    /// <summary>
    /// Gets a value indicating whether the stream is being passed through.
    /// </summary>
    /// <remarks>
    /// Says nothing about how it started. <see cref="StartedOnRandomAccessPoint"/> is the
    /// question of whether the first packet delivered was one a decoder may begin at.
    /// </remarks>
    public bool HasStarted => _started;

    /// <summary>
    /// Gets a value indicating whether delivery began at a packet the broadcast marked as a
    /// random access point.
    /// </summary>
    /// <remarks>
    /// False when the search gave up and started at a bare payload unit start instead. That is a
    /// guess about where a picture begins, and while it is a reasonable one to deliver from, it
    /// is never recorded as a place a later reader may join.
    /// </remarks>
    public bool StartedOnRandomAccessPoint { get; private set; }

    /// <summary>
    /// Gets whether the access unit delivery started at carried an IDR picture, or
    /// <see langword="null"/> when the question does not apply or has not been settled yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null has three meanings, and none of them is "no": the video is not H.264, so the question
    /// is about a syntax the stream does not use; delivery began somewhere other than a signalled
    /// access point; or the access unit has not finished arriving. Only a settled
    /// <see langword="false"/> states the thing worth acting on -- a broadcast that signals random
    /// access at a recovery point, with no IDR anywhere in the picture it points at.
    /// </para>
    /// <para>
    /// Read once, at the start. This is not a running census of the stream and is not remembered
    /// across channels.
    /// </para>
    /// </remarks>
    public bool? StartAccessUnitCarriesIdr { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the broadcaster has changed the program layout since the
    /// stream started. Cleared by <see cref="AcknowledgeProgramLayoutChange"/>.
    /// </summary>
    public bool ProgramLayoutChanged { get; private set; }

    /// <summary>
    /// Gets which program layout the tables and access points now being produced belong to.
    /// </summary>
    /// <remarks>
    /// Counts up whenever the broadcaster changes what the stream contains. Everything downstream
    /// carries it, so tables and join points can only ever be stored together when they came from
    /// the same layout -- an invariant a reader can rely on rather than an ordering the writer has
    /// to remember to get right.
    /// </remarks>
    public int ProgramLayoutGeneration { get; private set; }

    /// <summary>
    /// Gets the program map of the stream, or <see langword="null"/> until a complete one has
    /// been reassembled.
    /// </summary>
    public ProgramMapTable? ProgramMap { get; private set; }

    /// <summary>
    /// Gets the PID of the video elementary stream, or -1 until the program map has been read.
    /// </summary>
    public int VideoPid => ProgramMap?.VideoPid ?? -1;

    /// <summary>
    /// Gets the stream type of the video, or zero until the program map has been read.
    /// </summary>
    /// <remarks>
    /// Kept because it decides which analysis of the video is even meaningful. Losing it is what
    /// once let an H.264 IDR scan run over MPEG-2 slice start codes, where the same byte pattern
    /// occurs by coincidence.
    /// </remarks>
    public byte VideoStreamType => ProgramMap?.VideoStreamType ?? 0;

    /// <summary>
    /// Gets the offsets within the destination of the last <see cref="Condition"/> call at which
    /// a decoder may start.
    /// </summary>
    public IReadOnlyList<int> RandomAccessOffsets => _randomAccessOffsets;

    /// <summary>
    /// Gets a value indicating whether both program tables have been captured whole.
    /// </summary>
    public bool HasProgramTables => _programAssociationPackets.Length > 0 && _programMapPackets.Length > 0;

    /// <summary>
    /// Clears <see cref="ProgramLayoutChanged"/>, for a caller that has acted on it.
    /// </summary>
    public void AcknowledgeProgramLayoutChange() => ProgramLayoutChanged = false;

    /// <summary>
    /// Gets the smallest destination size that can hold the conditioned form of a chunk.
    /// </summary>
    /// <param name="sourceLength">The length of the chunk about to be conditioned.</param>
    /// <returns>The required destination length.</returns>
    public static int GetMaximumConditionedLength(int sourceLength)
    {
        // A chunk can complete the packet held over from the previous call, and the chunk that
        // starts the stream is preceded by the captured tables. Both tables can span several
        // packets, so the whole of the largest pair either could be is allowed for.
        const int MaximumTablePackets = 2 * 8;
        return sourceLength + ((MaximumTablePackets + 2) * TransportStreamPacket.Length);
    }

    /// <summary>
    /// Copies the captured program tables into a buffer.
    /// </summary>
    /// <param name="destination">The buffer receiving the tables.</param>
    /// <returns>The number of bytes written.</returns>
    public int WriteProgramTables(Span<byte> destination)
    {
        var written = 0;
        foreach (var packet in _programAssociationPackets.Concat(_programMapPackets))
        {
            packet.CopyTo(destination[written..]);
            written += TransportStreamPacket.Length;
        }

        return written;
    }

    /// <summary>
    /// Takes the program tables as they stand, so they can be published together with the access
    /// points found in the same chunk.
    /// </summary>
    /// <returns>The snapshot, empty until both tables have arrived.</returns>
    public ProgramTableSnapshot TakeProgramTables()
        => HasProgramTables
            ? new ProgramTableSnapshot(
                _programAssociationPackets,
                _programMapPackets,
                ProgramLayoutGeneration,
                _generationStartOffset)
            : ProgramTableSnapshot.Empty;

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/>, dropping the filtered
    /// PID and everything ahead of the first random access point. Bytes that do not complete a
    /// packet are held over until the following call.
    /// </summary>
    /// <param name="source">The next chunk of the transport stream.</param>
    /// <param name="destination">The buffer receiving the retained packets.</param>
    /// <returns>The number of bytes written to <paramref name="destination"/>.</returns>
    public int Condition(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var written = 0;
        _randomAccessOffsets.Clear();
        _generationStartOffset = -1;

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

        var generationBefore = ProgramLayoutGeneration;

        if (pid == TransportStreamPacket.ProgramAssociationTablePid)
        {
            CaptureProgramAssociation(packet);
        }
        else if (pid == _programMapTablePid)
        {
            CaptureProgramMap(packet);
        }

        if (ProgramLayoutGeneration != generationBefore)
        {
            // Where the new layout begins in this chunk's output, which is this packet. The
            // chunk boundary is not a usable approximation: a layout change lands wherever the
            // broadcaster put the table, and everything emitted before it in the same chunk is
            // still the programme before.
            _generationStartOffset = destinationOffset;
        }

        var videoPid = VideoPid;
        var isRandomAccessPoint = pid == videoPid
            && TransportStreamPacket.StartsPayloadUnit(packet)
            && TransportStreamPacket.SignalsRandomAccess(packet);

        if (_started)
        {
            packet.CopyTo(destination);
            if (isRandomAccessPoint)
            {
                _randomAccessOffsets.Add(destinationOffset);
            }

            ObserveStartAccessUnit(packet, pid);
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
        StartedOnRandomAccessPoint = isRandomAccessPoint;

        // Whether the picture delivery begins with is one a decoder can start cold on. Asked only
        // of H.264, and only when the broadcast said this was an access point at all.
        if (isRandomAccessPoint && VideoStreamType == H264StreamType)
        {
            _readingStartAccessUnit = true;
            _videoUnitsSinceStart = 0;
            _accessUnitScanner.Reset();
            _accessUnitScanner.Scan(TransportStreamPacket.ReadPayload(packet));
            Settle();
        }

        // The player needs the tables before it can make sense of the elementary streams, and
        // both were withheld along with everything else.
        var written = WriteProgramTables(destination);
        packet.CopyTo(destination[written..]);

        // Recorded as a place a decoder may join only when the broadcast actually said so. The
        // start may be a bare payload unit start accepted because the search ran out of
        // patience, and that is a guess: a picture start is not a random access point unless
        // the adaptation field says it is. Delivering from a guess is recoverable -- the first
        // seconds may not decode -- but storing it would hand every later reader the same bad
        // position for as long as it stays inside the window.
        if (isRandomAccessPoint)
        {
            _randomAccessOffsets.Add(destinationOffset + written);
        }

        return written + TransportStreamPacket.Length;
    }

    /// <summary>
    /// Follows the access unit delivery began at until it is possible to say whether it carried an
    /// IDR picture.
    /// </summary>
    private void ObserveStartAccessUnit(ReadOnlySpan<byte> packet, int pid)
    {
        if (!_readingStartAccessUnit || pid != VideoPid)
        {
            return;
        }

        if (TransportStreamPacket.StartsPayloadUnit(packet))
        {
            _videoUnitsSinceStart++;
        }

        _accessUnitScanner.Scan(TransportStreamPacket.ReadPayload(packet));
        Settle();
    }

    /// <summary>
    /// Settles whether the picture delivery began at carried an IDR, once that can be said.
    /// </summary>
    /// <remarks>
    /// The scanner ends the access unit where the syntax ends it -- a second access unit delimiter,
    /// or a slice that starts a new picture -- which on the broadcasts measured falls in the PES
    /// after the one the entry point is in. The payload unit count is only a bound -- the next PES
    /// begins a new picture in every broadcast measured -- so a stream
    /// whose access unit never closes settles conservatively instead of leaving the open waiting.
    /// </remarks>
    private void Settle()
    {
        const int MaximumPayloadUnits = 1;

        if (!_accessUnitScanner.Completed && _videoUnitsSinceStart < MaximumPayloadUnits)
        {
            return;
        }

        StartAccessUnitCarriesIdr = _accessUnitScanner.CarriesIdr;
        _readingStartAccessUnit = false;
    }

    private bool ShouldStartAt(ReadOnlySpan<byte> packet, int pid)
    {
        if (!HasProgramTables)
        {
            return false;
        }

        // A program with no video to wait for. A radio service is the ordinary case; a television
        // service whose video the program map did not identify is the other, and withholding that
        // one until a video packet arrives would withhold it for ever -- the point of publishing
        // it undescribed is to let Jellyfin inspect it, which it cannot do if nothing is
        // delivered.
        if (VideoPid < 0)
        {
            return true;
        }

        if (pid != VideoPid)
        {
            return false;
        }

        if (TransportStreamPacket.StartsPayloadUnit(packet) && TransportStreamPacket.SignalsRandomAccess(packet))
        {
            return true;
        }

        // Fall back to any access unit boundary rather than withholding a stream whose broadcaster
        // does not set the random access indicator at all.
        if (!TransportStreamPacket.StartsPayloadUnit(packet))
        {
            return false;
        }

        return _bytesInspected >= RandomAccessSearchLimit
            || Stopwatch.GetElapsedTime(_firstInspectedTimestamp) >= RandomAccessSearchTimeLimit;
    }

    private void CaptureProgramAssociation(ReadOnlySpan<byte> packet)
    {
        if (!_programAssociationSection.Accept(packet))
        {
            return;
        }

        foreach (var section in _programAssociationSection.Completed)
        {
            CaptureProgramAssociation(section);
        }
    }

    private void CaptureProgramAssociation(PsiSection section)
    {
        var table = ProgramAssociationTable.Parse(section.Bytes);
        if (table is null)
        {
            // Damaged, announced for later, or naming no program. Whatever is already in hand
            // still describes the stream that is arriving, so none of it is disturbed -- not the
            // program map PID, not the stored packets.
            return;
        }

        if (_programMapTablePid >= 0 && table.ProgramMapPid != _programMapTablePid)
        {
            // The broadcaster has moved the program map. Everything read from the old PID
            // describes a program this table no longer points at, so it is put aside until the
            // map at the new PID arrives; publishing the new PAT beside the old PMT would be a
            // pairing that never existed on air.
            _programMapSection.Reset();
            _programMapPackets = [];
            ProgramMap = null;
            BeginNewProgramLayout();
        }

        _programMapTablePid = table.ProgramMapPid;
        _programAssociationPackets = section.Packets;
    }

    private void CaptureProgramMap(ReadOnlySpan<byte> packet)
    {
        if (!_programMapSection.Accept(packet))
        {
            return;
        }

        foreach (var section in _programMapSection.Completed)
        {
            CaptureProgramMap(section);
        }
    }

    private void CaptureProgramMap(PsiSection section)
    {
        var table = ProgramMapTable.Parse(section.Bytes);
        if (table is null)
        {
            // Damaged, or announced for later. The table already in hand still describes the
            // stream that is arriving, so it stays.
            return;
        }

        // A broadcaster changing the program layout mid-stream -- adding an audio track, moving
        // to a different video PID at the end of a programme -- makes every entry point found
        // under the old table useless: a reader sent there would be given the new tables and the
        // old picture. Which of them changed is not worth tracking; that any of them did is
        // enough to start the index again.
        var layoutChanged = ProgramMap is { } previous && !DescribesSameStreams(previous, table);

        ProgramMap = table;
        _programMapPackets = section.Packets;

        if (layoutChanged)
        {
            BeginNewProgramLayout();
        }
    }

    /// <summary>
    /// Marks everything read under the previous program layout as belonging to it and no longer
    /// to the stream being delivered.
    /// </summary>
    /// <remarks>
    /// The access points already found in the chunk being conditioned go with it. They were
    /// recorded under the old tables, and the chunk is published with the new ones, so keeping
    /// them would hand a joining reader precisely the pairing this exists to prevent -- the new
    /// description and the old picture -- and it would do so inside a single call, where no
    /// amount of discarding afterwards can reach them.
    /// </remarks>
    private void BeginNewProgramLayout()
    {
        _randomAccessOffsets.Clear();
        ProgramLayoutGeneration++;
        ProgramLayoutChanged = true;
    }

    /// <summary>
    /// Reports whether two program maps would produce the same published description.
    /// </summary>
    /// <remarks>
    /// Everything that reaches a media source is compared, not just the PID and the stream type.
    /// A broadcaster that swaps a track's language, or turns a private stream into an identified
    /// AC-3 one by adding a descriptor, has changed what a client is told it is playing; treating
    /// that as "the same layout" would leave viewers with a description of the programme before.
    /// </remarks>
    private static bool DescribesSameStreams(ProgramMapTable first, ProgramMapTable second)
    {
        if (first.Entries.Count != second.Entries.Count)
        {
            return false;
        }

        // The clock the decoder times everything against. Moving it to another PID makes every
        // access point found under the old table useless to a decoder joining there, whatever the
        // elementary streams do -- and since this comparison now guards the bootstrap and not only
        // the description, that counts as a different layout.
        if (first.PcrPid != second.PcrPid)
        {
            return false;
        }

        for (var index = 0; index < first.Entries.Count; index++)
        {
            var before = first.Entries[index];
            var after = second.Entries[index];

            if (before.Pid != after.Pid
                || before.StreamType != after.StreamType
                || before.Kind != after.Kind
                || !string.Equals(before.Codec, after.Codec, StringComparison.Ordinal)
                || !string.Equals(before.Language, after.Language, StringComparison.Ordinal)
                || before.IsHearingImpaired != after.IsHearingImpaired)
            {
                return false;
            }
        }

        return true;
    }
}
