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

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Receives one TVHeadend stream and makes it readable by Jellyfin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One stream, one TVHeadend profile, no re-coding of any kind. Which profile is read, and
    /// therefore whether this is the broadcast or a rendering TVHeadend produced from it, is
    /// decided before this is constructed; nothing here knows what a variant means or what a
    /// client can decode.
    /// </para>
    /// <para>
    /// The buffer is a fixed-size ring, so a channel left running costs the same disk space
    /// after eight hours as after one minute, and every reader joins it at a point a decoder can
    /// start from.
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
        /// How much has to be in hand before the container can be established. Four packet
        /// boundaries carrying a sync byte do not happen by chance; fewer prove nothing.
        /// </summary>
        private const int ContainerDetectionBytes = 4 * 188;

        /// <summary>
        /// Enough of the stream for a client to begin. The conditioner puts the tables and an
        /// access point at the very front, so this only has to cover the first few frames.
        /// </summary>
        private const int MinimumStartBufferSize = 65536;

        /// <summary>
        /// What FFprobe needs to describe the stream completely. Only paid when no current
        /// description is stored.
        /// </summary>
        private const int AnalysisBufferSize = 131072;

        private static readonly TimeSpan AnalysisBufferDuration = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long to wait for the random access verdict before starting anyway. It resolves as
        /// soon as an IDR is seen -- measured at 219 to 503 ms on services that send them -- so
        /// this is a bound, not a cost.
        /// </summary>
        private static readonly TimeSpan AssessmentTimeLimit = TimeSpan.FromSeconds(3);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly string _upstreamUrl;
        private readonly IReadOnlyDictionary<string, string> _upstreamHeaders;
        private readonly bool _describedAlready;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly StreamBootstrapIndex _transportBootstrap = new();
        private readonly string _bufferPath;
        private readonly int _bufferSizeMegabytes;
        private readonly string? _legacyNormalizerFfmpegPath;

        private Legacy.LegacyH264LiveNormalizer? _legacyNormalizer;
        private TransportStreamConditioner? _conditioner;
        private VideoRandomAccessProbe? _probe;
        private Task? _feedTask;
        private bool? _isTransportStream;
        private long _firstByteTimestamp;
        private bool _closed;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvheadendLiveStream"/> class.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="variantRole">Which delivery role this serves, for logging and reuse.</param>
        /// <param name="upstreamUrl">The TVHeadend stream URL, including its profile.</param>
        /// <param name="upstreamHeaders">The headers the request needs.</param>
        /// <param name="mediaSource">The media source this stream backs.</param>
        /// <param name="bufferPath">Where to write the buffer, without an extension.</param>
        /// <param name="bufferSizeMegabytes">The configured buffer window.</param>
        /// <param name="describedAlready">
        /// Whether a current description is already stored, which spares the analysis and the
        /// buffering it needs.
        /// </param>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="legacyNormalizerFfmpegPath">
        /// Set only while no TVHeadend profile fills the IDR normalization role, in which case
        /// the broadcast is run through the plugin's own transitional encoder. Everything below
        /// this point then sees an ordinary transport stream and does not know the difference.
        /// </param>
        public TvheadendLiveStream(
            string channelId,
            string variantRole,
            string upstreamUrl,
            IReadOnlyDictionary<string, string> upstreamHeaders,
            MediaSourceInfo mediaSource,
            string bufferPath,
            int bufferSizeMegabytes,
            bool describedAlready,
            IHttpClientFactory httpClientFactory,
            ILogger logger,
            string? legacyNormalizerFfmpegPath = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);
            ArgumentException.ThrowIfNullOrEmpty(variantRole);
            ArgumentException.ThrowIfNullOrEmpty(upstreamUrl);
            ArgumentNullException.ThrowIfNull(upstreamHeaders);
            ArgumentNullException.ThrowIfNull(mediaSource);
            ArgumentException.ThrowIfNullOrEmpty(bufferPath);
            ArgumentNullException.ThrowIfNull(httpClientFactory);
            ArgumentNullException.ThrowIfNull(logger);

            ChannelId = channelId;
            VariantRole = variantRole;
            _upstreamUrl = upstreamUrl;
            _upstreamHeaders = upstreamHeaders;
            _httpClientFactory = httpClientFactory;
            _describedAlready = describedAlready;
            _logger = logger;
            _legacyNormalizerFfmpegPath = legacyNormalizerFfmpegPath;

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
        /// Gets which delivery role this serves. Part of what makes a stream reusable, so that a
        /// broadcast and a rendering of it can never be mistaken for one another.
        /// </summary>
        public string VariantRole { get; }

        /// <summary>
        /// Gets the buffer this stream fills.
        /// </summary>
        public LiveStreamBuffer Buffer { get; private set; } = null!;

        /// <summary>
        /// Gets a value indicating whether the buffer still exists.
        /// </summary>
        public bool HasBuffer => Buffer?.Exists == true;

        /// <summary>
        /// Gets what the transport layer observed while receiving the stream.
        /// </summary>
        public Media.TransportObservation Observation
            => Media.TransportObservation.From(_conditioner, _probe, _isTransportStream == true);

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

        /// <inheritdoc />
        public async Task Open(CancellationToken openCancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_bufferPath)
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

            if (!string.IsNullOrEmpty(_legacyNormalizerFfmpegPath))
            {
                _legacyNormalizer = Legacy.LegacyH264LiveNormalizer.Start(
                    upstream,
                    _legacyNormalizerFfmpegPath,
                    _logger,
                    $"channel {ChannelId} {VariantRole}",
                    _lifetime.Token);
                upstream = _legacyNormalizer.Output;
            }

            _feedTask = Feed(upstream, [client, response], _lifetime.Token);

            try
            {
                await _ready.Task.WaitAsync(openCancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _lifetime.CancelAsync().ConfigureAwait(false);
                throw;
            }

            _logger.LogInformation(
                "TVHeadend live stream {UniqueId} ready after {ElapsedMilliseconds} ms (channel {ChannelId}, {Role})",
                UniqueId,
                stopwatch.ElapsedMilliseconds,
                ChannelId,
                VariantRole);
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
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "TVHeadend live stream {UniqueId}: the feed ended with an error", UniqueId);
                }
            }

            if (_legacyNormalizer is not null)
            {
                await _legacyNormalizer.DisposeAsync().ConfigureAwait(false);
            }

            if (Buffer is not null)
            {
                await Buffer.DisposeAsync().ConfigureAwait(false);
            }

            _lifetime.Dispose();

            _logger.LogInformation("TVHeadend live stream {UniqueId} closed", UniqueId);
        }

        private async Task Feed(
            Stream upstream,
            IReadOnlyList<IDisposable> owned,
            CancellationToken cancellationToken)
        {
            byte[]? readBuffer = null;
            byte[]? conditionedBuffer = null;
            byte[]? carry = null;
            var pending = 0;

            try
            {
                await using (upstream.ConfigureAwait(false))
                {
                    readBuffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);

                    // Holds what arrives while the container is still being established, which
                    // is at most one detection window plus the read that completed it.
                    carry = ArrayPool<byte>.Shared.Rent(StreamBufferSize + ContainerDetectionBytes);
                    conditionedBuffer = ArrayPool<byte>.Shared.Rent(
                        TransportStreamConditioner.GetMaximumConditionedLength(carry.Length));

                    var probe = new VideoRandomAccessProbe();
                    var conditioner = new TransportStreamConditioner(
                        TransportStreamConditioner.EventInformationTablePid,
                        probe);
                    _probe = probe;
                    _conditioner = conditioner;

                    while (true)
                    {
                        var read = await upstream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        ReadOnlyMemory<byte> chunk;
                        if (_isTransportStream is null)
                        {
                            // The proof that this is a transport stream is a run of sync bytes
                            // exactly a packet apart, so there has to be more than one packet to
                            // look at. A first read shorter than that proves nothing, and
                            // deciding on it anyway is what left a quarter of the channels
                            // recorded with no PMT stream type at all.
                            readBuffer.AsSpan(0, read).CopyTo(carry.AsSpan(pending));
                            pending += read;
                            if (pending < ContainerDetectionBytes)
                            {
                                continue;
                            }

                            // Neither the live stream nor a recording is guaranteed to be
                            // MPEG-TS: both follow profiles configured on the TVHeadend server,
                            // and a server set to one of the WebTV profiles serves Matroska.
                            _isTransportStream = SourceContainer.IsTransportStream(carry.AsSpan(0, pending));

                            // The buffer indexes entry points in the vocabulary of whatever
                            // arrived. A transport stream is conditioned and its access points
                            // come from the conditioner; anything else is passed through and
                            // finds its own.
                            // The buffer is named after what actually arrived. Jellyfin serves it
                            // to a direct-playing client straight from disk and takes the content
                            // type from the extension, so a Matroska stream in a file called .ts
                            // is announced as video/mp2t -- and a player that believes it finds
                            // no sync byte and never renders a frame.
                            Buffer = new LiveStreamBuffer(
                                _bufferPath + (_isTransportStream == true ? ".ts" : ".mkv"),
                                _bufferSizeMegabytes);

                            if (_isTransportStream == true)
                            {
                                Buffer.Bootstrap = _transportBootstrap;
                            }
                            else
                            {
                                Buffer.Bootstrap = new MatroskaBootstrapIndex();
                                _logger.LogInformation(
                                    "TVHeadend live stream {UniqueId}: channel {ChannelId} is not an MPEG-TS stream, so it is passed through unconditioned",
                                    UniqueId,
                                    ChannelId);
                            }

                            // Nothing accumulated while deciding is thrown away: the conditioner
                            // has to see the stream from its first byte.
                            chunk = carry.AsMemory(0, pending);
                        }
                        else
                        {
                            chunk = readBuffer.AsMemory(0, read);
                        }

                        if (_firstByteTimestamp == 0)
                        {
                            _firstByteTimestamp = Stopwatch.GetTimestamp();
                        }

                        ReadOnlyMemory<byte> payload;
                        IReadOnlyList<int>? accessPoints = null;
                        if (_isTransportStream == true)
                        {
                            var conditioned = conditioner.Condition(chunk.Span, conditionedBuffer);
                            if (conditioned == 0)
                            {
                                continue;
                            }

                            payload = conditionedBuffer.AsMemory(0, conditioned);
                            accessPoints = conditioner.RandomAccessOffsets;
                            conditioner.PublishProgramTables(_transportBootstrap);
                            probe.Evaluate();
                        }
                        else
                        {
                            payload = chunk;
                        }

                        await Buffer.Write(payload, accessPoints, cancellationToken).ConfigureAwait(false);
                        SignalReadyIfPossible(probe);
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
            catch (Exception exception)
            {
                _ready.TrySetException(exception);
                _logger.LogError(exception, "TVHeadend live stream {UniqueId} stopped unexpectedly", UniqueId);
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

                if (carry is not null)
                {
                    ArrayPool<byte>.Shared.Return(carry);
                }

                foreach (var resource in owned)
                {
                    resource.Dispose();
                }
            }
        }

        private void SignalReadyIfPossible(VideoRandomAccessProbe probe)
        {
            if (_ready.Task.IsCompleted)
            {
                return;
            }

            var buffered = Buffer.WritePosition;
            var elapsed = Stopwatch.GetElapsedTime(_firstByteTimestamp);

            // The verdict on how the video offers random access has to be in before anything is
            // published, because it decides which variant a client is given -- and it is taken
            // from this tune, never from what was stored, so that a broadcaster changing its GOP
            // structure is noticed rather than served from a stale conclusion.
            if (_isTransportStream == true
                && probe.Kind == H264RandomAccessKind.Unknown
                && elapsed < AssessmentTimeLimit)
            {
                return;
            }

            var enough = _describedAlready
                ? buffered >= MinimumStartBufferSize
                : buffered >= AnalysisBufferSize && elapsed >= AnalysisBufferDuration;

            if (enough)
            {
                _ready.TrySetResult();
            }
        }
    }
}
