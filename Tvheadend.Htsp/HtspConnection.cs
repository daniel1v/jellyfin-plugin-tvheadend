using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tvheadend.Htsp.Model;
using Tvheadend.Htsp.Protocol;

namespace Tvheadend.Htsp;

/// <summary>
/// One HTSP connection to one TVHeadend server.
/// </summary>
/// <remarks>
/// <para>
/// There is exactly one read path and one write path. The read loop owns the socket's receive
/// side for the connection's whole life and is the only thing that decodes; writes are
/// serialised by a semaphore because a message must reach the wire whole. Nothing polls, nothing
/// sleeps, and no thread is created: a request parks on a
/// <see cref="TaskCompletionSource{TResult}"/> that the read loop completes.
/// </para>
/// <para>
/// Messages divide in three. A reply carries the <c>seq</c> of its request and completes it. A
/// message carrying a <c>subscriptionId</c> belongs to a subscription and is routed to it.
/// Everything else -- the channel, DVR and EPG feed -- is raised on
/// <see cref="MessageReceived"/>.
/// </para>
/// </remarks>
public sealed class HtspConnection : IAsyncDisposable
{
    private readonly HtspConnectionOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<HtspMessage>> _pending = new();
    private readonly ConcurrentDictionary<int, HtspSubscription> _subscriptions = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Socket? _socket;
    private Stream? _stream;
    private Task? _readLoop;
    private long _sequence;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="HtspConnection"/> class.
    /// </summary>
    /// <param name="options">Where the server is and how to identify to it.</param>
    /// <param name="logger">The logger, or <see langword="null"/> to log nothing.</param>
    public HtspConnection(HtspConnectionOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Raised for every asynchronous message that belongs neither to a request nor to a
    /// subscription.
    /// </summary>
    /// <remarks>
    /// Raised on the read loop, so a handler that blocks stops the connection. Handlers are
    /// expected to record and return.
    /// </remarks>
    public event EventHandler<HtspMessage>? MessageReceived;

    /// <summary>
    /// Gets what the server said about itself during the handshake.
    /// </summary>
    public HtspHello? Hello { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the connection is usable.
    /// </summary>
    public bool IsConnected => _stream is not null && !_lifetime.IsCancellationRequested && Volatile.Read(ref _disposed) == 0;

    /// <summary>
    /// Gets a task that completes when the connection is finished, however it ended.
    /// </summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// Gets the HTSP version in effect, which is the lower of what each side supports.
    /// </summary>
    public int ProtocolVersion => Math.Min(
        Hello?.ProtocolVersion ?? HtspConnectionOptions.SupportedProtocolVersion,
        HtspConnectionOptions.SupportedProtocolVersion);

    /// <summary>
    /// Connects, shakes hands and authenticates.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the connection is ready to carry requests.</returns>
    /// <exception cref="HtspAuthenticationException">TVHeadend refused the credentials.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_stream is not null)
        {
            throw new InvalidOperationException("This HTSP connection has already been opened.");
        }

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        connectTimeout.CancelAfter(_options.ConnectTimeout);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(_options.Host, _options.Port, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new HtspException(string.Create(
                CultureInfo.InvariantCulture,
                $"TVHeadend at {_options.Host}:{_options.Port} did not accept a connection within {_options.ConnectTimeout.TotalSeconds:0} seconds."));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _readLoop = Task.Run(() => ReadLoopAsync(_lifetime.Token), CancellationToken.None);

        try
        {
            await HandshakeAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation(
            "HTSP connected to {ServerName} {ServerVersion} at {Host}:{Port}; protocol version {Version} (server offers {ServerOffers}, client offers {ClientOffers})",
            Hello?.ServerName,
            Hello?.ServerVersion,
            _options.Host,
            _options.Port,
            ProtocolVersion,
            Hello?.ProtocolVersion,
            HtspConnectionOptions.SupportedProtocolVersion);
    }

    /// <summary>
    /// Sends a request and waits for the reply TVHeadend correlates to it.
    /// </summary>
    /// <param name="request">The request. A sequence number is added here.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="HtspRequestException">TVHeadend answered with an error.</exception>
    public async Task<HtspMessage> SendRequestAsync(HtspMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stream = _stream ?? throw new InvalidOperationException("This HTSP connection is not open.");
        var method = request.Method;

        // Wraps back into the positive range rather than into negative numbers: TVHeadend reads
        // seq as a uint32 and echoes it back, so a negative value would not round-trip.
        var sequence = Interlocked.Increment(ref _sequence) & 0x7FFFFFFF;
        request.Set("seq", sequence);

        var completion = new TaskCompletionSource<HtspMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(sequence, completion))
        {
            throw new HtspException("Two HTSP requests were given the same sequence number.");
        }

        try
        {
            await WriteAsync(stream, request, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(_options.RequestTimeout);

            HtspMessage reply;
            try
            {
                reply = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HtspException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"TVHeadend did not answer '{method}' within {_options.RequestTimeout.TotalSeconds:0} seconds."));
            }

            if (reply.GetString("error") is { } error)
            {
                throw new HtspRequestException(method, error);
            }

            if (reply.GetBoolean("noaccess"))
            {
                throw new HtspAuthenticationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The TVHeadend user '{_options.UserName}' is not allowed to call '{method}'."));
            }

            return reply;
        }
        finally
        {
            _pending.TryRemove(sequence, out _);
        }
    }

    /// <summary>
    /// Asks the server to start sending the channel, tag and DVR feed.
    /// </summary>
    /// <remarks>
    /// The reply arrives before the feed does, so a caller that needs a complete picture waits
    /// for <c>initialSyncCompleted</c> on <see cref="MessageReceived"/> rather than for this.
    /// </remarks>
    /// <param name="includeEpg">Whether to include the EPG feed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the server has accepted the request.</returns>
    public async Task EnableAsyncMetadataAsync(bool includeEpg, CancellationToken cancellationToken)
    {
        var request = HtspMessage.Create("enableAsyncMetadata");
        if (includeEpg)
        {
            request.Set("epg", 1L);
        }

        await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a subscription on a channel.
    /// </summary>
    /// <param name="channelId">The HTSP channel identifier.</param>
    /// <param name="options">How to subscribe.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The subscription, already registered for its asynchronous messages.</returns>
    public async Task<HtspSubscription> SubscribeAsync(
        int channelId,
        HtspSubscriptionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var subscriptionId = NextSubscriptionId();
        var subscription = new HtspSubscription(this, subscriptionId, channelId, _logger);

        // Registered before the request goes out: TVHeadend may send subscriptionStart the
        // instant it has a picture, and the reply and that message travel the same socket in
        // that order, so there is no window in which a message could arrive unrouted.
        _subscriptions[subscriptionId] = subscription;

        try
        {
            var request = HtspMessage.Create("subscribe")
                .Set("subscriptionId", subscriptionId)
                .Set("channelId", channelId)
                .Set("weight", options.Weight)
                .Set("90khz", 1L);

            if (!string.IsNullOrEmpty(options.Profile))
            {
                request.Set("profile", options.Profile);
            }

            if (options.QueueDepth is { } queueDepth)
            {
                request.Set("queueDepth", queueDepth);
            }

            await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);

            if (options.DisableAllStreams)
            {
                await subscription.DisableAllStreamsAsync(cancellationToken).ConfigureAwait(false);
            }

            return subscription;
        }
        catch
        {
            _subscriptions.TryRemove(subscriptionId, out _);
            throw;
        }
    }

    /// <summary>
    /// Sends a request that belongs to a subscription.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The reply.</returns>
    internal Task<HtspMessage> SendSubscriptionRequestAsync(HtspMessage request, CancellationToken cancellationToken)
        => SendRequestAsync(request, cancellationToken);

    /// <summary>
    /// Forgets a subscription, so its messages stop being routed.
    /// </summary>
    /// <param name="subscriptionId">The subscription identifier.</param>
    internal void Unregister(int subscriptionId) => _subscriptions.TryRemove(subscriptionId, out _);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);

