using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.Recordings;
using Xunit;

namespace TVHeadEnd.Tests;

public class RecordingSampleTests
{
    [Fact]
    public async Task AServerThatHonoursTheRangeIsCopiedWhole()
    {
        var source = new MemoryStream(Pattern(4096));
        var destination = new MemoryStream();

        var copied = await TvheadendRecordingSampleSource.CopyAtMost(source, destination, 8192, CancellationToken.None);

        Assert.Equal(4096, copied);
        Assert.Equal(4096, destination.Length);
    }

    [Fact]
    public async Task AServerThatIgnoresTheRangeIsCutOffAtTheLimit()
    {
        // TVHeadend without range support, or a proxy in between, answers 200 with the whole
        // recording. Copying to the end would pull gigabytes across for an analysis that needs
        // megabytes, so the limit is enforced while reading rather than assumed from the answer.
        var source = new MemoryStream(Pattern(1024 * 1024));
        var destination = new MemoryStream();

        var copied = await TvheadendRecordingSampleSource.CopyAtMost(source, destination, 4096, CancellationToken.None);

        Assert.Equal(4096, copied);
        Assert.Equal(4096, destination.Length);
        Assert.Equal(Pattern(4096), destination.ToArray());
    }

    [Fact]
    public async Task AnAnswerShorterThanTheLimitEndsTheCopy()
    {
        var source = new MemoryStream(Pattern(100));
        var destination = new MemoryStream();

        var copied = await TvheadendRecordingSampleSource.CopyAtMost(source, destination, 4096, CancellationToken.None);

        Assert.Equal(100, copied);
    }

    [Fact]
    public async Task AnEmptyAnswerCopiesNothingRatherThanHanging()
    {
        var copied = await TvheadendRecordingSampleSource.CopyAtMost(
            new MemoryStream([]),
            new MemoryStream(),
            4096,
            CancellationToken.None);

        Assert.Equal(0, copied);
    }

    [Fact]
    public async Task CancellationStopsTheCopy()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TvheadendRecordingSampleSource.CopyAtMost(
            new MemoryStream(Pattern(4096)),
            new MemoryStream(),
            4096,
            cancellation.Token));
    }

    [Fact]
    public async Task ALimitOfNothingIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TvheadendRecordingSampleSource.CopyAtMost(
            new MemoryStream(Pattern(16)),
            new MemoryStream(),
            0,
            CancellationToken.None));
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }
}
