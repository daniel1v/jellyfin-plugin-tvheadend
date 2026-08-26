using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Tvheadend.Htsp.Protocol;

namespace TVHeadEnd.Tests.Htsp;

/// <summary>
/// A TVHeadend that answers HTSP on a loopback socket.
/// </summary>
/// <remarks>
/// Enough of the real server to exercise the parts of the client that are hard to be sure of by
/// reading: that a reply reaches the request it belongs to even when replies come back out of
/// order, and that a subscription's asynchronous messages reach the right subscription. Both are
/// properties of the wire, so they are tested over one.
/// </remarks>
public sealed class FakeHtspServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentQueue<HtspMessage> _received = new();
    private readonly SemaphoreSlim _requestArrived = new(0);

    private Task? _acceptLoop;
    private NetworkStream? _client;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeHtspServer"/> class.
    /// </summary>
    public FakeHtspServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Gets the port the server is listening on.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Gets or sets what to do with each request. Return a reply to send one, or
    /// <see langword="null"/> to answer nothing.
    /// </summary>
    public Func<HtspMessage, FakeHtspServer, HtspMessage?> OnRequest { get; set; } = DefaultHandler;

    /// <summary>
    /// Starts accepting the connection.
    /// </summary>
    public void Start() => _acceptLoop = Task.Run(() => AcceptAsync(_lifetime.Token));

    /// <summary>
    /// Waits until a request with the given method has arrived, and returns it.
    /// </summary>
    /// <param name="method">The method to wait for.</param>
    /// <returns>The request.</returns>
    public async Task<HtspMessage> WaitForRequestAsync(string method)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            foreach (var candidate in _received)
            {
                if (string.Equals(candidate.Method, method, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            await _requestArrived.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets every request received so far.
    /// </summary>
    /// <returns>The requests, in arrival order.</returns>
    public IReadOnlyList<HtspMessage> ReceivedRequests() => [.. _received];

    /// <summary>
    /// Sends a message the client did not ask for.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>A task that completes once it is on the wire.</returns>
    public async Task SendAsync(HtspMessage message)
    {
        var stream = _client ?? throw new InvalidOperationException("Nothing has connected yet.");
        var frame = HtspCodec.Encode(message);
        await stream.WriteAsync(frame).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Idempotent: a test that closes the server to see what the client does about it still
        // has it in an await-using block.
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Teardown.
            }
        }

        _lifetime.Dispose();
        _requestArrived.Dispose();
    }

    private static HtspMessage? DefaultHandler(HtspMessage request, FakeHtspServer server)
        => request.Method switch
        {
            "hello" => new HtspMessage()
                .Set("htspversion", 44)
                .Set("servername", "FakeTvheadend")
                .Set("serverversion", "4.3")
                .Set("challenge", new byte[32]),
            _ => new HtspMessage(),
        };

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        using var socket = await _listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        var stream = new NetworkStream(socket, ownsSocket: false);
        _client = stream;

        var header = new byte[HtspCodec.FrameHeaderLength];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                var length = HtspCodec.ReadBodyLength(header);
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);

                var request = HtspCodec.Decode(body);
                _received.Enqueue(request);
                _requestArrived.Release();

                var reply = OnRequest(request, this);
                if (reply is null)
                {
                    continue;
                }

                if (request.GetInt64("seq") is { } sequence)
                {
                    reply.Set("seq", sequence);
                }

                await SendAsync(reply).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is EndOfStreamException or IOException or OperationCanceledException)
            {
                return;
            }
        }
    }
}