        // Shutting the socket down is what unblocks a read that is parked on it; disposing the
        // stream alone can leave the read loop waiting on a handle nobody will ever complete.
        try
        {
            _socket?.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
            // Already gone.
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _socket?.Dispose();

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The read loop reports through Fail; anything left here is the teardown itself.
            }
        }

        Fail(new HtspException("The HTSP connection was closed."));

        _writeLock.Dispose();
        _lifetime.Dispose();
        _closed.TrySetResult();
    }

    private int NextSubscriptionId()
    {
        // Distinct from the request sequence: TVHeadend keys subscriptions by this alone, and a
        // collision would route one subscription's start message to another.
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var candidate = (int)(Interlocked.Increment(ref _sequence) & 0x00FFFFFF);
            if (candidate > 0 && !_subscriptions.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        throw new HtspException("No free HTSP subscription identifier could be found.");
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        var hello = HtspMessage.Create("hello")
            .Set("clientname", _options.ClientName)
            .Set("clientversion", _options.ClientVersion)
            .Set("htspversion", HtspConnectionOptions.SupportedProtocolVersion);

        if (!string.IsNullOrEmpty(_options.UserName))
        {
            hello.Set("username", _options.UserName);
        }

        var response = await SendRequestAsync(hello, cancellationToken).ConfigureAwait(false);
        Hello = HtspHello.From(response);

        if (string.IsNullOrEmpty(_options.UserName))
        {
            return;
        }

        var authenticate = HtspMessage.Create("authenticate")
            .Set("username", _options.UserName);

        // The challenge is what makes the digest specific to this connection. A server that
        // sends none is answered with a digest over the password alone, which is what TVHeadend
        // itself computes in that case.
        var challenge = Hello.Challenge ?? [];
        authenticate.Set("digest", ComputeDigest(_options.Password, challenge));

        var reply = await SendRequestAsync(authenticate, cancellationToken).ConfigureAwait(false);
        if (reply.GetBoolean("noaccess"))
        {
            throw new HtspAuthenticationException(string.Create(
                CultureInfo.InvariantCulture,
                $"TVHeadend refused the credentials for user '{_options.UserName}'."));
        }
    }

    private static byte[] ComputeDigest(string password, byte[] challenge)
    {
        // Dictated by the protocol: TVHeadend compares against SHA-1 over the password followed
        // by the challenge, so the algorithm is not this client's to choose.
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var buffer = new byte[passwordBytes.Length + challenge.Length];
        passwordBytes.CopyTo(buffer, 0);
        challenge.CopyTo(buffer, passwordBytes.Length);
#pragma warning disable CA5350 // The digest algorithm is fixed by the HTSP protocol.
        return SHA1.HashData(buffer);
#pragma warning restore CA5350
    }

    private async Task WriteAsync(Stream stream, HtspMessage message, CancellationToken cancellationToken)
    {
        var frame = HtspCodec.Encode(message);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var stream = _stream!;
        var header = new byte[HtspCodec.FrameHeaderLength];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                var length = HtspCodec.ReadBodyLength(header);

                var body = ArrayPool<byte>.Shared.Rent(length);
                HtspMessage message;
                try
                {
                    await stream.ReadExactlyAsync(body.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    message = HtspCodec.Decode(body.AsSpan(0, length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(body);
                }

                Dispatch(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (EndOfStreamException)
        {
            Fail(new HtspException("TVHeadend closed the HTSP connection."));
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            Fail(new HtspException("The HTSP connection was lost.", exception));
        }
        catch (HtspProtocolException exception)
        {
            _logger.LogError(exception, "HTSP framing is out of step; the connection cannot continue");
            Fail(exception);
        }
    }

    private void Dispatch(HtspMessage message)
    {
        if (message.GetInt64("seq") is { } sequence
            && _pending.TryRemove(sequence, out var completion))
        {
            completion.TrySetResult(message);
            return;
        }

        if (message.GetInt32("subscriptionId") is { } subscriptionId
            && _subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            subscription.Handle(message);
            return;
        }

        try
        {
            MessageReceived?.Invoke(this, message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A handler must never be able to stop the connection reading.
            _logger.LogError(exception, "An HTSP message handler threw on {Method}", message.Method);
        }
    }

    private void Fail(Exception exception)
    {
        foreach (var sequence in _pending.Keys)
        {
            if (_pending.TryRemove(sequence, out var completion))
            {
                completion.TrySetException(exception);
            }
        }

        foreach (var subscriptionId in _subscriptions.Keys)
        {
            if (_subscriptions.TryRemove(subscriptionId, out var subscription))
            {
                subscription.Fail(exception);
            }
        }

        _closed.TrySetResult();
    }
}
