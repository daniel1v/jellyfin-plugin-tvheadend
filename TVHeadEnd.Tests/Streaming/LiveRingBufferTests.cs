using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

public sealed class LiveRingBufferTests : IDisposable
{
    private const int PacketSize = 188;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ringtest-{Guid.NewGuid():N}.ts");

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
    public async Task AReaderSeesExactlyWhatWasWritten()
    {
        await using var ring = new LiveRingBuffer(_path, 64 * PacketSize);
        var written = Pattern(0, 10 * PacketSize);

        await ring.WriteAsync(written, CancellationToken.None);

        using var reader = ring.OpenReaderFromStart();
        Assert.Equal(written, await ReadFully(reader, written.Length));
    }

    [Fact]
    public async Task ReadingAtTheLiveEdgeReturnsZeroRatherThanEndingTheStream()
    {
        // ProgressiveFileStream treats zero as "nothing yet, ask again in 50 ms". Ending the
        // stream instead would cut playback off the moment a client caught up with the tuner.
        await using var ring = new LiveRingBuffer(_path, 64 * PacketSize);
        await ring.WriteAsync(Pattern(0, PacketSize), CancellationToken.None);

        using var reader = ring.OpenReaderFromStart();
        await ReadFully(reader, PacketSize);

        var buffer = new byte[PacketSize];
        Assert.Equal(0, await reader.ReadAsync(buffer, CancellationToken.None));

        // ... and picks up again once the writer moves on.
        await ring.WriteAsync(Pattern(PacketSize, PacketSize), CancellationToken.None);
        Assert.Equal(PacketSize, await reader.ReadAsync(buffer, CancellationToken.None));
        Assert.Equal(Pattern(PacketSize, PacketSize), buffer);
    }

    [Fact]
    public async Task TheStreamStaysContinuousAcrossTheWrap()
    {
        // The whole point of the ring: writing more than the capacity must not corrupt what a
        // reader keeping up with the writer sees.
        const int capacity = 16 * PacketSize;
        await using var ring = new LiveRingBuffer(_path, capacity);

        using var reader = ring.OpenReaderFromStart();
        var received = new MemoryStream();
        var chunk = new byte[4 * PacketSize];

        for (var round = 0; round < 10; round++)
        {
            await ring.WriteAsync(Pattern(round * chunk.Length, chunk.Length), CancellationToken.None);

            int read;
            while ((read = await reader.ReadAsync(chunk, CancellationToken.None)) > 0)
            {
                await received.WriteAsync(chunk.AsMemory(0, read), CancellationToken.None);
            }
        }

        Assert.Equal(Pattern(0, 10 * 4 * PacketSize), received.ToArray());
        Assert.Equal(capacity, new FileInfo(_path).Length);
    }

    [Fact]
    public async Task TheFileNeverGrowsBeyondTheCapacity()
    {
        const int capacity = 8 * PacketSize;
        await using var ring = new LiveRingBuffer(_path, capacity);

        for (var round = 0; round < 50; round++)
        {
            await ring.WriteAsync(Pattern(round * PacketSize, PacketSize), CancellationToken.None);
        }

        Assert.Equal(capacity, new FileInfo(_path).Length);
        Assert.Equal(50 * PacketSize, ring.WritePosition);
    }

    [Fact]
    public async Task AReaderThatFellOutOfTheWindowResumesAtTheOldestDataOnAPacketBoundary()
    {
        // A client paused for longer than the buffer holds. Serving it the bytes that have since
        // overwritten its position would hand the decoder a splice in mid-packet; it is moved to
        // the oldest data still present instead.
        const int capacity = 8 * PacketSize;
        await using var ring = new LiveRingBuffer(_path, capacity);
        await ring.WriteAsync(Pattern(0, PacketSize), CancellationToken.None);

        using var reader = ring.OpenReaderFromStart();

        // The writer laps the reader several times over.
        for (var round = 1; round < 30; round++)
        {
            await ring.WriteAsync(Pattern(round * PacketSize, PacketSize), CancellationToken.None);
        }

        var buffer = new byte[PacketSize];
        var read = await reader.ReadAsync(buffer, CancellationToken.None);

        Assert.Equal(PacketSize, read);
        Assert.Equal(0, ring.OldestPosition % PacketSize);

        // What arrives is real stream content, not a fragment straddling two packets.
        var expectedStart = ring.OldestPosition;
        Assert.Equal(Pattern((int)expectedStart, PacketSize), buffer);
    }

    [Fact]
    public async Task ResetDiscardsTheDetectionPhaseSoTheEncoderOutputStandsAlone()
    {
        await using var ring = new LiveRingBuffer(_path, 64 * PacketSize);
        await ring.WriteAsync(Pattern(0, 4 * PacketSize), CancellationToken.None);

        ring.Reset();
        Assert.Equal(0, ring.WritePosition);

        var encoded = Pattern(1000, 2 * PacketSize);
        await ring.WriteAsync(encoded, CancellationToken.None);

        using var reader = ring.OpenReaderFromStart();
        Assert.Equal(encoded, await ReadFully(reader, encoded.Length));
    }

    private static byte[] Pattern(int start, int length)
        => Enumerable.Range(start, length).Select(value => (byte)(value % 251)).ToArray();

    private static async Task<byte[]> ReadFully(Stream stream, int length)
    {
        var result = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(result.AsMemory(offset, length - offset), CancellationToken.None);
            if (read == 0)
            {
                throw new InvalidOperationException($"only {offset} of {length} bytes arrived");
            }

            offset += read;
        }

        return result;
    }
}
