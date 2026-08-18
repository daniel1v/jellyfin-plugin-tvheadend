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
using TVHeadEnd.Media;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// A rendering of a channel made for one playback session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything the native path does to let a viewer join a broadcast already in progress --
    /// transport stream conditioning, a ring buffer, hunting for a safe entry point -- exists
    /// because that stream is shared and was started before the viewer arrived. None of it
    /// applies here. A compatibility stream is started by the session that wants it, begins at
    /// the first byte a freshly started encoder produces, and is never handed to anyone else.
    /// </para>
    /// <para>
    /// So this receives the body, spools it, and serves it. It deliberately does not implement
    /// <c>IDirectStreamProvider</c>: Jellyfin serves such a stream with a hardcoded MPEG-TS
    /// content type, which would misdescribe a Matroska rendering to every client that believes
    /// the declaration.
    /// </para>
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1711:Identifiers should not have incorrect suffix",
        Justification = "This is a Jellyfin ILiveStream, and 'stream' is what both Jellyfin and the domain call it.")]
    public sealed class CompatibilityLiveStream : ILiveStream, ITvheadendStream, IAsyncDisposable
    {
        /// <summary>
        /// How much has to arrive before the session is handed over. Enough for the container
        /// header and the first pictures, so what is published can also be inspected.
        /// </summary>
        private const int MinimumStartBytes = 256 * 1024;

        private const int ReadBufferSize = 65536;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly string _upstreamUrl;
        private readonly IReadOnlyDictionary<string, string> _upstreamHeaders;
        private readonly string _spoolPath;
        private readonly TimeSpan _startupTimeLimit;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private SessionSpool? _spool;
        private Task? _feedTask;
        private bool _closed;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="CompatibilityLiveStream"/> class.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="role">Which form of the channel this serves.</param>
        /// <param name="container">The container the source delivers, as a file extension.</param>
        /// <param name="upstreamUrl">The TVHeadend stream URL, including its profile.</param>
        /// <param name="upstreamHeaders">The headers the request needs.</param>
        /// <param name="mediaSource">The media source this stream backs.</param>
        /// <param name="spoolPath">Where to spool the session, without an extension.</param>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="startupTimeLimit">
        /// How long a connected source has to produce something. Defaults to the production
        /// bound; a test sets it shorter.
        /// </param>
        public CompatibilityLiveStream(
            string channelId,
            StreamProfileRole role,
            string container,
            string upstreamUrl,
            IReadOnlyDictionary<string, string> upstreamHeaders,
            MediaSourceInfo mediaSource,
            string spoolPath,
            IHttpClientFactory httpClientFactory,
            ILogger logger,
            TimeSpan? startupTimeLimit = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);
            ArgumentException.ThrowIfNullOrEmpty(container);
            ArgumentException.ThrowIfNullOrEmpty(upstreamUrl);
            ArgumentNullException.ThrowIfNull(upstreamHeaders);
            ArgumentNullException.ThrowIfNull(mediaSource);
            ArgumentException.ThrowIfNullOrEmpty(spoolPath);
            ArgumentNullException.ThrowIfNull(httpClientFactory);
            ArgumentNullException.ThrowIfNull(logger);

            ChannelId = channelId;
            Role = role;
            Container = container;
            _upstreamUrl = upstreamUrl;
            _upstreamHeaders = upstreamHeaders;
            _spoolPath = spoolPath + "." + container;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _startupTimeLimit = startupTimeLimit ?? TimeSpan.FromSeconds(20);

            UniqueId = Guid.NewGuid().ToString("N");
            MediaSource = mediaSource;
            ConsumerCount = 1;
            OriginalStreamId = string.Empty;
            TunerHostId = string.Empty;
        }

        /// <inheritdoc />
        public string ChannelId { get; }

        /// <inheritdoc />
        public StreamProfileRole Role { get; }

        /// <summary>
        /// Gets the container this session delivers, as a file extension.
        /// </summary>
        public string Container { get; }

        /// <inheritdoc />
        public string MediaPath => _spool?.Path ?? _spoolPath;

        /// <inheritdoc />
        /// <remarks>
        /// Nothing is parsed out of the container here, so there is nothing to report. What the
        /// session turned out to contain is established by inspecting the spooled file, the same
        /// way a recording is.
        /// </remarks>
        public TransportObservation Observation
            => new(false, null, 0);

        /// <inheritdoc />
        public int ConsumerCount { get; set; }

        /// <inheritdoc />
        public string OriginalStreamId { get; set; }

        /// <inheritdoc />
        public string TunerHostId { get; }

        /// <summary>
        /// Gets a value indicating whether this stream may be shared.
        /// </summary>
        /// <remarks>
        /// Never. Each session gets its own TVHeadend transcoder, started at the moment it is
        /// wanted; a second session joining this one would arrive in the middle of a container
        /// whose header it never saw, and closing either would take the stream away from both.
        /// </remarks>
        public bool EnableStreamSharing => false;

        /// <inheritdoc />
        public MediaSourceInfo MediaSource { get; set; }

        /// <inheritdoc />
        public string UniqueId { get; }

        /// <inheritdoc />
        public async Task Open(CancellationToken openCancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

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

            _spool = new SessionSpool(_spoolPath);
            _feedTask = Feed(upstream, [client, response], _lifetime.Token);

            try
            {
                await _ready.Task.WaitAsync(_startupTimeLimit, openCancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await _lifetime.CancelAsync().ConfigureAwait(false);
                throw new TimeoutException(FormattableString.Invariant(
                    $"TVHeadend accepted the {Role} subscription for channel {ChannelId}, but produced nothing within {_startupTimeLimit.TotalSeconds:0} seconds."));
            }
            catch
            {
                await _lifetime.CancelAsync().ConfigureAwait(false);
                throw;
            }

            _logger.LogInformation(
                "TVHeadend compatibility stream {UniqueId} ready after {ElapsedMilliseconds} ms (channel {ChannelId}, {Role}, {Container})",
                UniqueId,
                stopwatch.ElapsedMilliseconds,
                ChannelId,
                Role,
                Container);
        }

        /// <inheritdoc />
        public async Task Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            await DisposeAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Every caller gets its own reader over the whole session, starting at the container
        /// header. Jellyfin is free to ask more than once -- a client that reconnects produces
        /// exactly that -- and neither caller disturbs the other.
        /// </remarks>
        public Stream GetStream()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return _spool?.OpenReader()
                ?? throw new InvalidOperationException("The compatibility stream has not been opened.");
        }

        /// <inheritdoc />
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
            await _lifetime.CancelAsync().ConfigureAwait(false);

            if (_feedTask is not null)
            {
                try
                {
                    await _feedTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the session ends.
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "TVHeadend compatibility stream {UniqueId}: the feed ended with an error", UniqueId);
                }
            }

            if (_spool is not null)
            {
                await _spool.DisposeAsync().ConfigureAwait(false);
            }

            _lifetime.Dispose();

            _logger.LogInformation("TVHeadend compatibility stream {UniqueId} closed", UniqueId);
        }

        private async Task Feed(
            Stream upstream,
            IReadOnlyList<IDisposable> owned,
            CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);

            try
            {
                await using (upstream.ConfigureAwait(false))
                {
                    while (true)
                    {
                        var read = await upstream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        await _spool!.Append(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                        if (_spool.Length >= MinimumStartBytes)
                        {
                            _ready.TrySetResult();
                        }
                    }

                    _spool!.Complete();
                    if (!_ready.Task.IsCompleted)
                    {
                        _ready.TrySetException(new EndOfStreamException(
                            "TVHeadend ended the compatibility stream before sending enough data."));
                    }
                }
            }
            catch (OperationCanceledException exception)
            {
                _spool?.Complete();
                _ready.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                _spool?.Complete();
                _ready.TrySetException(exception);
                _logger.LogError(exception, "TVHeadend compatibility stream {UniqueId} stopped unexpectedly", UniqueId);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);

                foreach (var resource in owned)
                {
                    resource.Dispose();
                }
            }
        }
    }
}
