using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using Tvheadend.Htsp;
using Tvheadend.Htsp.Model;

namespace TVHeadEnd.Streaming;

/// <summary>
/// One live channel: the transport stream TVHeadend delivers over HTTP, and the HTSP subscription
/// that says what is in it.
/// </summary>
/// <remarks>
/// <para>
/// The media path is TVHeadend's <c>pass</c> profile and nothing else -- the broadcast forwarded
/// untouched, with its own PCR, program tables and random access points intact. Alongside it runs
/// an HTSP subscription on the same service with every stream index filtered out, so TVHeadend
/// keeps parsing and keeps describing the stream without ever putting a frame of it on that
/// second socket.
/// </para>
/// <para>
/// The subscription lives as long as the stream does, rather than being read once and dropped.
/// That is what makes a mid-broadcast change in the elementary streams visible: TVHeadend sends a
/// fresh description, and the media source is corrected to match.
/// </para>
/// <para>
/// The buffer is a fixed-size ring, so a channel left running costs the same disk space after
/// eight hours as after one minute, and every reader joins it at a point a decoder can start
/// from.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "This is a Jellyfin ILiveStream, and 'stream' is what both Jellyfin and the domain call it.")]
public sealed class TvheadendLiveStream : ILiveStream, IDirectStreamProvider, IAsyncDisposable
{
    private const int StreamBufferSize = 131072;

    /// <summary>
    /// Enough of the stream for a client to begin. The conditioner puts the program tables and an
    /// access point at the very front, so this only has to cover the first few frames.
    /// </summary>
    private const int MinimumStartBufferSize = 65536;

    /// <summary>
    /// How long a connected stream has to produce something playable before the open fails.
    /// </summary>
    /// <remarks>
    /// Connecting proves only that TVHeadend accepted the subscription. A stream that never
    /// produces a usable entry point would otherwise leave the caller waiting on a task that
    /// never completes, which reaches the client as a spinner that never resolves. Failing is
    /// recoverable; hanging is not.
    /// </remarks>
    private static readonly TimeSpan StartupTimeLimit = TimeSpan.FromSeconds(20);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _upstreamUrl;
    private readonly IReadOnlyDictionary<string, string> _upstreamHeaders;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StreamBootstrapIndex _bootstrap = new();
    private readonly string _bufferPath;
    private readonly int _bufferSizeMegabytes;
    private readonly TimeSpan _startupTimeLimit;

    private TransportStreamConditioner? _conditioner;
    private HtspSubscription? _subscription;
    private Func<HtspSubscriptionStart, Task>? _onRedescribed;
    private Task? _feedTask;
    private bool _closed;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvheadendLiveStream"/> class.
    /// </summary>
    /// <param name="channelId">The TVHeadend channel identifier.</param>
    /// <param name="channelName">The channel name, for the log.</param>
    /// <param name="upstreamUrl">The TVHeadend stream URL, including the pass profile.</param>
    /// <param name="upstreamHeaders">The headers the request needs.</param>
    /// <param name="mediaSource">The media source this stream backs.</param>
    /// <param name="bufferPath">Where to write the buffer.</param>
    /// <param name="bufferSizeMegabytes">The configured buffer window.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="startupTimeLimit">
    /// How long a connected stream has to produce something playable. Defaults to the production
    /// bound; a test sets it shorter.
    /// </param>
    public TvheadendLiveStream(
        string channelId,
        string? channelName,
        string upstreamUrl,
        IReadOnlyDictionary<string, string> upstreamHeaders,
        MediaSourceInfo mediaSource,
        string bufferPath,
        int bufferSizeMegabytes,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        TimeSpan? startupTimeLimit = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ArgumentException.ThrowIfNullOrEmpty(upstreamUrl);
        ArgumentNullException.ThrowIfNull(upstreamHeaders);
        ArgumentNullException.ThrowIfNull(mediaSource);
        ArgumentException.ThrowIfNullOrEmpty(bufferPath);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        ChannelId = channelId;
        ChannelName = channelName;
        _upstreamUrl = upstreamUrl;
        _upstreamHeaders = upstreamHeaders;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _startupTimeLimit = startupTimeLimit ?? StartupTimeLimit;

        UniqueId = Guid.NewGuid().ToString("N");
        _bufferPath = bufferPath;
        _bufferSizeMegabytes = bufferSizeMegabytes;

        MediaSource = mediaSource;
        ConsumerCount = 1;
        EnableStreamSharing = true;
        OriginalStreamId = string.Empty;
        TunerHostId = string.Empty;
    }

