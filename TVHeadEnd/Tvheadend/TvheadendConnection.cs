using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tvheadend.Htsp;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.Tvheadend.Catalogs;

namespace TVHeadEnd.Tvheadend;

/// <summary>
/// The plugin's one connection to TVHeadend, and everything the server has told it.
/// </summary>
/// <remarks>
/// <para>
/// One HTSP connection carries the channel list, the guide, the DVR feed and every request the
/// plugin makes. Live playback is not on it: the broadcast arrives over HTTP and describes
/// itself, so a running channel costs this connection nothing.
/// </para>
/// <para>
/// Reconnection is deliberately unambitious. A lost connection fails everything waiting on it,
/// and the next operation opens a fresh one; nothing is silently re-established behind a
/// caller's back. A live stream already open is unaffected, because it does not depend on this
/// connection once it has started.
/// </para>
/// </remarks>
public sealed class TvheadendConnection : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TvheadendConnection> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private HtspSession? _session;
    private TvheadendSettings? _settings;
    private string _webRoot = string.Empty;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvheadendConnection"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    public TvheadendConnection(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TvheadendConnection>();

        Channels = new ChannelCatalog(loggerFactory.CreateLogger<ChannelCatalog>());
        Dvr = new DvrCatalog(loggerFactory.CreateLogger<DvrCatalog>());
        SeriesRules = new SeriesRuleCatalog(loggerFactory.CreateLogger<SeriesRuleCatalog>());
    }

    /// <summary>
    /// Gets the channels TVHeadend has announced.
    /// </summary>
    public ChannelCatalog Channels { get; }

    /// <summary>
    /// Gets the DVR entries TVHeadend has announced, which are its timers and its recordings.
    /// </summary>
    public DvrCatalog Dvr { get; }

    /// <summary>
    /// Gets the series rules TVHeadend has announced.
    /// </summary>
    public SeriesRuleCatalog SeriesRules { get; }

    /// <summary>
    /// Gets the validated settings, reading them if this is the first ask.
    /// </summary>
    public TvheadendSettings Settings => _settings ??= TvheadendSettings.From(Plugin.Instance.Configuration);

    /// <summary>
    /// Gets where TVHeadend's HTTP interface is and how to authenticate to it.
    /// </summary>
    /// <remarks>
    /// The web root is only known once the server has said so in its handshake, so this is only
    /// correct after a connection has been established at least once. Callers that need it for
    /// an address reach it through <see cref="GetHttpEndpointAsync"/>.
    /// </remarks>
    public TvheadendHttpEndpoint HttpEndpoint => new(
        Settings.Host,
        Settings.HttpPort,
        _webRoot,
        Settings.UserName,
        Settings.Password);

    /// <summary>
    /// Gets the HTTP endpoint, connecting first so that the server's web root is known.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The endpoint.</returns>
    public async Task<TvheadendHttpEndpoint> GetHttpEndpointAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return HttpEndpoint;
    }

    /// <summary>
    /// Sends a request, connecting first if necessary.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The reply.</returns>
    public async Task<HtspMessage> SendAsync(HtspMessage request, CancellationToken cancellationToken)
    {
        var session = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await session.Connection.SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until TVHeadend has sent its whole channel and DVR picture.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the catalogs are complete.</returns>
    public async Task WaitForInitialSyncAsync(CancellationToken cancellationToken)
    {
        // The session, not the field: waiting on whichever connection happens to be current when
        // the await resumes is how a caller ends up waiting for a picture of the world that a
        // connection it never used is no longer sending.
        var session = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await session.InitialSync.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Discards the cached settings, so the next operation reads the configuration again.
    /// </summary>
    public void InvalidateSettings() => _settings = null;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_session is { } session)
        {
            _session = null;
            await session.Connection.DisposeAsync().ConfigureAwait(false);
        }

        _connectLock.Dispose();
    }

    private async Task<HtspSession> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var existing = _session;
        if (existing is { Connection.IsConnected: true })
        {
            return existing;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = _session;
            if (existing is { Connection.IsConnected: true })
            {
                return existing;
            }

            if (existing is not null)
            {
                // Cleared before the replacement is opened, so a failed reconnect leaves no
                // session at all rather than the dead one it was meant to replace.
                _session = null;
                _logger.LogInformation("The HTSP connection to TVHeadend was lost; opening a new one");
                await existing.Connection.DisposeAsync().ConfigureAwait(false);
            }

            return await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<HtspSession> ConnectAsync(CancellationToken cancellationToken)
    {
        var settings = Settings;
        var version = typeof(TvheadendConnection).Assembly.GetName().Version;

        var connection = new HtspConnection(
            new HtspConnectionOptions
            {
                Host = settings.Host,
                Port = settings.HtspPort,
                UserName = settings.UserName,
                Password = settings.Password,
                ClientName = "Jellyfin",
                ClientVersion = version?.ToString() ?? "unknown",
            },
            _loggerFactory.CreateLogger<HtspConnection>());

        connection.MessageReceived += OnMessageReceived;

        try
        {
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            connection.MessageReceived -= OnMessageReceived;
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // Everything the server is about to re-send replaces what the previous connection left
        // behind, so the catalogs start empty rather than merging two pictures of the world.
        Channels.Clear();
        Dvr.Clear();
        SeriesRules.Clear();

        var session = new HtspSession(connection);

        // However this connection ends, everyone waiting for the server's picture of the world is
        // told. Without it a waiter outlives the connection it was waiting on and never returns,
        // which reaches a caller as a request that simply never answers.
        _ = connection.Closed.ContinueWith(
            _ => session.InitialSync.TrySetException(new HtspException(
                "The HTSP connection ended before TVHeadend finished sending its channels and recordings.")),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            // Without EPG: programmes are fetched per channel when Jellyfin asks for them, and
            // asking for the whole guide up front would push a very large amount of data that
            // nothing reads.
            await connection.EnableAsyncMetadataAsync(includeEpg: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            connection.MessageReceived -= OnMessageReceived;
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _webRoot = NormalizeWebRoot(connection.Hello?.WebRoot);

        // Published last. Until the handshake, the authentication and the metadata subscription
        // have all gone through, this connection cannot answer anything a caller would ask of it,
        // and a caller that found it here would think otherwise.
        _session = session;

        return session;
    }

    private void OnMessageReceived(object? sender, HtspMessage message)
    {
        switch (message.Method)
        {
            case "channelAdd":
            case "channelUpdate":
                Channels.AddOrUpdate(message);
                break;

            case "channelDelete":
                Channels.Remove(message);
                break;

            case "dvrEntryAdd":
                Dvr.Add(message);
                break;

            case "dvrEntryUpdate":
                Dvr.Update(message);
                break;

            case "dvrEntryDelete":
                Dvr.Remove(message);
                break;

            case "autorecEntryAdd":
            case "autorecEntryUpdate":
                SeriesRules.AddOrUpdate(message);
                break;

            case "autorecEntryDelete":
                SeriesRules.Remove(message);
                break;

            case "initialSyncCompleted":
                _logger.LogInformation(
                    "TVHeadend finished its initial sync: {ChannelCount} channels, {DvrCount} DVR entries, {RuleCount} series rules",
                    Channels.Count,
                    Dvr.Count,
                    SeriesRules.Count);
                _session?.InitialSync.TrySetResult();
                break;

            default:
                // Tags, timerec entries and the EPG feed, none of which this plugin surfaces.
                break;
        }
    }

    private static string NormalizeWebRoot(string? webRoot)
    {
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            return string.Empty;
        }

        var trimmed = webRoot.Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    /// <summary>
    /// One connection together with the state that only means anything alongside it.
    /// </summary>
    /// <remarks>
    /// The initial sync belongs to a particular connection: it is the moment that connection
    /// finished describing the world, and it is meaningless once that connection has gone. Held
    /// beside it rather than in a field of its own, so that a caller which waited for a connection
    /// waits for that connection's sync and not for whichever one happens to be current later.
    /// </remarks>
    private sealed class HtspSession
    {
        internal HtspSession(HtspConnection connection)
        {
            Connection = connection;
        }

        internal HtspConnection Connection { get; }

        internal TaskCompletionSource InitialSync { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
