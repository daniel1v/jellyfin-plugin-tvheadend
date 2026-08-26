using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// What a viewer joining a broadcast already in progress is handed.
/// </summary>
/// <remarks>
/// Measured failure this exists for: the Android client was correctly told it could direct play
/// the broadcast, fetched it, and answered
/// <c>UnrecognizedInputFormatException: None of the available extractors could read the stream</c>
/// within 170 ms. A transport stream is recognised by sync bytes exactly one packet apart
/// starting at the first byte, so anything that hands a reader a partial packet -- or nothing
/// that resembles one -- is unplayable however good the rest of the pipeline is.
/// </remarks>
public sealed class LateJoinTests : IDisposable
{
    private const int PacketSize = 188;
    private const int VideoPid = 0x0100;
    private const int ProgramMapPid = 0x1000;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"latejoin-{Guid.NewGuid():N}.ts");

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // The test already made its point.
        }
    }

    [Fact]
    public async Task AViewerJoiningLateGetsASyncAlignedTransportStream()
    {
        await using var buffer = await FillBroadcast();

        using var reader = buffer.OpenReader();
        var start = await ReadUpTo(reader, 16 * PacketSize);

        Assert.NotEmpty(start);
        AssertSyncAligned(start);
    }

    [Fact]
    public async Task AViewerJoiningLateGetsTheProgramTablesFirst()
    {
        // Without them a decoder has no way to know which elementary streams the packets belong
        // to, and the tables were sent once, long before this reader arrived.
        await using var buffer = await FillBroadcast();

        using var reader = buffer.OpenReader();
        var start = await ReadUpTo(reader, 4 * PacketSize);

        Assert.True(start.Length >= 2 * PacketSize, "the reader delivered less than the program tables");
        Assert.Equal(0, ReadPid(start, 0));
        Assert.Equal(ProgramMapPid, ReadPid(start, PacketSize));
    }

    [Fact]
    public async Task AViewerJoiningLateGetsSomethingRatherThanWaitingAtTheWriteHead()
    {
        // Joining at the newest access point is only safe if there is something after it. A
        // reader positioned exactly at the write head returns nothing, and a player that is
        // handed nothing cannot recognise the container.
        await using var buffer = await FillBroadcast();

        using var reader = buffer.OpenReader();
        var start = await ReadUpTo(reader, 8 * PacketSize);

        Assert.True(
            start.Length >= 3 * PacketSize,
            $"the reader delivered only {start.Length} bytes, too few to recognise a transport stream");
    }

    private static void AssertSyncAligned(byte[] data)
    {
        for (var offset = 0; offset + PacketSize <= data.Length; offset += PacketSize)
        {
            Assert.True(
                data[offset] == 0x47,
                $"expected a sync byte at offset {offset}, found 0x{data[offset]:X2}");
        }
    }

    private static int ReadPid(byte[] data, int offset)
        => ((data[offset + 1] & 0x1F) << 8) | data[offset + 2];

    private static async Task<byte[]> ReadUpTo(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;

        // A live reader answers zero when it has caught up, so a few attempts stand in for the
        // polling Jellyfin's ProgressiveFileStream does.
        for (var attempt = 0; attempt < 20 && read < count; attempt++)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read, count - read));
            if (got == 0)
            {
                await Task.Delay(10);
                continue;
            }

            read += got;
        }

        return buffer[..read];
    }

    private async Task<LiveStreamBuffer> FillBroadcast()
    {
        var buffer = new LiveStreamBuffer(_path, LiveStreamBuffer.MinimumSizeMegabytes);
        var bootstrap = new StreamBootstrapIndex();
        buffer.Bootstrap = bootstrap;

        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);

        // A broadcast that has been running a while: tables, then repeated access points with
        // ordinary video between them, exactly as the conditioner would see it.
        await Feed(buffer, conditioner, ProgramAssociationTable());
        await Feed(buffer, conditioner, ProgramMapTable());

        for (var group = 0; group < 6; group++)
        {
            await Feed(buffer, conditioner, VideoPacket(startsUnit: true, randomAccess: true));
            for (var filler = 0; filler < 5; filler++)
            {
                await Feed(buffer, conditioner, VideoPacket(startsUnit: false, randomAccess: false));
            }
        }

        return buffer;
    }

    private static async Task Feed(
        LiveStreamBuffer buffer,
        TransportStreamConditioner conditioner,
        byte[] packet)
    {
        var output = new byte[TransportStreamConditioner.GetMaximumConditionedLength(packet.Length)];
        var length = conditioner.Condition(packet, output);
        if (length == 0)
        {
            return;
        }

        await buffer.Write(
            output.AsMemory(0, length),
            conditioner.AccessPoints,
            conditioner.TakeProgramTables(),
            CancellationToken.None);
    }

    private static byte[] VideoPacket(bool startsUnit, bool randomAccess)
    {
        var packet = new byte[PacketSize];
        packet[0] = 0x47;
        packet[1] = (byte)((startsUnit ? 0x40 : 0x00) | ((VideoPid >> 8) & 0x1F));
        packet[2] = (byte)(VideoPid & 0xFF);
        packet[3] = randomAccess ? (byte)0x30 : (byte)0x10;

        if (randomAccess)
        {
            packet[4] = 1;
            packet[5] = 0x40;
        }

        return packet;
    }

    private static byte[] ProgramAssociationTable()
    {
        // 0xC1 sets current_next_indicator: the table describes the stream as it is, not as it
        // will be. Without it the parser refuses the section, as it should.
        byte[] section =
        [
            0x00, // table_id
            0xB0, 0x0D, // section_length
            0x00, 0x01, // transport_stream_id
            0xC1, 0x00, 0x00, // version, current/next, section numbers
            0x00, 0x01, // program_number
            (byte)(0xE0 | ((ProgramMapPid >> 8) & 0x1F)), (byte)(ProgramMapPid & 0xFF),
        ];

        return SectionPacket(0, PsiSection.WithCrc(section));
    }

    private static byte[] ProgramMapTable()
    {
        byte[] section =
        [
            0x02, // table_id
            0xB0, 0x12, // section_length
            0x00, 0x01, // program_number
            0xC1, 0x00, 0x00, // version, current/next, section numbers
            (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), (byte)(VideoPid & 0xFF), // PCR PID
            0xF0, 0x00, // program_info_length
            0x1B, (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), (byte)(VideoPid & 0xFF), 0xF0, 0x00,
        ];

        return SectionPacket(ProgramMapPid, PsiSection.WithCrc(section));
    }

    private static byte[] SectionPacket(int pid, byte[] section)
    {
        var packet = new byte[PacketSize];
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | ((pid >> 8) & 0x1F));
        packet[2] = (byte)(pid & 0xFF);
        packet[3] = 0x10;
        packet[4] = 0x00;
        section.CopyTo(packet, 5);
        return packet;
    }
}
