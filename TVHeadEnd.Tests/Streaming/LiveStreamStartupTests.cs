using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Media;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// Connecting to TVHeadend proves only that the subscription was accepted. What arrives after
/// that has to be bounded, because a caller waiting on a stream that never becomes playable
/// reaches the viewer as a spinner that never resolves.
/// </summary>
public class LiveStreamStartupTests
{
    [Fact]
    public async Task AConnectedStreamThatNeverBecomesPlayableFailsTheOpen()
    {
        await using var stream = Create(new StalledContentStream(), TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<TimeoutException>(() => stream.Open(CancellationToken.None));
    }

    [Fact]
    public async Task AStreamThatEndsImmediatelyFailsTheOpen()
    {
        await using var stream = Create(new MemoryStream(), TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<EndOfStreamException>(() => stream.Open(CancellationToken.None));
    }

    [Fact]
    public async Task CancellingTheOpenIsHonouredWithoutWaitingOutTheLimit()
    {
        await using var stream = Create(new StalledContentStream(), TimeSpan.FromMinutes(5));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.Open(cancellation.Token));
    }

    private static TvheadendLiveStream Create(Stream body, TimeSpan startupTimeLimit)
        => new(
            "42",
            "Native",
            "http://tvheadend.invalid/stream",
            new Dictionary<string, string>(),
            new MediaSourceInfo(),
            Path.Combine(Path.GetTempPath(), "tvheadend-test-" + Guid.NewGuid().ToString("N")),
            1,
            describedAlready: true,
            new StubHttpClientFactory(body),
            NullLogger.Instance,
            startupTimeLimit: startupTimeLimit);

    /// <summary>
    /// A body that stays open and never delivers anything, which is what a broken transcoding
    /// profile looks like from here.
    /// </summary>
    private sealed class StalledContentStream : Stream
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
