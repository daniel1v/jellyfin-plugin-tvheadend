using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// <summary>
    /// Feeds a TVHeadend channel into a shared buffer file that Jellyfin hands to clients as
    /// a local media source.
    /// </summary>
    /// <remarks>
    /// The buffer is filled one of two ways. Normally the transport stream is copied through
    /// unchanged apart from conditioning, which costs nothing and preserves the broadcast.
    /// Broadcasts that carry no IDR frame get their video re-encoded instead, because common
    /// device decoders never emit a frame from such a stream; which of the two applies is
    /// measured per channel rather than configured.
    /// </remarks>
    internal sealed class TvHeadendHttpLiveStream : ILiveStream, IDirectStreamProvider
    {
        private const int StreamBufferSize = 131072;

        /// <summary>
        /// Enough of the stream for a client to begin. The conditioner puts the tables and a
        /// keyframe at the very front, so this only has to cover the first few frames.
        /// </summary>
        private const int MinimumStartBufferSize = 65536;

        /// <summary>
        /// What FFprobe needs to describe the stream completely. Only relevant when the probe
        /// cannot be reused, because it is bought with <see cref="ProbeBufferDuration"/> of
        /// waiting on every channel change.
        /// </summary>
        private const int ProbeBufferSize = 131072;

        private const int TransportStreamPacketSize = 188;
        private const int LiveEdgeCatchUpLength = 20000;
        private const int RetryDeleteCount = 10;
        private const int ReencodeStderrTailLines = 12;

        private static readonly TimeSpan ProbeBufferDuration = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long to wait for an IDR frame before concluding the broadcast carries none.
        /// </summary>
        /// <remarks>
        /// Measured across twelve services: those that send IDRs send the first within 219 to
        /// 503 ms and repeat roughly every second; those that do not send none at all. Nothing
        /// falls in between, so the worst case is starting just after an IDR and waiting one
        /// interval, about 1.2 seconds. Erring long only delays the first tune of an affected
        /// channel; erring short costs an unnecessary re-encode, so this keeps a margin over
        /// the longest interval observed rather than hugging it.
        /// </remarks>
        private static readonly TimeSpan IdrDecisionTimeLimit = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan ReencodeStartupTimeout = TimeSpan.FromSeconds(30);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerApplicationHost _applicationHost;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lifetimeCancellationTokenSource = new();
        private readonly string _upstreamUrl;
        private readonly IReadOnlyDictionary<string, string> _upstreamHeaders;
        private readonly string _temporaryFilePath;
        private readonly string _ffmpegPath;
        private readonly bool _reencodeWhenNoIdr;
        private readonly bool? _knownRequiresReencode;
        private readonly Action<bool>? _reportRequiresReencode;
        private readonly string? _cachedProgramLayout;

        private Task? _feedTask;
        private CancellationTokenSource? _conditionedFeedCancellation;
        private LiveTransportStreamConditioner? _conditioner;
        private Process? _reencodeProcess;
        private DateTime _dateOpenedUtc;
        private bool _switchingToReencode;
        private bool _verdictReported;
        private bool _disposed;

        public TvHeadendHttpLiveStream(
            MediaSourceInfo mediaSource,
            IHttpClientFactory httpClientFactory,
            IConfigurationManager configurationManager,
            IServerApplicationHost applicationHost,
            ILogger logger,
            string ffmpegPath,
            bool reencodeWhenNoIdr,
            bool? knownRequiresReencode = null,
            Action<bool>? reportRequiresReencode = null,
            string? cachedProgramLayout = null)
        {
            ArgumentNullException.ThrowIfNull(mediaSource);
            ArgumentNullException.ThrowIfNull(httpClientFactory);
            ArgumentNullException.ThrowIfNull(configurationManager);
            ArgumentNullException.ThrowIfNull(applicationHost);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentException.ThrowIfNullOrEmpty(ffmpegPath);

            _upstreamUrl = mediaSource.Path
                ?? throw new ArgumentException("The opened media source must contain an upstream URL.", nameof(mediaSource));
            _upstreamHeaders = new Dictionary<string, string>(mediaSource.RequiredHttpHeaders, StringComparer.OrdinalIgnoreCase);
            _httpClientFactory = httpClientFactory;
            _applicationHost = applicationHost;
            _logger = logger;
            _ffmpegPath = ffmpegPath;
            _reencodeWhenNoIdr = reencodeWhenNoIdr;
            _knownRequiresReencode = knownRequiresReencode;
            _reportRequiresReencode = reportRequiresReencode;
            _cachedProgramLayout = cachedProgramLayout;

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

        /// <summary>
        /// Gets a value indicating whether the buffer is fed by an FFmpeg child process that
        /// re-encodes the video because the broadcast carries no IDR frames.
        /// </summary>
        public bool IsReencoding { get; private set; }

        /// <summary>
        /// Gets the elementary stream layout the PMT announced, to cache a probe result
        /// against, or <see langword="null"/> if no PMT was parsed.
        /// </summary>
        public string? ProgramLayout => _conditioner?.ProgramLayout;

        /// <summary>
        /// Gets a value indicating whether the broadcast still announces the elementary
        /// streams a previous tune probed, so that probe can be reused instead of spending a
        /// fresh analysis -- and the two seconds of buffering it needs -- on the channel change.
        /// </summary>
        public bool MatchesCachedLayout { get; private set; }

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

            var stopwatch = Stopwatch.StartNew();

            // A channel already measured to carry no IDR frames skips the detection phase, so
            // tuning it again costs only the encoder start-up.
            if (_reencodeWhenNoIdr && _knownRequiresReencode == true)
            {
                StartReencodeFeed();
                await WaitForReencodeOutput(openCancellationToken).ConfigureAwait(false);
            }
            else
            {
                await StartConditionedFeed(openCancellationToken).ConfigureAwait(false);
            }

            PublishBufferAsMediaSource();

            _logger.LogInformation(
                "TVHeadend live stream {UniqueId} ready after {ElapsedMilliseconds} ms ({Mode})",
                UniqueId,
                stopwatch.ElapsedMilliseconds,
                IsReencoding ? "video re-encoded, broadcast carries no IDR frames"
                    : MatchesCachedLayout ? "copy-through, probe reused"
                    : "copy-through, probe pending");
        }

        public async Task Close()
        {
            if (_disposed)
            {
                return;
            }

            EnableStreamSharing = false;
            await _lifetimeCancellationTokenSource.CancelAsync().ConfigureAwait(false);
            TryKillReencodeProcess();

            if (_feedTask is not null)
            {
                try
                {
                    await _feedTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the final consumer closes the live stream.
                }
            }

            await DeleteTemporaryFile().ConfigureAwait(false);
            _logger.LogInformation("TVHeadend live stream {UniqueId} closed", UniqueId);
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

            if ((DateTime.UtcNow - _dateOpenedUtc).TotalSeconds > 10 && stream.Length > LiveEdgeCatchUpLength)
            {
                // A consumer joining a stream that has been running for a while starts near
                // the live edge rather than replaying the backlog, on a packet boundary so the
                // reader does not have to resynchronise first.
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
            TryKillReencodeProcess();
            _lifetimeCancellationTokenSource.Dispose();
            _conditionedFeedCancellation?.Dispose();
            _reencodeProcess?.Dispose();
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

        /// <summary>
        /// Removes buffers left behind by a previous run. A server that stops while a stream
        /// is open never reaches <see cref="Close"/>, and each orphan keeps a recording's
        /// worth of disk space. Safe only before the first stream of a process is opened,
        /// when no buffer can belong to a live stream.
        /// </summary>
        /// <param name="configurationManager">The Jellyfin configuration manager.</param>
        /// <param name="logger">The logger.</param>
        internal static void RemoveOrphanedBuffers(IConfigurationManager configurationManager, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(configurationManager);
            ArgumentNullException.ThrowIfNull(logger);

            try
            {
                var directory = GetBufferDirectory(configurationManager);
                if (!Directory.Exists(directory))
                {
                    return;
                }

                long reclaimedBytes = 0;
                var removed = 0;
                foreach (var path in Directory.EnumerateFiles(directory, "tvheadend-*.ts"))
                {
                    try
                    {
                        var length = new FileInfo(path).Length;
                        File.Delete(path);
                        reclaimedBytes += length;
                        removed++;
                    }
                    catch (IOException)
                    {
                        // Still held by something; it will be swept on a later start.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Same.
                    }
                }

                if (removed > 0)
                {
                    logger.LogInformation(
                        "Removed {Count} live TV buffer(s) left behind by a previous run, reclaiming {ReclaimedMegabytes} MB",
                        removed,
                        reclaimedBytes / (1024 * 1024));
                }
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Could not sweep the live TV buffer directory");
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogWarning(exception, "Could not sweep the live TV buffer directory");
            }
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

        /// <summary>
        /// Builds the FFmpeg argument list that re-encodes the video of an IDR-less broadcast
        /// while copying every audio track. Subtitle and data streams are dropped; they do not
        /// survive an encode anyway, and the output keeps a deterministic stream order.
        /// </summary>
        /// <param name="upstreamUrl">The authenticated TVHeadend stream URL.</param>
        /// <param name="upstreamHeaders">HTTP headers the upstream requires, if any.</param>
        /// <param name="outputPath">The shared buffer file FFmpeg writes to.</param>
        /// <returns>The argument list, one argument per element.</returns>
        internal static IReadOnlyList<string> BuildReencodeArguments(
            string upstreamUrl,
            IReadOnlyDictionary<string, string> upstreamHeaders,
            string outputPath)
        {
            ArgumentException.ThrowIfNullOrEmpty(upstreamUrl);
            ArgumentNullException.ThrowIfNull(upstreamHeaders);
            ArgumentException.ThrowIfNullOrEmpty(outputPath);

            var arguments = new List<string>
            {
                "-hide_banner",
                "-loglevel", "warning",
                "-fflags", "+genpts",

                // FFmpeg would otherwise spend up to its five second default deciding what a
                // transport stream contains. The PMT names every elementary stream within the
                // first packets, which is all the encoder needs.
                "-analyzeduration", "1000000",
                "-probesize", "4000000",
            };

            if (upstreamHeaders.Count > 0)
            {
                arguments.Add("-headers");
                arguments.Add(string.Join("\r\n", upstreamHeaders.Select(header => $"{header.Key}: {header.Value}")) + "\r\n");
            }

            arguments.AddRange(
            [
                "-i", upstreamUrl,
                "-map", "0:v:0",
                "-map", "0:a?",
                "-dn", "-sn",
                "-c:a", "copy",
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-crf", "21",
                "-maxrate", "10M",
                "-bufsize", "14M",

                // Closed GOPs whose keyframes are IDR: exactly the property the source lacks
                // and device decoders refuse to start without.
                "-x264-params", "keyint=50:min-keyint=25:scenecut=0",

                // Passes progressive frames through untouched and deinterlaces the rest, so
                // interlaced services do not come out combed.
                "-vf", "yadif=deint=interlaced",
                "-f", "mpegts",
                "-y", outputPath,
            ]);

            return arguments;
        }

        /// <summary>
        /// Copies the broadcast into the buffer, deciding along the way whether it can be used
        /// as it is. If it cannot, the feed is torn down and handed to the encoder.
        /// </summary>
        private async Task StartConditionedFeed(CancellationToken openCancellationToken)
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
            var upstream = await response.Content.ReadAsStreamAsync(_lifetimeCancellationTokenSource.Token).ConfigureAwait(false);

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var feedMode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _conditionedFeedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationTokenSource.Token);
            _feedTask = PumpConditionedStream(
                upstream,
                [client, response],
                ready,
                feedMode,
                _conditionedFeedCancellation.Token);

            bool requiresReencode;
            try
            {
                requiresReencode = await feedMode.Task.WaitAsync(openCancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _lifetimeCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                throw;
            }

            if (!requiresReencode)
            {
                try
                {
                    await ready.Task.WaitAsync(openCancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await _lifetimeCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                    throw;
                }

                return;
            }

            await SwitchToReencodeFeed(openCancellationToken).ConfigureAwait(false);
        }

        private async Task PumpConditionedStream(
            Stream upstream,
            IReadOnlyList<IDisposable> owned,
            TaskCompletionSource ready,
            TaskCompletionSource<bool> feedMode,
            CancellationToken cancellationToken)
        {
            byte[]? buffer = null;
            byte[]? conditionedBuffer = null;
            long bufferedBytes = 0;
            long firstByteTimestamp = 0;
            bool? requiresReencode = _reencodeWhenNoIdr ? null : false;
            if (requiresReencode == false)
            {
                feedMode.TrySetResult(false);
            }

            try
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
                            var bytesRead = await upstream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                                .ConfigureAwait(false);
                            if (bytesRead == 0)
                            {
                                break;
                            }

                            var conditionedBytes = conditioner.Condition(buffer.AsSpan(0, bytesRead), conditionedBuffer);
                            if (conditionedBytes == 0)
                            {
                                continue;
                            }

                            await output.WriteAsync(conditionedBuffer.AsMemory(0, conditionedBytes), cancellationToken)
                                .ConfigureAwait(false);
                            bufferedBytes += conditionedBytes;
                            if (firstByteTimestamp == 0)
                            {
                                firstByteTimestamp = Stopwatch.GetTimestamp();
                            }

                            requiresReencode ??= DecideFeedMode(conditioner, feedMode, firstByteTimestamp);
                            UpdateCachedLayoutMatch(conditioner);

                            if (requiresReencode == false && !ready.Task.IsCompleted && IsBufferReady(bufferedBytes, firstByteTimestamp))
                            {
                                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                                _dateOpenedUtc = DateTime.UtcNow;
                                ready.TrySetResult();
                            }
                        }

                        if (!ready.Task.IsCompleted)
                        {
                            var endOfStream = new EndOfStreamException(
                                "TVHeadend closed the live stream before sending enough MPEG-TS data.");
                            feedMode.TrySetException(endOfStream);
                            ready.TrySetException(endOfStream);
                        }
                    }
                }
            }
            catch (OperationCanceledException exception)
            {
                feedMode.TrySetCanceled(exception.CancellationToken);
                ready.TrySetCanceled(exception.CancellationToken);
                throw;
            }
            catch (Exception exception)
            {
                feedMode.TrySetException(exception);
                ready.TrySetException(exception);
                _logger.LogError(exception, "TVHeadend live stream {UniqueId} stopped unexpectedly", UniqueId);
                throw;
            }
            finally
            {
                // When the feed is handed to the encoder the stream stays shareable; the
                // encoder monitor takes over responsibility for clearing the flag.
                if (!_switchingToReencode)
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
        /// A reusable probe spares both the analysis and the buffering it needs, so a channel
        /// whose layout is unchanged is ready as soon as a client has something to read.
        /// </summary>
        private bool IsBufferReady(long bufferedBytes, long firstByteTimestamp)
        {
            if (MatchesCachedLayout)
            {
                return bufferedBytes >= MinimumStartBufferSize;
            }

            return bufferedBytes >= ProbeBufferSize
                && Stopwatch.GetElapsedTime(firstByteTimestamp) >= ProbeBufferDuration;
        }

        private void UpdateCachedLayoutMatch(LiveTransportStreamConditioner conditioner)
        {
            if (MatchesCachedLayout || _cachedProgramLayout is null || conditioner.ProgramLayout is null)
            {
                return;
            }

            MatchesCachedLayout = string.Equals(conditioner.ProgramLayout, _cachedProgramLayout, StringComparison.Ordinal);
        }

        /// <summary>
        /// Decides whether the broadcast can be copied through, once the scan has seen enough
        /// of it, and records the verdict for the channel so later tunes skip the detection.
        /// </summary>
        /// <returns>
        /// Whether the feed has to be re-encoded, or <see langword="null"/> while the scan has
        /// not seen enough of the stream to decide.
        /// </returns>
        private bool? DecideFeedMode(
            LiveTransportStreamConditioner conditioner,
            TaskCompletionSource<bool> feedMode,
            long firstByteTimestamp)
        {
            bool carriesNoIdr;
            if (conditioner.HasSeenIdrFrame)
            {
                carriesNoIdr = false;
            }
            else if (conditioner.IdrScanBytes > 0
                && Stopwatch.GetElapsedTime(firstByteTimestamp) >= IdrDecisionTimeLimit)
            {
                // Deliberately on elapsed time rather than on bytes scanned: a byte budget
                // makes low bitrate channels wait longest, which is the wrong way round.
                carriesNoIdr = true;
            }
            else
            {
                return null;
            }

            if (!_verdictReported)
            {
                _verdictReported = true;
                _reportRequiresReencode?.Invoke(carriesNoIdr);
            }

            feedMode.TrySetResult(carriesNoIdr);
            return carriesNoIdr;
        }

        private async Task SwitchToReencodeFeed(CancellationToken openCancellationToken)
        {
            _logger.LogInformation(
                "TVHeadend live stream {UniqueId}: no IDR frame within the scan window; re-encoding the video so clients can start decoding",
                UniqueId);

            _switchingToReencode = true;
            if (_conditionedFeedCancellation is not null)
            {
                await _conditionedFeedCancellation.CancelAsync().ConfigureAwait(false);
            }

            if (_feedTask is not null)
            {
                try
                {
                    await _feedTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The detection feed was cancelled on purpose.
                }
                catch (IOException)
                {
                    // The upstream connection may abort while being torn down.
                }
            }

            StartReencodeFeed();
            await WaitForReencodeOutput(openCancellationToken).ConfigureAwait(false);
        }

        private void StartReencodeFeed()
        {
            // A detection feed may have written to this path already. Left in place, the wait
            // below would be satisfied by those stale bytes and the probe would describe the
            // original stream instead of the re-encoded one.
            try
            {
                File.Delete(_temporaryFilePath);
            }
            catch (IOException)
            {
                // A reader may still hold the file; FFmpeg truncates it either way.
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in BuildReencodeArguments(_upstreamUrl, _upstreamHeaders, _temporaryFilePath))
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("The FFmpeg re-encode process could not be started.");
            }

            _reencodeProcess = process;
            IsReencoding = true;
            _feedTask = MonitorReencodeFeed(process, _lifetimeCancellationTokenSource.Token);
        }

        private async Task MonitorReencodeFeed(Process process, CancellationToken cancellationToken)
        {
            var stderrTail = new Queue<string>(ReencodeStderrTailLines);
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                {
                    if (stderrTail.Count == ReencodeStderrTailLines)
                    {
                        stderrTail.Dequeue();
                    }

                    stderrTail.Enqueue(line);
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    _logger.LogError(
                        "TVHeadend live stream {UniqueId}: the re-encode ended with {ExitCode}: {StderrTail}",
                        UniqueId,
                        process.ExitCode,
                        string.Join(" | ", stderrTail));
                }
                else
                {
                    _logger.LogInformation(
                        "TVHeadend live stream {UniqueId}: the re-encode ended because the upstream closed",
                        UniqueId);
                }
            }
            finally
            {
                EnableStreamSharing = false;
            }
        }

        private async Task WaitForReencodeOutput(CancellationToken openCancellationToken)
        {
            var startedAt = Stopwatch.GetTimestamp();
            while (true)
            {
                openCancellationToken.ThrowIfCancellationRequested();
                _lifetimeCancellationTokenSource.Token.ThrowIfCancellationRequested();

                var fileInfo = new FileInfo(_temporaryFilePath);
                if (fileInfo.Exists && fileInfo.Length >= ProbeBufferSize)
                {
                    _dateOpenedUtc = DateTime.UtcNow;
                    return;
                }

                if (_reencodeProcess is { HasExited: true })
                {
                    throw new InvalidOperationException(
                        $"The FFmpeg re-encode process exited with {_reencodeProcess.ExitCode} before producing output.");
                }

                if (Stopwatch.GetElapsedTime(startedAt) >= ReencodeStartupTimeout)
                {
                    TryKillReencodeProcess();
                    throw new TimeoutException("The FFmpeg re-encode process produced no output within the start-up timeout.");
                }

                await Task.Delay(200, openCancellationToken).ConfigureAwait(false);
            }
        }

        private void TryKillReencodeProcess()
        {
            var process = _reencodeProcess;
            if (process is null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process ended between the check and the kill.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The process is not killable any more; it is already terminating.
            }
        }

        /// <summary>
        /// Points the media source at the shared buffer.
        /// </summary>
        /// <remarks>
        /// Android's integrated player treats every HTTP direct-play source as HLS, so the
        /// buffer is exposed as a local file instead. Its static /Videos/{id}/stream request
        /// then receives the MPEG-TS stream directly.
        /// </remarks>
        private void PublishBufferAsMediaSource()
        {
            MediaSource.Path = _temporaryFilePath;
            MediaSource.Protocol = MediaProtocol.File;
            MediaSource.EncoderPath = _applicationHost.GetApiUrlForLocalAccess().TrimEnd('/')
                + "/LiveTv/LiveStreamFiles/"
                + UniqueId
                + "/stream.ts";
            MediaSource.EncoderProtocol = MediaProtocol.Http;
            MediaSource.RequiredHttpHeaders = new Dictionary<string, string>();
            MediaSource.SupportsDirectPlay = true;
        }

        private async Task DeleteTemporaryFile()
        {
            for (var attempt = 0; attempt <= RetryDeleteCount; attempt++)
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
