using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tvheadend.Htsp.Model;
using Tvheadend.Htsp.Protocol;

namespace Tvheadend.Htsp;

/// <summary>
/// One TVHeadend subscription, and what the server says about the stream behind it.
/// </summary>
/// <remarks>
/// <para>
/// The authoritative description of a running stream. TVHeadend has already parsed the
/// broadcast to produce it -- the same parse that feeds its own muxers -- so this is the
/// server's own analysis rather than a second one made from the delivered bytes.
/// </para>
/// <para>
/// A subscription can be described more than once. Whenever the broadcast changes shape the
/// server sends a fresh <c>subscriptionStart</c>, and <see cref="Start"/> is replaced. That is
/// the whole reason the subscription outlives the first description instead of being closed
/// once it has been read.
/// </para>
/// </remarks>
public sealed class HtspSubscription : IAsyncDisposable
{
    /// <summary>
    /// The number of stream indices TVHeadend keeps filter bits for.
    /// </summary>
    /// <remarks>
    /// <c>NUM_FILTERED_STREAMS</c> in the server, as eight 64-bit words. Every index is
    /// disabled rather than only the ones a description mentions, which also sidesteps a
    /// defect in the server's bit arithmetic: it shifts a plain <c>int</c> by up to 63 places,
    /// so the upper half of each word aliases the lower. Disabling the whole range sets every
    /// bit that any index could land on regardless.
    /// </remarks>
    public const int FilteredStreamCount = 512;

    private readonly HtspConnection _connection;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private TaskCompletionSource<HtspSubscriptionStart> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _disposed;

    internal HtspSubscription(HtspConnection connection, int subscriptionId, int channelId, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
        SubscriptionId = subscriptionId;
        ChannelId = channelId;
        State = HtspSubscriptionState.Starting;
    }

    /// <summary>
    /// Raised whenever the server describes the stream, including every later redescription.
    /// </summary>
    public event EventHandler<HtspSubscriptionStart>? Started;

    /// <summary>
    /// Raised when the server reports a change in the subscription's condition.
    /// </summary>
    public event EventHandler<HtspSubscriptionStatus>? StatusChanged;

    /// <summary>
    /// Raised when the server stops the subscription.
    /// </summary>
    public event EventHandler<HtspSubscriptionStop>? Stopped;

    /// <summary>
    /// Gets the identifier this client gave the subscription, which TVHeadend echoes on every
    /// message belonging to it.
    /// </summary>
    public int SubscriptionId { get; }

    /// <summary>
    /// Gets the channel subscribed to.
    /// </summary>
    public int ChannelId { get; }

    /// <summary>
    /// Gets where the subscription has got to.
    /// </summary>
    public HtspSubscriptionState State { get; private set; }

    /// <summary>
    /// Gets the current description of the stream, or <see langword="null"/> before the first
    /// one arrives.
    /// </summary>
    public HtspSubscriptionStart? Start { get; private set; }

    /// <summary>
    /// Gets where the stream is coming from, or <see langword="null"/> before it is described.
    /// </summary>
    public HtspSourceInfo? SourceInfo => Start?.SourceInfo;

