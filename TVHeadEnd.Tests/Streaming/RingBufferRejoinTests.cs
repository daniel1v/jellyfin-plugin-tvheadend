using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.Core.Media;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// Where a reader is put when it first joins, and where it is put when the writer laps it.
/// </summary>
/// <remarks>
/// The two cases are the same problem. The oldest surviving byte in a ring is wherever the write
/// head happened to wrap, which is the middle of a picture with no program tables in front of it
/// -- exactly the state a tuner hands over, and one no decoder recovers from. Both paths
/// therefore go through the bootstrap index.
/// </remarks>
public sealed class RingBufferRejoinTests : IDisposable
{
    private const int PacketLength = 188;
    private const int PatPid = 0x00;
    private const int PmtPid = 0x13ec;
    private const int VideoPid = 0x13ed;

    /// <summary>Where a written packet carries the label that says which one it is.</summary>
    private const int Mark = 10;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ringrejoin-{Guid.NewGuid():N}.ts");

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // Left behind in the temporary directory; harmless.
        }
    }

    [Fact]
    public async Task AReaderTheWriterHasLappedResumesAtAConfirmedRandomAccessPoint()
    {
        // A client that paused for longer than the buffer holds. Continuing from the oldest
        // surviving byte is what left it with a decoder that never recovered.
        var bootstrap = new StreamBootstrapIndex();

        await using var buffer = new LiveStreamBuffer(_path, LiveStreamBuffer.MinimumSizeMegabytes)
        {
            Bootstrap = bootstrap,
        };

        // Fill the window once so the reader starts at a known place.
        await WriteAccessPoint(buffer, bootstrap);
        var reader = buffer.OpenReader();

        // The bootstrap prefix, then the access point itself.
        var opening = await ReadUpTo(reader, 3 * PacketLength);
        Assert.Equal(PatPid, ReadPid(opening, 0));
        Assert.Equal(PmtPid, ReadPid(opening, PacketLength));

        // Now lap the reader: write more than the whole window, with access points along the way.
        var capacity = LiveStreamBuffer.MinimumSizeMegabytes * 1024L * 1024L;
        var written = 0L;
        while (written < capacity + (2 * 1024 * 1024))
        {
            written += await WriteAccessPoint(buffer, bootstrap);
            written += await WriteFiller(buffer, 64);
        }

        var resumed = await ReadUpTo(reader, 3 * PacketLength);

        // The tables come first, exactly as they do for a reader that has just joined.
        Assert.Equal(PatPid, ReadPid(resumed, 0));
        Assert.Equal(PmtPid, ReadPid(resumed, PacketLength));

        // And what follows is the access point, not whatever the ring happened to wrap onto.
        Assert.Equal(VideoPid, ReadPid(resumed, 2 * PacketLength));
        Assert.True(SignalsRandomAccess(resumed, 2 * PacketLength));
    }

    [Fact]
    public async Task AReaderJoiningLateStartsAtAConfirmedRandomAccessPoint()
    {
        var bootstrap = new StreamBootstrapIndex();

        await using var buffer = new LiveStreamBuffer(_path, LiveStreamBuffer.MinimumSizeMegabytes)
        {
            Bootstrap = bootstrap,
        };

        await WriteAccessPoint(buffer, bootstrap);
        await WriteFiller(buffer, 200);
        await WriteAccessPoint(buffer, bootstrap);
        await WriteFiller(buffer, 200);

        var opening = await ReadUpTo(buffer.OpenReader(), 3 * PacketLength);

        Assert.Equal(PatPid, ReadPid(opening, 0));
        Assert.Equal(PmtPid, ReadPid(opening, PacketLength));
        Assert.Equal(VideoPid, ReadPid(opening, 2 * PacketLength));
        Assert.True(SignalsRandomAccess(opening, 2 * PacketLength));
    }

    [Fact]
    public async Task AReaderWithNoRecordedAccessPointStillGetsTheProgramTables()
    {
        // No confirmed entry point survives -- a broadcaster that never sets the indicator. The
        // stream is still delivered, and the tables at least let the decoder map the streams
        // once it resynchronises on its own.
        var bootstrap = new StreamBootstrapIndex();

        await using var buffer = new LiveStreamBuffer(_path, LiveStreamBuffer.MinimumSizeMegabytes)
        {
            Bootstrap = bootstrap,
        };

        await WriteFiller(buffer, 500);

        var opening = await ReadUpTo(buffer.OpenReader(), 2 * PacketLength);

        Assert.Equal(PatPid, ReadPid(opening, 0));
        Assert.Equal(PmtPid, ReadPid(opening, PacketLength));
    }

    [Fact]
    public async Task AReaderTheWriterHasLappedRejoinsOnAnIdrRatherThanANearerRandomAccessPoint()
    {
        // The two guarantees are not interchangeable and the newer point is not the better one.
        // A DVB random access point is a legal entry for the broadcast and may be a recovery point
        // rather than an IDR; a decoder that will not start without an IDR gets no picture from
        // it. That has to hold on the rejoin path as much as on the first join -- a reader the
        // writer has lapped is being placed afresh, and placing it on the nearest point would put
        // it exactly where it must not be.
        const byte Idr = 0xA1;
        const byte Rap = 0xB2;

        var bootstrap = new StreamBootstrapIndex();

        await using var buffer = new LiveStreamBuffer(_path, LiveStreamBuffer.MinimumSizeMegabytes)
        {
            Bootstrap = bootstrap,
            RequiredGuarantee = RandomAccessGuarantee.Idr,
        };

        // Start the reader off on an IDR, so it is running before it is overtaken.
        await WriteAccessPoint(buffer, bootstrap, RandomAccessGuarantee.Idr, Idr);
        var reader = buffer.OpenReader();

        var opening = await ReadUpTo(reader, 3 * PacketLength);
        Assert.Equal(PatPid, ReadPid(opening, 0));
        Assert.Equal(Idr, opening[(2 * PacketLength) + Mark]);

        // Overtake it. Everything written while lapping is an ordinary random access point, so
        // the IDR it started on is the only one of its kind -- and it scrolls out of the window.
        var capacity = LiveStreamBuffer.MinimumSizeMegabytes * 1024L * 1024L;
        var written = 0L;
        while (written < capacity + (2 * 1024 * 1024))
        {
            written += await WriteAccessPoint(buffer, bootstrap, RandomAccessGuarantee.DvbRandomAccess, Rap);
            written += await WriteFiller(buffer, 64);
        }

        // What the window now holds: an IDR, and after it a nearer point that is only a DVB
        // random access point.
        await WriteAccessPoint(buffer, bootstrap, RandomAccessGuarantee.Idr, Idr);
        await WriteFiller(buffer, 200);
        await WriteAccessPoint(buffer, bootstrap, RandomAccessGuarantee.DvbRandomAccess, Rap);
        await WriteFiller(buffer, 200);

        var resumed = await ReadUpTo(reader, 3 * PacketLength);

        // Program tables ahead of the payload are what a rejoin produces and what reading on from
        // the old position could not: a reader that had not been placed afresh would be part way
        // through filler here.
        Assert.Equal(PatPid, ReadPid(resumed, 0));
        Assert.Equal(PmtPid, ReadPid(resumed, PacketLength));

        Assert.Equal(VideoPid, ReadPid(resumed, 2 * PacketLength));
        Assert.True(SignalsRandomAccess(resumed, 2 * PacketLength));
        Assert.Equal(Idr, resumed[(2 * PacketLength) + Mark]);
    }

    private static async Task<long> WriteAccessPoint(
        LiveStreamBuffer buffer,
        StreamBootstrapIndex bootstrap,
        RandomAccessGuarantee guarantee = RandomAccessGuarantee.DvbRandomAccess,
        byte mark = 0)
    {
        // The tables travel with the chunk, as they do from the conditioner. An access point
        // offered without them is one no reader could be told how to decode, and is not kept.
        var packet = VideoPacket(randomAccess: true, mark);
        await buffer.Write(packet, [new StreamAccessPoint(buffer.WritePosition, guarantee)], Tables(), CancellationToken.None);
        _ = bootstrap;
        return packet.Length;
    }

    private static async Task<long> WriteFiller(LiveStreamBuffer buffer, int packets)
    {
        var filler = Enumerable.Range(0, packets)
            .SelectMany(_ => VideoPacket(randomAccess: false))
            .ToArray();
        await buffer.Write(filler, null, Tables(), CancellationToken.None);
        return filler.Length;
    }

    private static byte[] VideoPacket(bool randomAccess, byte mark = 0)
    {
        var packet = new byte[PacketLength];
        packet[0] = 0x47;
        packet[1] = (byte)(((VideoPid >> 8) & 0x1F) | 0x40);
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

        // First payload byte: the adaptation field above is one byte long, so this is past it.
        // Only a test reads it, and only to say which access point a reader was put on.
        packet[Mark] = mark;

        return packet;
    }

    private static ProgramTableSnapshot Tables()
        => new([TablePacket(PatPid)], [TablePacket(PmtPid)], 0);

    private static byte[] TablePacket(int pid)

    {
        var packet = new byte[PacketLength];
        packet[0] = 0x47;
        packet[1] = (byte)(((pid >> 8) & 0x1F) | 0x40);
        packet[2] = (byte)(pid & 0xFF);
        packet[3] = 0x10;
        return packet;
    }

    private static int ReadPid(byte[] data, int offset)
        => ((data[offset + 1] & 0x1F) << 8) | data[offset + 2];

    private static bool SignalsRandomAccess(byte[] data, int offset)
    {
        var adaptationFieldControl = (data[offset + 3] >> 4) & 0x3;
        if (adaptationFieldControl is not (2 or 3))
        {
            return false;
        }

        return data[offset + 4] > 0 && (data[offset + 5] & 0x40) != 0;
    }

    private static async Task<byte[]> ReadUpTo(Stream stream, int count)
    {
        var buffer = new byte[count];
        var total = 0;
        var attempts = 0;

        while (total < count && attempts < 500)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total));
            if (read == 0)
            {
                attempts++;
                await Task.Delay(5);
                continue;
            }

            total += read;
        }

        return buffer.AsSpan(0, total).ToArray();
    }
}