    /// <summary>
    /// Gets the TVHeadend channel identifier.
    /// </summary>
    public string ChannelId { get; }

    /// <summary>
    /// Gets the channel name.
    /// </summary>
    public string? ChannelName { get; }

    /// <summary>
    /// Gets the buffer this stream fills.
    /// </summary>
    public LiveStreamBuffer Buffer { get; private set; } = null!;

    /// <summary>
    /// Gets a value indicating whether the buffer still exists.
    /// </summary>
    /// <remarks>
    /// A source whose buffer has gone is worse than no source: the client keeps asking for
    /// something that answers 404 instead of opening a fresh stream.
    /// </remarks>
    public bool HasBuffer => Buffer?.Exists == true;

    /// <summary>
    /// Gets the file the stream can be read as.
    /// </summary>
    public string MediaPath => Buffer?.Path ?? string.Empty;

    /// <summary>
    /// Gets the program map of the transport stream actually arriving, or <see langword="null"/>
    /// until one has been reassembled.
    /// </summary>
    public ProgramMapTable? ProgramMap => _conditioner?.ProgramMap;

    /// <inheritdoc />
    public int ConsumerCount { get; set; }

    /// <inheritdoc />
    public string OriginalStreamId { get; set; }

    /// <inheritdoc />
    public string TunerHostId { get; }

