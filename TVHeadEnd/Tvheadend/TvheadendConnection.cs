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

    private HtspConnection? _connection;
    private TaskCompletionSource _initialSync = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        var connection = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await connection.SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until TVHeadend has sent its whole channel and DVR picture.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the catalogs are complete.</returns>
    public async Task WaitForInitialSyncAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _initialSync.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _connectLock.Dispose();
    }

    private async Task<HtspConnection> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var existing = _connection;
        if (existing is { IsConnected: true })
        {
            return existing;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = _connection;
            if (existing is { IsConnected: true })
            {
                return existing;
            }

            if (existing is not null)
            {
                _logger.LogInformation("The HTSP connection to TVHeadend was lost; opening a new one");
                await existing.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }

            return await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<HtspConnection> ConnectAsync(CancellationToken cancellationToken)
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

        _webRoot = NormalizeWebRoot(connection.Hello?.WebRoot);

        // Everything the server is about to re-send replaces what the previous connection left
        // behind, so the catalogs start empty rather than merging two pictures of the world.
        Channels.Clear();
        Dvr.Clear();
        SeriesRules.Clear();
        _initialSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _connection = connection;

        // Without EPG: programmes are fetched per channel when Jellyfin asks for them, and
        // asking for the whole guide up front would push a very large amount of data that
        // nothing reads.
        await connection.EnableAsyncMetadataAsync(includeEpg: false, cancellationToken).ConfigureAwait(false);

        return connection;
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
                _initialSync.TrySetResult();
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
}
