using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.Core.Media;
using TVHeadEnd.Infrastructure.LiveBuffer;
using TVHeadEnd.Tests.Core;
using Xunit;

namespace TVHeadEnd.Tests.Infrastructure;

/// <summary>
/// What a reader is given while the broadcaster is changing what the stream contains.
/// </summary>
/// <remarks>
/// Driven through the conditioner and the buffer rather than through the index alone, because the
/// two defects this guards both live in the joins between them: a layout change that happens part
/// way through one conditioned chunk, and a reader with nowhere safe to start that has to wait for
/// somewhere rather than read on regardless.
/// </remarks>
public sealed class LayoutChangeJoinTests : IDisposable
{
    private const int PacketLength = 188;
    private const int PmtPid = 0x13ec;
    private const int VideoPid = 0x13ed;
    private const int AudioPid = 0x13ee;

    private readonly string _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        try
        {
            File.Delete(_path + ".ts");
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AReaderWithNowhereSafeToStartWaitsForSomewhere()
    {
        // The whole point of NotYet. Parking such a reader at the write head and letting it read
        // on as soon as anything arrives is not waiting -- it waits for the next write, which is
        // not the same thing as waiting for a place a decoder can start.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
        var bootstrap = new StreamBootstrapIndex();
        await using var buffer = new LiveStreamBuffer(_path, LiveStreamBuffer.MinimumSizeMegabytes)
        {
            Bootstrap = bootstrap,
        };

        // A first layout, delivered from its access point.
        await Feed(buffer, conditioner, Concat(Pat(), Pmt(0x1B), VideoPacket(randomAccess: true)));

        // Then the layout changes, and nothing of the new one has an access point yet.
        await Feed(buffer, conditioner, Pmt(0x1B, withAudioLanguage: true));

        using var reader = buffer.OpenReader();

        // More bytes arrive, but none of them is a place to start.
        await Feed(buffer, conditioner, Concat(VideoPacket(randomAccess: false), VideoPacket(randomAccess: false)));

        var buffered = new byte[4 * PacketLength];
        Assert.Equal(0, await reader.ReadAsync(buffered));

        // The first access point of the new layout is what it was waiting for.
        await Feed(buffer, conditioner, VideoPacket(randomAccess: true));

        var read = await ReadUpTo(reader, 3 * PacketLength);

        // It begins with the tables, and then the access point itself.
        Assert.Equal(0x00, ReadPid(read, 0));
        Assert.Equal(PmtPid, ReadPid(read, PacketLength));
        Assert.Equal(VideoPid, ReadPid(read, 2 * PacketLength));
    }

    [Fact]
    public async Task AnAccessPointBeforeALayoutChangeInTheSameChunkIsNotOfferedWithTheNewTables()
    {
        // The case a test built from two separate Publish calls cannot reach: the access point and
        // the change that invalidates it are in one conditioned chunk, so the chunk's own start
        // position says nothing about where the new layout begins.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
        var bootstrap = new StreamBootstrapIndex();
        await using var buffer = new LiveStreamBuffer(_path, LiveStreamBuffer.MinimumSizeMegabytes)
        {
            Bootstrap = bootstrap,
        };

        await Feed(buffer, conditioner, Concat(Pat(), Pmt(0x1B), VideoPacket(randomAccess: true)));

        // One chunk: an access point of the old layout, then the table that replaces it.
        var chunkStart = buffer.WritePosition;
        await Feed(
            buffer,
            conditioner,
            Concat(VideoPacket(randomAccess: true), Pmt(0x1B, withAudioLanguage: true)));

        // Asked from the very start of that chunk. The layout changed part way into it, so the
        // bytes at its start are still the programme before and there is nowhere safe to begin.
        // Taking the chunk boundary as the moment the layout changed -- the only thing the buffer
        // knows by itself -- would answer FromOldest here and hand those bytes over behind the
        // new tables.
        Assert.Equal(StreamJoinKind.NotYet, bootstrap.CreateJoin(chunkStart).Kind);

        // And from before the chunk, for the same reason.
        Assert.Equal(StreamJoinKind.NotYet, bootstrap.CreateJoin(0).Kind);
    }

    private static async Task Feed(LiveStreamBuffer buffer, TransportStreamConditioner conditioner, byte[] source)
    {
        var destination = new byte[TransportStreamConditioner.GetMaximumConditionedLength(source.Length)];
        var written = conditioner.Condition(source, destination);
        if (written == 0)
        {
            return;
        }

        await buffer.Write(
            destination.AsMemory(0, written),
            conditioner.AccessPoints,
            conditioner.TakeProgramTables(),
            CancellationToken.None);
    }

    private static async Task<byte[]> ReadUpTo(Stream reader, int count)
    {
        var buffer = new byte[count];
        var filled = 0;
        for (var attempt = 0; filled < count && attempt < 200; attempt++)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(filled));
            if (read == 0)
            {
                await Task.Delay(5);
                continue;
            }

            filled += read;
        }

        return buffer;
    }

    private static int ReadPid(byte[] data, int offset)
        => ((data[offset + 1] & 0x1F) << 8) | data[offset + 2];

    private static byte[] VideoPacket(bool randomAccess)
    {
        var packet = new byte[PacketLength];
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | ((VideoPid >> 8) & 0x1F));
        packet[2] = VideoPid & 0xFF;

        if (randomAccess)
        {
            packet[3] = 0x30;
            packet[4] = 1;
            packet[5] = 0x40;
        }
        else
        {
            packet[3] = 0x10;
        }

        return packet;
    }

    private static byte[] Pat()
    {
        byte[] section =
        [
            0x00,
            0xB0, 0x0D,
            0x00, 0x01,
            0xC1, 0x00, 0x00,
            0x00, 0x01,
            (byte)(0xE0 | ((PmtPid >> 8) & 0x1F)), PmtPid & 0xFF,
        ];

        return SectionPacket(0x00, PsiSectionBytes.WithCrc(section));
    }

    private static byte[] Pmt(byte videoStreamType, bool withAudioLanguage = false)
    {
        List<byte> section =
        [
            0x02,
            0xB0, withAudioLanguage ? (byte)0x1D : (byte)0x17,
            0x00, 0x01,
            0xC1, 0x00, 0x00,
            (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF,
            0xF0, 0x00,
            videoStreamType, (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF, 0xF0, 0x00,
            0x03, (byte)(0xE0 | ((AudioPid >> 8) & 0x1F)), AudioPid & 0xFF, 0xF0,
            withAudioLanguage ? (byte)0x06 : (byte)0x00,
        ];

        if (withAudioLanguage)
        {
            section.AddRange([0x0A, 0x04, (byte)'d', (byte)'e', (byte)'u', 0x00]);
        }

        return SectionPacket(PmtPid, PsiSectionBytes.WithCrc(section));
    }

    private static byte[] SectionPacket(int pid, IReadOnlyList<byte> section)
    {
        var packet = new byte[PacketLength];
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | ((pid >> 8) & 0x1F));
        packet[2] = (byte)(pid & 0xFF);
        packet[3] = 0x10;
        packet[4] = 0x00;

        for (var index = 0; index < section.Count; index++)
        {
            packet[5 + index] = section[index];
        }

        return packet;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}
