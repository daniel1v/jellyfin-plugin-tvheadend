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
public sealed class TvheadendConnection : ITvheadendHttpEndpointSource, IAsyncDisposable
{
    /// <summary>
    /// How many times a connect is retried when the configuration changes underneath it.
    /// </summary>
    private const int MaximumConnectAttempts = 3;

    private readonly ILoggerFactory _loggerFactory;
    private readonly ITvheadendSettingsSource _settingsSource;
    private readonly ILogger<TvheadendConnection> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private HtspSession? _session;
    private HtspSession? _owner;
    private TvheadendSettings? _settings;
    private int _configurationGeneration;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvheadendConnection"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="settings">Which server to talk to, and word when that changes.</param>
    public TvheadendConnection(ILoggerFactory loggerFactory, ITvheadendSettingsSource settings)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(settings);

        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TvheadendConnection>();
        _settingsSource = settings;
        _settingsSource.Changed += OnSettingsChanged;

        Channels = new ChannelCatalog(loggerFactory.CreateLogger<ChannelCatalog>());
        Dvr = new DvrCatalog(loggerFactory.CreateLogger<DvrCatalog>());
        SeriesRules = new SeriesRuleCatalog(loggerFactory.CreateLogger<SeriesRuleCatalog>());
        ChannelTags = new ChannelTagCatalog();
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
    /// Gets the channel tags TVHeadend has announced, which are its own grouping of its channels.
    /// </summary>
    public ChannelTagCatalog ChannelTags { get; }

    /// <summary>
    /// Gets the validated settings, reading them if this is the first ask.
    /// </summary>
    /// <remarks>
    /// Held once read, because the comparison that decides whether a settings change is worth
    /// reconnecting for needs both the old answer and the new one -- see
    /// <see cref="ApplyConfiguration"/>.
    /// </remarks>
    public TvheadendSettings Settings => _settings ??= _settingsSource.Current;

    /// <summary>
    /// Gets where TVHeadend's HTTP interface is and how to authenticate to it.
    /// </summary>
    /// <remarks>
    /// The web root is only known once the server has said so in its handshake, so this is only
    /// correct after a connection has been established at least once. Callers that need it for
    /// an address reach it through <see cref="GetHttpEndpointAsync"/>.
    /// </remarks>
    public TvheadendHttpEndpoint HttpEndpoint
        => _session?.Endpoint ?? new TvheadendHttpEndpoint(
            Settings.Host,
            Settings.HttpPort,
            string.Empty,
            Settings.UserName,
            Settings.Password);

    /// <summary>
    /// Gets the HTTP endpoint, connecting first so that the server's web root is known.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The endpoint.</returns>
    public async Task<TvheadendHttpEndpoint> GetHttpEndpointAsync(CancellationToken cancellationToken)
    {
        var session = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return session.Endpoint;
    }