    /// <inheritdoc />
    public bool EnableStreamSharing { get; private set; }

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; }

    /// <summary>
    /// Hands the stream the HTSP subscription that describes it.
    /// </summary>
    /// <remarks>
    /// Ownership passes with it: the subscription is closed when the stream is, which is what
    /// releases TVHeadend's second claim on the service.
    /// </remarks>
    /// <param name="subscription">The subscription, already filtered down to metadata.</param>
    /// <param name="onRedescribed">
    /// What to do when TVHeadend describes the stream again, which it does whenever the broadcast
    /// changes shape.
    /// </param>
    public void AttachMetadata(HtspSubscription subscription, Func<HtspSubscriptionStart, Task> onRedescribed)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(onRedescribed);

        _subscription = subscription;
        _onRedescribed = onRedescribed;
        subscription.Started += OnSubscriptionStarted;
    }

    /// <inheritdoc />
    public async Task Open(CancellationToken openCancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Directory.CreateDirectory(Path.GetDirectoryName(_bufferPath)
            ?? throw new InvalidOperationException("The live TV buffer path has no parent directory."));

        var stopwatch = Stopwatch.StartNew();
        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, _upstreamUrl);
        foreach (var header in _upstreamHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        HttpResponseMessage response;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(openCancellationToken, _lifetime.Token);
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            request.Dispose();
            client.Dispose();
            throw;
        }

        request.Dispose();
        var upstream = await response.Content.ReadAsStreamAsync(_lifetime.Token).ConfigureAwait(false);

        Buffer = new LiveStreamBuffer(_bufferPath + ".ts", _bufferSizeMegabytes) { Bootstrap = _bootstrap };
        _feedTask = Feed(upstream, [client, response], _lifetime.Token);

        try
        {
            await _ready.Task.WaitAsync(_startupTimeLimit, openCancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            throw new TimeoutException(FormattableString.Invariant(
                $"TVHeadend accepted the subscription for channel {ChannelId} but produced nothing playable within {_startupTimeLimit.TotalSeconds:0} seconds."));
        }
        catch
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            throw;
        }

        _logger.LogDebug(
            "Live TV: HTTP pass opened for channel {ChannelId} ({ChannelName}); playable after {ElapsedMilliseconds} ms",
            ChannelId,
            ChannelName ?? "<unknown>",
            stopwatch.ElapsedMilliseconds);
    }

    /// <inheritdoc />
    public async Task Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        EnableStreamSharing = false;
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Buffer.OpenReader();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Jellyfin's <see cref="ILiveStream"/> is <see cref="IDisposable"/>, so this bridge has to
    /// exist. It cannot deadlock: everything <see cref="DisposeAsync"/> awaits is configured not
    /// to resume on the caller's synchronization context.
    /// </remarks>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EnableStreamSharing = false;

        if (_subscription is not null)
        {
            _subscription.Started -= OnSubscriptionStarted;
            await _subscription.DisposeAsync().ConfigureAwait(false);
            _subscription = null;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_feedTask is not null)
        {
            try
            {
                await _feedTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the last consumer goes away.
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogDebug(exception, "Live TV stream {UniqueId}: the feed ended with an error", UniqueId);
            }
        }

        if (Buffer is not null)
        {
            await Buffer.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();

        _logger.LogDebug("Live TV: stream closed for channel {ChannelId} ({ChannelName})", ChannelId, ChannelName ?? "<unknown>");
    }

    private void OnSubscriptionStarted(object? sender, HtspSubscriptionStart start)
    {
        var handler = _onRedescribed;
        if (handler is null || _disposed)
        {
            return;
        }

        // TVHeadend has described the stream again, which means the broadcast changed shape. The
        // media source is corrected on a background continuation rather than on the connection's
        // read loop, which must never be made to wait.
        _ = Task.Run(async () =>
        {
            try
            {
                await handler(start).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogWarning(
                    exception,
                    "Live TV: channel {ChannelId} was described again but the media source could not be updated",
                    ChannelId);
            }
        });
    }

    private async Task Feed(
        Stream upstream,
        IReadOnlyList<IDisposable> owned,
        CancellationToken cancellationToken)
    {
        byte[]? readBuffer = null;
        byte[]? conditionedBuffer = null;

        try
        {
            await using (upstream.ConfigureAwait(false))
            {
                readBuffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
                conditionedBuffer = ArrayPool<byte>.Shared.Rent(
                    TransportStreamConditioner.GetMaximumConditionedLength(StreamBufferSize));

                var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
                _conditioner = conditioner;
                var checkedContainer = false;

                while (true)
                {
                    var read = await upstream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (!checkedContainer)
                    {
                        checkedContainer = true;

                        // The pass profile is requested explicitly, so anything else means the
                        // server substituted a profile of its own. Said plainly here rather than
                        // left to surface as a startup timeout with no explanation.
                        if (!SourceContainer.IsTransportStream(readBuffer.AsSpan(0, read)))
                        {
                            throw new InvalidOperationException(FormattableString.Invariant(
                                $"TVHeadend did not deliver channel {ChannelId} as MPEG-TS. The plugin requests the 'pass' profile, so the server has substituted another one; check that 'pass' exists and that this user may use it."));
                        }
                    }

                    var conditioned = conditioner.Condition(readBuffer.AsSpan(0, read), conditionedBuffer);
                    if (conditioned == 0)
                    {
                        continue;
                    }

                    await Buffer.Write(
                        conditionedBuffer.AsMemory(0, conditioned),
                        conditioner.RandomAccessOffsets,
                        cancellationToken).ConfigureAwait(false);

                    conditioner.PublishProgramTables(_bootstrap);

                    if (!_ready.Task.IsCompleted && Buffer.WritePosition >= MinimumStartBufferSize)
                    {
                        _ready.TrySetResult();
                    }
                }

                if (!_ready.Task.IsCompleted)
                {
                    _ready.TrySetException(new EndOfStreamException(
                        "TVHeadend closed the live stream before sending enough data."));
                }
            }
        }
        catch (OperationCanceledException exception)
        {
            _ready.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ready.TrySetException(exception);
            _logger.LogError(exception, "Live TV: the stream for channel {ChannelId} stopped unexpectedly", ChannelId);
        }
        finally
        {
            EnableStreamSharing = false;

            if (readBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(readBuffer);
            }

            if (conditionedBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(conditionedBuffer);
            }

            foreach (var resource in owned)
            {
                resource.Dispose();
            }
        }
    }
}
