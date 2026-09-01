using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.Core.Media;
using TVHeadEnd.Infrastructure.LiveBuffer;
using Xunit;

namespace TVHeadEnd.Tests.Infrastructure;

/// <summary>
/// A reader and the writer that laps it, running at the same time.
/// </summary>
/// <remarks>
/// <para>
/// The regression this guards is not a wrap or a lapping in isolation -- those were already
/// covered, sequentially -- but the window between the two. The writer used to overwrite the
/// physical bytes of the oldest region and only afterwards publish the new write position, and the
/// readable window was derived from that published position. For the whole duration of a write, a
/// reader sitting on the region being overwritten was therefore told its position was still valid
/// while the bytes underneath it had already become the new ones.
/// </para>
/// <para>
/// Each packet carries its own logical position, so a mixture is detectable: a packet whose body
/// does not match its own header is one that was assembled from two different writes.
/// </para>
/// </remarks>
public sealed class LiveRingBufferConcurrencyTests : IDisposable
{
    private const int PacketSize = 188;
    private const int Capacity = 256 * PacketSize;

    private readonly string _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // The buffer file is best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AReaderIsNeverHandedNewBytesUnderAnOldPosition()
    {
        await using var ring = new LiveRingBuffer(_path, Capacity);

        // Fill the ring once so every further write overwrites something a reader could be on.
        for (var packet = 0; packet < Capacity / PacketSize; packet++)
        {
            await ring.WriteAsync(Packet(packet), CancellationToken.None);
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var reader = ring.OpenReaderFromStart(null);

        var writer = Task.Run(
            async () =>
            {
                // Many laps of the whole window, in chunks of several packets. The chunk size
                // matters: a write big enough that a reader can get inside it is what makes a torn
                // packet observable at all, and a torn packet is the shape the defect took.
                const int PacketsPerWrite = 24;
                for (var packet = Capacity / PacketSize; packet < 20000; packet += PacketsPerWrite)
                {
                    var chunk = new byte[PacketsPerWrite * PacketSize];
                    for (var index = 0; index < PacketsPerWrite; index++)
                    {
                        Packet(packet + index).CopyTo(chunk.AsMemory(index * PacketSize));
                    }

                    await ring.WriteAsync(chunk, cancellation.Token);
                }
            },
            cancellation.Token);

        var buffer = new byte[7 * PacketSize];
        var carried = 0;
        var checkedPackets = 0;

        while (!writer.IsCompleted || checkedPackets == 0)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(carried), cancellation.Token);
            if (read == 0)
            {
                await Task.Yield();
                continue;
            }

            var available = carried + read;
            var offset = 0;
            for (; offset + PacketSize <= available; offset += PacketSize)
            {
                AssertWholePacket(buffer.AsSpan(offset, PacketSize));
                checkedPackets++;
            }

            // Whatever did not make a whole packet is carried into the next read.
            carried = available - offset;
            buffer.AsSpan(offset, carried).CopyTo(buffer);
        }

        await writer;
        Assert.True(checkedPackets > 0, "The reader never received a whole packet.");
    }

    [Fact]
    public async Task TheRegionBeingOverwrittenLeavesTheWindowBeforeAByteOfItMoves()
    {
        // The defect itself, stated as arithmetic. The writer used to put the new bytes down and
        // only then publish the new end, while the readable window was derived from that end -- so
        // for the whole duration of a write, the region underneath it was still being offered
        // under its old logical positions.
        await using var ring = new LiveRingBuffer(_path, Capacity);

        var fill = new byte[Capacity];
        await ring.WriteAsync(fill, CancellationToken.None);
        Assert.Equal(0, ring.OldestPosition);

        // Left unawaited on purpose: this is the window the reader used to fall into.
        var chunk = new byte[Capacity / 2];
        var write = ring.WriteAsync(chunk, CancellationToken.None);
        var oldestDuringWrite = ring.OldestPosition;
        var publishedDuringWrite = ring.WritePosition;

        await write;

        if (publishedDuringWrite == Capacity)
        {
            // The write had not been published yet, so the window was observed mid-flight: the
            // region about to be overwritten must already have left it.
            Assert.Equal(chunk.Length, oldestDuringWrite);
        }

        Assert.Equal(Capacity + chunk.Length, ring.WritePosition);
        Assert.Equal(chunk.Length, ring.OldestPosition);
    }

    [Fact]
    public async Task AWriteLargerThanTheWindowLeavesOnlyItsOwnTailReadable()

    {
        // The head of such a write is never put down at all, so no reader may be offered it.
        await using var ring = new LiveRingBuffer(_path, Capacity);

        var oversized = new byte[Capacity + (10 * PacketSize)];
        for (var packet = 0; packet * PacketSize < oversized.Length; packet++)
        {
            Packet(packet).CopyTo(oversized.AsMemory(packet * PacketSize));
        }

        await ring.WriteAsync(oversized, CancellationToken.None);

        Assert.Equal(oversized.Length, ring.WritePosition);
        Assert.Equal(oversized.Length - Capacity, ring.OldestPosition);

        using var reader = ring.OpenReaderFromStart(null);
        var buffer = new byte[PacketSize];
        var read = await ReadExactly(reader, buffer);

        Assert.Equal(PacketSize, read);
        AssertWholePacket(buffer);

        // And what it starts with is the first packet that survived, not one of the discarded.
        Assert.Equal(oversized.Length - Capacity, BinaryPrimitives.ReadInt64BigEndian(buffer));
    }

    private static async Task<int> ReadExactly(Stream reader, byte[] buffer)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(filled));
            if (read == 0)
            {
                await Task.Yield();
                continue;
            }

            filled += read;
        }

        return filled;
    }

    /// <summary>
    /// Checks that a packet's body is the one its own header claims, which a packet assembled
    /// from two different writes cannot be.
    /// </summary>
    private static void AssertWholePacket(ReadOnlySpan<byte> packet)
    {
        var position = BinaryPrimitives.ReadInt64BigEndian(packet);
        Assert.True(position >= 0 && position % PacketSize == 0, $"Packet header is not a position: {position}.");

        for (var index = sizeof(long); index < PacketSize; index++)
        {
            Assert.Equal(Fill(position, index), packet[index]);
        }
    }

    /// <summary>
    /// One packet, carrying the logical position it was written at and a body derived from it.
    /// </summary>
    private static byte[] Packet(int index)
    {
        var packet = new byte[PacketSize];
        var position = (long)index * PacketSize;
        BinaryPrimitives.WriteInt64BigEndian(packet, position);

        for (var offset = sizeof(long); offset < PacketSize; offset++)
        {
            packet[offset] = Fill(position, offset);
        }

        return packet;
    }

    private static byte Fill(long position, int offset) => (byte)((position / PacketSize) + offset);
}