    /// <summary>
    /// Gets how far the TVHeadend server's own clock is from UTC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed for one thing only: an autorec entry states its start window as minutes from
    /// midnight on the <em>server's</em> clock. Reading those with this process's own time zone
    /// is wrong whenever Jellyfin and TVHeadend are not in the same one -- a container running in
    /// UTC beside a server in Berlin moved every rule by two hours -- and reading them as UTC, as
    /// this did, is wrong whenever the server is not.
    /// </para>
    /// <para>
    /// <c>getSysTime</c> answers with <c>gmtoffset</c>, the minutes east of GMT the server is
    /// currently at. That is an offset and not a time zone: it is correct now, and it is the only
    /// thing the protocol offers -- HTSP never names the zone, and guessing one from an offset
    /// would be a guess.
    /// </para>
    /// <para>
    /// Asked afresh every time it is needed, and only when it is needed. An HTSP connection can
    /// stand open for months, so an offset cached on it is an offset that is right until the
    /// clocks change and wrong afterwards, with nothing to notice. The operations that do not
    /// need it -- an edit to a rule whose window is being kept as the server already has it -- do
    /// not ask at all.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The offset from UTC.</returns>
    public async Task<TimeSpan> GetServerOffsetAsync(CancellationToken cancellationToken)
    {
        var session = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var reply = await session.Connection
                .SendRequestAsync(HtspMessage.Create("getSysTime"), cancellationToken)
                .ConfigureAwait(false);

            return ReadServerOffset(reply);
        }
        catch (HtspException exception)
        {
            // A server that will not say. UTC is the same assumption as before this existed, and
            // a series rule read an hour out is better than one that cannot be read at all.
            _logger.LogWarning(exception, "TVHeadend would not report its clock; assuming UTC");
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Reads the server's offset from a <c>getSysTime</c> reply.
    /// </summary>
    /// <remarks>
    /// <c>gmtoffset</c> is minutes east of GMT. A server that does not send it is taken to be at
    /// UTC, which is what this plugin assumed of every server before it asked.
    /// </remarks>
    /// <param name="reply">The reply.</param>
    /// <returns>The offset from UTC.</returns>
    internal static TimeSpan ReadServerOffset(HtspMessage reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        if (reply.GetInt32("gmtoffset") is not { } minutes)
        {
            return TimeSpan.Zero;
        }

        // Beyond a day either way is not an offset any clock has, so it is a field this reply
        // means something else by.
        return minutes is > -1440 and < 1440 ? TimeSpan.FromMinutes(minutes) : TimeSpan.Zero;
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
    /// Takes the configuration as it now stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two kinds of setting, and they are treated differently because they mean different things.
    /// Anything that names the server -- host, either port, the credentials -- describes a
    /// connection, so the one in hand is retired and the next operation opens a replacement
    /// against the new address. Everything else is a parameter of the next operation and is simply
    /// read when that operation happens.
    /// </para>
    /// <para>
    /// Nothing is disposed or reconnected here. The replacement happens under the connect lock
    /// when something next needs a connection, so a configuration save does not start work of its
    /// own, and a live stream already running is left alone -- it holds an HTTP response that owes
    /// nothing to this connection.
    /// </para>
    /// </remarks>
    public void ApplyConfiguration()
    {
        var previous = _settings;
        _settings = null;

        if (previous is null)
        {
            return;
        }

        var current = Settings;
        if (DescribesSameServer(previous, current))
        {
            return;
        }

        // Everything opened under the configuration before this is now out of date --
        // including a connect that is still in progress and has no session to retire yet.
        Interlocked.Increment(ref _configurationGeneration);

        _session?.Retire();
        _logger.LogInformation(
            "The TVHeadend server settings changed; the next operation opens a connection to {Host}",
            current.Host);
    }

    private void OnSettingsChanged(object? sender, EventArgs arguments) => ApplyConfiguration();

    private static bool DescribesSameServer(TvheadendSettings first, TvheadendSettings second)
        => string.Equals(first.Host, second.Host, StringComparison.Ordinal)
        && first.HtspPort == second.HtspPort
        && first.HttpPort == second.HttpPort
        && string.Equals(first.UserName, second.UserName, StringComparison.Ordinal)
        && string.Equals(first.Password, second.Password, StringComparison.Ordinal);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsSource.Changed -= OnSettingsChanged;

        if (_session is { } session)
        {
            _session = null;
            _owner = null;
            await session.Connection.DisposeAsync().ConfigureAwait(false);
        }

        _connectLock.Dispose();
    }

    private async Task<HtspSession> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var existing = _session;
        if (existing is { IsRetired: false, Connection.IsConnected: true })
        {
            return existing;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = _session;
            if (existing is { IsRetired: false, Connection.IsConnected: true })
            {
                return existing;
            }

            if (existing is not null)
            {
                // Cleared before the replacement is opened, so a failed reconnect leaves no
                // session at all rather than the dead one it was meant to replace.
                _session = null;
                _owner = null;
                _logger.LogInformation("The HTSP connection to TVHeadend was lost; opening a new one");
                await existing.Connection.DisposeAsync().ConfigureAwait(false);
            }

            // A connect discarded because the configuration changed under it is retried with
            // the settings that replaced it. Bounded, so a configuration being saved repeatedly
            // cannot keep a caller here.
            for (var attempt = 0; attempt < MaximumConnectAttempts; attempt++)
            {
                if (await ConnectAsync(cancellationToken).ConfigureAwait(false) is { } session)
                {
                    return session;
                }
            }

            throw new HtspException(
                "The TVHeadend settings kept changing while connecting, so no connection could be established.");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<HtspSession?> ConnectAsync(CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _configurationGeneration);
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

        try
        {
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // Everything the server is about to re-send replaces what the previous connection left
        // behind, so the catalogs start empty rather than merging two pictures of the world.
        Channels.Clear();
        Dvr.Clear();
        SeriesRules.Clear();
        ChannelTags.Clear();

        var session = new HtspSession(connection, settings, NormalizeWebRoot(connection.Hello?.WebRoot));

        // The catalogs belong to this connection from here on, and so does everything it says.
        // Subscribed before the metadata is asked for, because the server starts answering
        // immediately: a handler wired up afterwards, or one that routed through whichever session
        // happened to be published, would drop the messages that arrive in between -- including
        // the one that says the picture is complete, leaving every caller waiting for a sync that
        // had already happened.
        _owner = session;
        connection.MessageReceived += (_, message) => OnMessageReceived(session, message);

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
            _owner = null;
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // A connect takes as long as a handshake, an authentication and a metadata request,
        // and the configuration can be saved during any of them. Publishing a session built from
        // settings that have since been replaced would make the old server the current one until
        // something else happened to fail. So the generation is checked here, where the session
        // finally becomes visible, and a stale one is thrown away instead.
        if (Volatile.Read(ref _configurationGeneration) != generation)
        {
            _logger.LogInformation(
                "The TVHeadend settings changed while connecting to {Host}; that connection is discarded",
                settings.Host);

            _owner = null;
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        // Published last. Until the handshake, the authentication and the metadata subscription
        // have all gone through, this connection cannot answer anything a caller would ask of it,
        // and a caller that found it here would think otherwise.
        _session = session;

        return session;
    }

    private void OnMessageReceived(HtspSession session, HtspMessage message)
    {
        // A connection that has been replaced may still have messages in flight. They describe a
        // world that is no longer being maintained, and letting them reach the catalogs would have
        // an old connection editing a new one's picture.
        if (!ReferenceEquals(_owner, session))
        {
            return;
        }

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

            // The server's own grouping of its channels. It sends these before the channels that
            // reference them, and then a second round carrying the members it has worked out --
            // which is why the catalogue merges rather than replaces.
            case "tagAdd":
            case "tagUpdate":
                ChannelTags.AddOrUpdate(message);
                break;

            case "tagDelete":
                ChannelTags.Remove(message);
                break;

            case "initialSyncCompleted":
                _logger.LogInformation(
                    "TVHeadend finished its initial sync: {ChannelCount} channels, {TagCount} tags, {DvrCount} DVR entries, {RuleCount} series rules",
                    Channels.Count,
                    ChannelTags.Count,
                    Dvr.Count,
                    SeriesRules.Count);
                session.InitialSync.TrySetResult();
                break;

            default:
                // Timerec entries and the EPG feed, neither of which this plugin surfaces.
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
        internal HtspSession(HtspConnection connection, TvheadendSettings settings, string webRoot)
        {
            Connection = connection;
            Settings = settings;
            Endpoint = new TvheadendHttpEndpoint(
                settings.Host,
                settings.HttpPort,
                webRoot,
                settings.UserName,
                settings.Password);
        }

        internal HtspConnection Connection { get; }

        /// <summary>
        /// Gets the settings this connection was opened with.
        /// </summary>
        internal TvheadendSettings Settings { get; }

        /// <summary>
        /// Gets the HTTP endpoint of this connection's server.
        /// </summary>
        /// <remarks>
        /// Built once, from the settings that opened the connection and the web root that
        /// connection's handshake reported. Assembling it later from whatever the configuration
        /// says now and whatever web root was last seen can produce an address that belonged to
        /// neither server.
        /// </remarks>
        internal TvheadendHttpEndpoint Endpoint { get; }

        internal TaskCompletionSource InitialSync { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets a value indicating whether this session has been superseded by a configuration
        /// change and should be replaced at the next opportunity.
        /// </summary>
        internal bool IsRetired { get; private set; }

        internal void Retire() => IsRetired = true;
    }
}
