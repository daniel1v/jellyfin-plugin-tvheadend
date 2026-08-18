using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// A compatibility rendering is made for one session. It begins at the first byte of a freshly
/// started transcoder, it is never shared, and every consumer sees the container header.
/// </summary>
public class CompatibilitySessionTests
{
    private static readonly byte[] MatroskaHeader =
        [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0xF7, 0x81, 0x01];

    [Fact]
    public async Task ASessionIsNeverShared()
    {
        await using var session = Create(Body(300_000));
        await session.Open(CancellationToken.None);

        Assert.False(session.EnableStreamSharing);
        Assert.False(LiveTvService.CanBeReusedFor(session, "42", PlaybackVariant.H264IdrNormalization));
        Assert.False(LiveTvService.CanBeReusedFor(session, "42", PlaybackVariant.Mpeg2H264Compatibility));
        Assert.False(LiveTvService.CanBeReusedFor(session, "42", PlaybackVariant.Native));
    }

    [Fact]
    public async Task EveryConsumerSeesTheContainerHeader()
    {
        // The header is written once. A consumer that starts anywhere else has a stream no
        // decoder can open, which is exactly what the ring buffer forced on a Matroska source
        // and why this path does not use one.
        await using var session = Create(Body(300_000));
        await session.Open(CancellationToken.None);

        using var first = session.GetStream();
        using var second = session.GetStream();

        Assert.Equal(MatroskaHeader, await ReadExactly(first, MatroskaHeader.Length));
        Assert.Equal(MatroskaHeader, await ReadExactly(second, MatroskaHeader.Length));
    }

    [Fact]
    public async Task StartupInspectionDoesNotConsumeWhatThePlayerNeeds()
    {
        // The session is inspected as a file while it is being received. Reading it must leave
        // the stream handed to the player untouched, header included.
        await using var session = Create(Body(300_000));
        await session.Open(CancellationToken.None);

        // Opened the way anything inspecting a file still being written has to open it, which is
        // how the ring buffer is already read while the native stream fills it.
        await using var inspection = new FileStream(
            session.MediaPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(MatroskaHeader, await ReadExactly(inspection, MatroskaHeader.Length));

        using var player = session.GetStream();
        Assert.Equal(MatroskaHeader, await ReadExactly(player, MatroskaHeader.Length));
    }

    [Fact]
    public async Task TheSessionIsPublishedAsWhatItActuallyDelivers()
    {
        await using var session = Create(Body(300_000));

        Assert.Equal(CompatibilityContainer.Matroska, session.Container);
        Assert.EndsWith(".mkv", session.MediaPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingEndsTheSessionAndRemovesItsSpool()
    {
        var session = Create(Body(300_000));
        await session.Open(CancellationToken.None);
        var spoolPath = session.MediaPath;
        Assert.True(File.Exists(spoolPath));

        await session.Close();

        Assert.False(File.Exists(spoolPath));
    }

    [Fact]
    public async Task ASourceThatNeverDeliversFailsTheOpen()
    {
        await using var session = Create(new StalledStream(), TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<TimeoutException>(() => session.Open(CancellationToken.None));
    }

    [Fact]
    public async Task ASourceThatEndsTooEarlyFailsTheOpen()
    {
        await using var session = Create(Body(1024));

        await Assert.ThrowsAsync<EndOfStreamException>(() => session.Open(CancellationToken.None));
    }

    private static async Task<byte[]> ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read, count - read));
            if (got == 0)
            {
                break;
            }

            read += got;
        }

        return buffer;
    }

    private static MemoryStream Body(int length)
    {
        var body = new byte[length];
        MatroskaHeader.CopyTo(body, 0);
        for (var i = MatroskaHeader.Length; i < length; i++)
        {
            body[i] = (byte)(i % 251);
        }

        return new MemoryStream(body);
    }

    private static CompatibilityLiveStream Create(Stream body, TimeSpan? startupTimeLimit = null)
        => new(
            "42",
            PlaybackVariant.H264IdrNormalization.ToString(),
            CompatibilityContainer.Matroska,
            "http://tvheadend.invalid/stream",
            new Dictionary<string, string>(),
            new MediaSourceInfo(),
            Path.Combine(Path.GetTempPath(), "tvheadend-test-" + Guid.NewGuid().ToString("N")),
            new StubHttpClientFactory(body),
            NullLogger.Instance,
            startupTimeLimit: startupTimeLimit ?? TimeSpan.FromSeconds(5));

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly Stream _body;

        public StubHttpClientFactory(Stream body) => _body = body;

        public HttpClient CreateClient(string name) => new(new StubHandler(_body));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Stream _body;

        public StubHandler(Stream body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(_body),
            });
    }
}
