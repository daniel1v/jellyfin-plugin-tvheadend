using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Streaming
{
    internal sealed class TvHeadendHttpLiveStream : ILiveStream, IDirectStreamProvider
    {
        private const int StreamBufferSize = 131072;
        private const int MinimumProbeBufferSize = 131072;
        private const int TransportStreamPacketSize = 188;
        private const int LiveEdgeCatchUpLength = 20000;
        private const int RetryDeleteCount = 10;
        private static readonly TimeSpan MinimumProbeBufferDuration = TimeSpan.FromSeconds(2);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerApplicationHost _applicationHost;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lifetimeCancellationTokenSource = new();
        private readonly string _upstreamUrl;
        private readonly IReadOnlyDictionary<string, string> _upstreamHeaders;
        private readonly string _temporaryFilePath;
        private Task? _pumpTask;
        private DateTime _dateOpenedUtc;
        private bool _disposed;
        private int _debugPacingBytesPerSecond;
        private bool _debugBypassConditioner;
        private string? _debugContainer;
        private LiveTransportStreamConditioner? _conditioner;

        public TvHeadendHttpLiveStream(
            MediaSourceInfo mediaSource,
            IHttpClientFactory httpClientFactory,
            IConfigurationManager configurationManager,
            IServerApplicationHost applicationHost,
            ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(mediaSource);
            ArgumentNullException.ThrowIfNull(httpClientFactory);
            ArgumentNullException.ThrowIfNull(configurationManager);
            ArgumentNullException.ThrowIfNull(applicationHost);
            ArgumentNullException.ThrowIfNull(logger);

            _upstreamUrl = mediaSource.Path
                ?? throw new ArgumentException("The opened media source must contain an upstream URL.", nameof(mediaSource));
            _upstreamHeaders = new Dictionary<string, string>(mediaSource.RequiredHttpHeaders, StringComparer.OrdinalIgnoreCase);
            _httpClientFactory = httpClientFactory;
            _applicationHost = applicationHost;
            _logger = logger;

            UniqueId = Guid.NewGuid().ToString("N");

            // Not the transcode directory. Jellyfin empties that whenever any transcoding job
            // or live stream ends, which would delete the buffer of every other stream that is
            // still running. The client then receives a source that answers 404 for the rest of
            // its session.
            _temporaryFilePath = Path.Combine(GetBufferDirectory(configurationManager), $"tvheadend-{UniqueId}.ts");
            MediaSource = mediaSource;
            ConsumerCount = 1;
            EnableStreamSharing = true;
            OriginalStreamId = string.Empty;
            TunerHostId = string.Empty;
        }

        /// <summary>
        /// Gets a value indicating whether the shared buffer this stream hands out still exists.
        /// </summary>
        public bool HasBuffer => File.Exists(_temporaryFilePath);

        /// <summary>
        /// Gets a value indicating whether the broadcast carries IDR frames, which a client
        /// needs before it can begin decoding a stream it did not receive from the start.
        /// </summary>
        public bool HasSeenIdrFrame => _conditioner?.HasSeenIdrFrame ?? true;

        public int ConsumerCount { get; set; }

        public string OriginalStreamId { get; set; }

        public string TunerHostId { get; }

        public bool EnableStreamSharing { get; private set; }

        public MediaSourceInfo MediaSource { get; set; }

        public string UniqueId { get; }

        public async Task Open(CancellationToken openCancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Directory.CreateDirectory(Path.GetDirectoryName(_temporaryFilePath)
                ?? throw new InvalidOperationException("The live TV buffer path has no parent directory."));

            Stream upstream;
            var owned = new List<IDisposable>();

            // DIAGNOSE (temporaer): eine lokale Datei statt TVHeadend durch den echten
            // Direct-Play-Pfad schicken, damit sich zwei Encodes kontrolliert vergleichen
            // lassen, die sich nur in einer Eigenschaft unterscheiden.
            var debugSource = TryGetDebugSourcePath();
            if (debugSource is not null)
            {
                _logger.LogWarning("TVHeadend DIAGNOSE: streaming local file {DebugSource} instead of the tuner", debugSource);
                var debugStream = new FileStream(
                    debugSource,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    IODefaults.FileStreamBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                owned.Add(debugStream);
                upstream = debugStream;

                // Eine .mp4-Testdatei unveraendert durchreichen: der Conditioner arbeitet auf
                // TS-Paketen und wuerde sie zerstoeren. Container passend melden.
                if (debugSource.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    _debugBypassConditioner = true;
                    _debugContainer = "mp4,fmp4";

                    // Etwas ueber Echtzeit (Clip: ~1,4 MB/s), damit die Datei wie ein
                    // echter Live-Stream kontinuierlich waechst. Der Server erzwingt fuer
                    // Live-TV IsInfiniteStream=true (LiveTvMediaSourceProvider.Normalize)
                    // und haelt die Antwort offen; ohne stetigen Nachschub laeuft der
                    // Client in sein Lesetimeout.
                    _debugPacingBytesPerSecond = 2 * 1024 * 1024;
                }
                else
                {
                    // Eine lokale Datei waere sofort eingelesen. Auf Live-Tempo drosseln,
                    // damit der Pump sich wie am Tuner verhaelt und der Vergleich
                    // aussagekraeftig ist.
                    _debugPacingBytesPerSecond = 1024 * 1024;
                }
            }
            else
            {
                var client = _httpClientFactory.CreateClient();
                var request = new HttpRequestMessage(HttpMethod.Get, _upstreamUrl);
                foreach (var header in _upstreamHeaders)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                HttpResponseMessage response;
                try
                {
                    using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                        openCancellationToken,
                        _lifetimeCancellationTokenSource.Token);
                    response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        linkedCancellationTokenSource.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }
                catch
                {
                    request.Dispose();
                    client.Dispose();
                    throw;
                }

                request.Dispose();
                owned.Add(client);
                owned.Add(response);
                upstream = await response.Content.ReadAsStreamAsync(_lifetimeCancellationTokenSource.Token).ConfigureAwait(false);
            }

            var streamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pumpTask = PumpToTemporaryFile(upstream, owned, streamStarted, _lifetimeCancellationTokenSource.Token);

            try
            {
                await streamStarted.Task.WaitAsync(openCancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _lifetimeCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                throw;
            }

            // Android's integrated player treats every HTTP direct-play source as HLS.
            // Expose the managed growing buffer as a local file source instead, so its
            // static /Videos/{id}/stream request can receive the MPEG-TS stream directly.
            MediaSource.Path = _temporaryFilePath;
            MediaSource.Protocol = MediaProtocol.File;
            MediaSource.EncoderPath = _applicationHost.GetApiUrlForLocalAccess().TrimEnd('/')
                + "/LiveTv/LiveStreamFiles/"
                + UniqueId
                + "/stream.ts";
            MediaSource.EncoderProtocol = MediaProtocol.Http;
            MediaSource.RequiredHttpHeaders = new Dictionary<string, string>();
            MediaSource.SupportsDirectPlay = true;

            if (_debugContainer is not null)
            {
                MediaSource.Container = _debugContainer;
            }

            _logger.LogInformation(
                "TVHeadend managed live stream {UniqueId} opened with a shared MPEG-TS buffer",
                UniqueId);
        }

        public async Task Close()
        {
            if (_disposed)
            {
                return;
            }

            EnableStreamSharing = false;
            await _lifetimeCancellationTokenSource.CancelAsync().ConfigureAwait(false);

            if (_pumpTask is not null)
            {
                try
                {
                    await _pumpTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the final consumer closes the live stream.
                }
            }

            await DeleteTemporaryFile().ConfigureAwait(false);
            _logger.LogInformation("TVHeadend managed live stream {UniqueId} closed", UniqueId);
        }

        public Stream GetStream()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var stream = new FileStream(
                _temporaryFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                IODefaults.FileStreamBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (!_debugBypassConditioner && (DateTime.UtcNow - _dateOpenedUtc).TotalSeconds > 10 && stream.Length > LiveEdgeCatchUpLength)
            {
                // Resume on a packet boundary. Handing FFmpeg a partial packet costs it the
                // resynchronisation and, with it, the PAT and PMT it needs to describe the
                // video stream completely.
                var liveEdgeOffset = stream.Length - LiveEdgeCatchUpLength;
                stream.Seek(liveEdgeOffset - (liveEdgeOffset % TransportStreamPacketSize), SeekOrigin.Begin);
            }

            return stream;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            EnableStreamSharing = false;
            _lifetimeCancellationTokenSource.Cancel();
            _lifetimeCancellationTokenSource.Dispose();
        }

        /// <summary>
        /// Gets the directory the shared live buffers are written to, beside Jellyfin's
        /// transcode directory rather than inside it.
        /// </summary>
        /// <param name="configurationManager">The Jellyfin configuration manager.</param>
        /// <returns>The buffer directory.</returns>
        internal static string GetBufferDirectory(IConfigurationManager configurationManager)
        {
            ArgumentNullException.ThrowIfNull(configurationManager);

            var transcodePath = configurationManager.GetTranscodePath();
            var parent = Path.GetDirectoryName(transcodePath);
            return parent is null
                ? transcodePath
                : Path.Combine(parent, "tvheadend-livebuffers");
        }

        internal static ILiveStream? AcquireReusable(
            IEnumerable<ILiveStream> currentLiveStreams,
            string streamId,
            string mediaSourceId)
        {
            ArgumentNullException.ThrowIfNull(currentLiveStreams);
            ArgumentException.ThrowIfNullOrEmpty(mediaSourceId);

            foreach (var liveStream in currentLiveStreams)
            {
                if (liveStream.EnableStreamSharing
                    && (string.Equals(liveStream.MediaSource.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrEmpty(streamId)
                            && string.Equals(liveStream.OriginalStreamId, streamId, StringComparison.OrdinalIgnoreCase))))
                {
                    liveStream.ConsumerCount++;
                    return liveStream;
                }
            }

            return null;
        }

        private async Task PumpToTemporaryFile(
            Stream upstream,
            List<IDisposable> owned,
            TaskCompletionSource streamStarted,
            CancellationToken cancellationToken)
        {
            byte[]? buffer = null;
            byte[]? conditionedBuffer = null;
            long bufferedBytes = 0;
            long firstByteTimestamp = 0;
            try
            {
                {
                    await using (upstream.ConfigureAwait(false))
                    {
                        var output = new FileStream(
                            _temporaryFilePath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.Read | FileShare.Delete,
                            IODefaults.FileStreamBufferSize,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using (output.ConfigureAwait(false))
                        {
                            buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
                            conditionedBuffer = ArrayPool<byte>.Shared.Rent(
                                LiveTransportStreamConditioner.GetMaximumConditionedLength(buffer.Length));
                            var conditioner = new LiveTransportStreamConditioner(
                                LiveTransportStreamConditioner.EventInformationTablePid);
                            _conditioner = conditioner;
                            while (true)
                            {
                                int bytesRead = await upstream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                                    .ConfigureAwait(false);
                                if (bytesRead == 0)
                                {
                                    break;
                                }

                                int conditionedBytes;
                                ReadOnlyMemory<byte> payload;
                                if (_debugBypassConditioner)
                                {
                                    conditionedBytes = bytesRead;
                                    payload = buffer.AsMemory(0, bytesRead);
                                }
                                else
                                {
                                    conditionedBytes = conditioner.Condition(buffer.AsSpan(0, bytesRead), conditionedBuffer);
                                    payload = conditionedBuffer.AsMemory(0, conditionedBytes);
                                }

                                if (conditionedBytes == 0)
                                {
                                    continue;
                                }

                                await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                                bufferedBytes += conditionedBytes;

                                if (firstByteTimestamp == 0)
                                {
                                    firstByteTimestamp = Stopwatch.GetTimestamp();
                                }
                                else if (_debugPacingBytesPerSecond > 0)
                                {
                                    var due = TimeSpan.FromSeconds((double)bufferedBytes / _debugPacingBytesPerSecond);
                                    var behind = due - Stopwatch.GetElapsedTime(firstByteTimestamp);
                                    if (behind > TimeSpan.Zero)
                                    {
                                        await Task.Delay(behind, cancellationToken).ConfigureAwait(false);
                                    }
                                }

                                if (!streamStarted.Task.IsCompleted
                                    && bufferedBytes >= MinimumProbeBufferSize
                                    && Stopwatch.GetElapsedTime(firstByteTimestamp) >= MinimumProbeBufferDuration)
                                {
                                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                                    _dateOpenedUtc = DateTime.UtcNow;
                                    streamStarted.TrySetResult();
                                }
                            }

                            if (!streamStarted.Task.IsCompleted)
                            {
                                if (_debugBypassConditioner)
                                {
                                    // Der statische Testfall: die Datei ist jetzt vollstaendig
                                    // geschrieben, kein "Live"-Stream, der noch waechst.
                                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                                    _dateOpenedUtc = DateTime.UtcNow;
                                    streamStarted.TrySetResult();
                                }
                                else
                                {
                                    streamStarted.TrySetException(new EndOfStreamException(
                                        "TVHeadend closed the live stream before sending MPEG-TS data."));
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException exception)
            {
                streamStarted.TrySetCanceled(exception.CancellationToken);
                throw;
            }
            catch (Exception exception)
            {
                streamStarted.TrySetException(exception);
                _logger.LogError(exception, "TVHeadend managed live stream {UniqueId} stopped unexpectedly", UniqueId);
                throw;
            }
            finally
            {
                // Bei einer statischen Debug-Quelle ist der Pump sofort fertig; das Sharing
                // muss aktiv bleiben, sonst liefert GetChannelStreamMediaSources die
                // Pending-Quelle aus und der Server proxied seine eigene Startseite als Video.
                if (!_debugBypassConditioner)
                {
                    EnableStreamSharing = false;
                }

                if (buffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
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

        /// <summary>
        /// DIAGNOSE (temporaer): liest den Pfad einer lokalen Testdatei aus einer Markerdatei
        /// neben dem Transcode-Verzeichnis. Fehlt sie, laeuft alles wie gewohnt ueber TVHeadend.
        /// </summary>
        private string? TryGetDebugSourcePath()
        {
            try
            {
                // Eine Ebene ueber dem Transcode-Verzeichnis: Jellyfin raeumt das
                // Transcode-Verzeichnis beim Schliessen eines Streams leer und wuerde die
                // Markerdatei mitnehmen.
                var cacheDirectory = Path.GetDirectoryName(Path.GetDirectoryName(_temporaryFilePath));
                if (cacheDirectory is null)
                {
                    return null;
                }

                var marker = Path.Combine(cacheDirectory, "tvheadend-debug-source.txt");
                if (!File.Exists(marker))
                {
                    return null;
                }

                var candidate = File.ReadAllText(marker).Trim();

                // Zeigt der Marker auf ein Verzeichnis, wird pro Kanal eine eigene Variante
                // ausgeliefert: <MediaSource.Id>.ts, sonst default.ts. So lassen sich mehrere
                // Varianten in einem Durchgang vergleichen, ohne zwischendurch umzuschalten.
                if (Directory.Exists(candidate))
                {
                    var perChannel = Path.Combine(candidate, MediaSource.Id + ".ts");
                    if (File.Exists(perChannel))
                    {
                        return perChannel;
                    }

                    var fallback = Path.Combine(candidate, "default.ts");
                    return File.Exists(fallback) ? fallback : null;
                }

                return File.Exists(candidate) ? candidate : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private async Task DeleteTemporaryFile()
        {
            for (int attempt = 0; attempt <= RetryDeleteCount; attempt++)
            {
                try
                {
                    File.Delete(_temporaryFilePath);
                    return;
                }
                catch (IOException) when (attempt < RetryDeleteCount)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }
        }
    }
}
