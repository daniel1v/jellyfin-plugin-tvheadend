using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Playback;

/// <summary>
/// Records who fetches a live transport stream.
/// </summary>
/// <remarks>
/// A diagnostic, and meant to be removed once it has answered one question. Both readers of a live
/// buffer arrive the same way -- the local FFmpeg that feeds an HLS remux, and a client taking the
/// stream directly -- so a trace inside the stream itself cannot tell them apart. The request can:
/// the client names itself in its user agent.
/// </remarks>
public sealed class StreamFetchTrace
{
    private readonly RequestDelegate _next;
    private readonly ILogger<StreamFetchTrace> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamFetchTrace"/> class.
    /// </summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="logger">Where the fetches are written.</param>
    public StreamFetchTrace(RequestDelegate next, ILogger<StreamFetchTrace> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Logs a fetch of a live stream and lets it through untouched.
    /// </summary>
    /// <param name="context">The request in flight.</param>
    /// <returns>A task that completes when the pipeline has.</returns>
    public async Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path.Value ?? string.Empty;
        var interesting = path.Contains("LiveStreamFiles", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("stream.ts", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/videos/", StringComparison.OrdinalIgnoreCase);

        if (!interesting)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Stream fetch: {Method} {Path}{Query} by {Agent} range={Range}",
            context.Request.Method,
            path,
            context.Request.QueryString.Value,
            context.Request.Headers.UserAgent.ToString(),
            context.Request.Headers.Range.ToString());

        // How much a fetch carried and how long it took is the whole question for the static path:
        // a growing file served as a file ends at whatever had been written when it started.
        var original = context.Response.Body;
        var counted = new CountingBody(original);
        context.Response.Body = counted;
        var started = Stopwatch.StartNew();

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;

            // Who hung up. A live stream this plugin serves never ends on its own, so a finished
            // request means either the client closed the connection or the pipeline was torn down.
            _logger.LogInformation(
                "Stream fetch ended: {Path} status={Status} bytes={Bytes} after={Elapsed}ms aborted={Aborted}",
                path,
                context.Response.StatusCode,
                counted.Written,
                started.ElapsedMilliseconds,
                context.RequestAborted.IsCancellationRequested);
        }
    }

    /// <summary>
    /// Counts what actually reached the client.
    /// </summary>
    private sealed class CountingBody : Stream
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Usage",
            "CA2213:Disposable fields should be disposed",
            Justification = "The response body belongs to the pipeline, which disposes it. Wrapping it must not.")]
        private readonly Stream _inner;

        public CountingBody(Stream inner) => _inner = inner;

        public long Written { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken)
            => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Written += count;
            _inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Written += buffer.Length;
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