    /// <summary>
    /// Waits for the server to describe the stream.
    /// </summary>
    /// <remarks>
    /// TVHeadend withholds <c>subscriptionStart</c> for a video service until it has parsed a
    /// frame size, so this completes when the description is genuinely usable rather than when
    /// the tuner was merely allocated.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The first description of the stream.</returns>
    public Task<HtspSubscriptionStart> WaitForStartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Start is { } start)
            {
                return Task.FromResult(start);
            }

            return _started.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Filters out every stream index, so the server keeps the subscription running but stops
    /// serialising audio and video onto the socket.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the server has applied the filter.</returns>
    public async Task DisableAllStreamsAsync(CancellationToken cancellationToken)
    {
        var request = HtspMessage.Create("subscriptionFilterStream")
            .Set("subscriptionId", SubscriptionId)
            .Set("disable", Enumerable.Range(0, FilteredStreamCount).Select(index => (long)index));

        await _connection.SendSubscriptionRequestAsync(request, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "HTSP subscription {SubscriptionId}: all {Count} stream indices filtered; only metadata will arrive",
            SubscriptionId,
            FilteredStreamCount);
    }

    /// <summary>
    /// Closes the subscription on the server.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the server has released the tuner.</returns>
    public async Task UnsubscribeAsync(CancellationToken cancellationToken)
    {
        var request = HtspMessage.Create("unsubscribe").Set("subscriptionId", SubscriptionId);
        await _connection.SendSubscriptionRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _connection.Unregister(SubscriptionId);

        try
        {
            if (_connection.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await UnsubscribeAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is HtspException or OperationCanceledException)
        {
            // The tuner is released when the connection drops in any case; failing to say so
            // politely is not worth propagating out of a dispose.
            _logger.LogDebug(
                "HTSP subscription {SubscriptionId} could not be closed cleanly: {Reason}",
                SubscriptionId,
                exception.Message);
        }

        lock (_gate)
        {
            State = HtspSubscriptionState.Stopped;
            _started.TrySetCanceled();
        }
    }

    internal void Handle(HtspMessage message)
    {
        switch (message.Method)
        {
            case "subscriptionStart":
                HandleStart(message);
                break;

            case "subscriptionStatus":
                HandleStatus(message);
                break;

            case "subscriptionStop":
                HandleStop(message);
                break;

            case "muxpkt":
                // Everything is filtered, so a packet here means the filter has not taken effect
                // yet -- the few frames between the subscription existing and the filter being
                // applied. Dropped without a word: logging per packet is exactly what this
                // design exists to avoid.
                break;

            default:
                // subscriptionGrace, subscriptionSkip, subscriptionSpeed, signalStatus,
                // timeshiftStatus, queueStatus, descrambleInfo. None of them change what the
                // stream is, which is all this subscription is for.
                break;
        }
    }

    internal void Fail(Exception exception)
    {
        lock (_gate)
        {
            State = HtspSubscriptionState.Faulted;
            _started.TrySetException(exception);
        }
    }

    private void HandleStart(HtspMessage message)
    {
        HtspSubscriptionStart start;
        try
        {
            start = HtspSubscriptionStart.From(message);
        }
        catch (HtspProtocolException exception)
        {
            _logger.LogWarning(
                exception,
                "HTSP subscription {SubscriptionId} sent a start message that could not be read",
                SubscriptionId);
            return;
        }

        bool isRedescription;
        lock (_gate)
        {
            isRedescription = Start is not null;
            Start = start;
            State = HtspSubscriptionState.Running;

            if (!_started.Task.IsCompleted)
            {
                _started.TrySetResult(start);
            }
            else if (isRedescription)
            {
                // A later waiter must see the current description, not the first one.
                _started = new TaskCompletionSource<HtspSubscriptionStart>(TaskCreationOptions.RunContinuationsAsynchronously);
                _started.TrySetResult(start);
            }
        }

        _logger.LogDebug(
            "HTSP subscription {SubscriptionId} {What}: {StreamCount} streams from {Source}",
            SubscriptionId,
            isRedescription ? "redescribed" : "started",
            start.Streams.Count,
            start.SourceInfo);

        Started?.Invoke(this, start);
    }

    private void HandleStatus(HtspMessage message)
    {
        var status = HtspSubscriptionStatus.From(message);
        if (status.Status is not null || status.SubscriptionError is not null)
        {
            _logger.LogWarning(
                "HTSP subscription {SubscriptionId} reports {Status}{Error}",
                SubscriptionId,
                status.Status ?? "a problem",
                status.SubscriptionError is null
                    ? string.Empty
                    : string.Create(CultureInfo.InvariantCulture, $" ({status.SubscriptionError})"));
        }

        StatusChanged?.Invoke(this, status);
    }

    private void HandleStop(HtspMessage message)
    {
        var stop = HtspSubscriptionStop.From(message);

        lock (_gate)
        {
            State = HtspSubscriptionState.Stopped;
            _started.TrySetException(new HtspException(string.Create(
                CultureInfo.InvariantCulture,
                $"TVHeadend stopped the subscription before describing the stream: {stop.Status ?? "no reason given"}.")));
        }

        _logger.LogInformation(
            "HTSP subscription {SubscriptionId} stopped by the server: {Status}",
            SubscriptionId,
            stop.Status ?? "no reason given");

        Stopped?.Invoke(this, stop);
    }
}
