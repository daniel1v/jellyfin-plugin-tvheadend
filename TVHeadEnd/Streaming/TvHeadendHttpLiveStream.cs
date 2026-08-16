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
        private LiveTransportStreamConditioner? _conditioner;
        private Process? _reencodeProcess;
        private DateTime _dateOpenedUtc;
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
            await StartFeed(openCancellationToken).ConfigureAwait(false);
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
        /// <remarks>
        /// The encoder is fed through a pipe rather than pointed at the tuner itself, so that
        /// switching to it does not open a second subscription for a channel already being
        /// received -- which costs another round of connection setup and, on a system with few
        /// tuners, may not be available at all.
        /// </remarks>
        /// <param name="outputPath">The shared buffer file FFmpeg writes to.</param>
        /// <returns>The argument list, one argument per element.</returns>
        internal static IReadOnlyList<string> BuildReencodeArguments(string outputPath)
        {
            ArgumentException.ThrowIfNullOrEmpty(outputPath);

            List<string> arguments =
            [
                "-hide_banner",
                "-loglevel", "warning",
                "-fflags", "+genpts",

                // FFmpeg would otherwise spend up to its five second default deciding what a
                // transport stream contains. The PMT names every elementary stream within the
                // first packets, which is all the encoder needs.
                "-analyzeduration", "1000000",
                "-probesize", "4000000",

                "-f", "mpegts",
                "-i", "pipe:0",
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
            ];

            return arguments;
        }

        /// <summary>
        /// Receives the channel and fills the buffer from it, either directly or through the
        /// encoder, deciding which along the way unless the channel is already known.
        /// </summary>
        private async Task StartFeed(CancellationToken openCancellationToken)
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

            // A channel already measured to carry no IDR frames goes straight through the
            // encoder; the scan still runs alongside, so a broadcaster that starts sending
            // IDRs is noticed rather than re-encoded forever.
            var startInReencodeMode = _reencodeWhenNoIdr && _knownRequiresReencode == true;

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var feedMode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _feedTask = PumpConditionedStream(
                upstream,
                [client, response],
                ready,
                feedMode,
                startInReencodeMode,
                _lifetimeCancellationTokenSource.Token);

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

            try
            {
                if (requiresReencode)
                {
                    await WaitForReencodeOutput(openCancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ready.Task.WaitAsync(openCancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                await _lifetimeCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task PumpConditionedStream(
            Stream upstream,
            IReadOnlyList<IDisposable> owned,
            TaskCompletionSource ready,
            TaskCompletionSource<bool> feedMode,
            bool startInReencodeMode,
            CancellationToken cancellationToken)
        {
            byte[]? buffer = null;
            byte[]? conditionedBuffer = null;
            long bufferedBytes = 0;
            long firstByteTimestamp = 0;
            FileStream? bufferFile = null;
            Stream? sink = null;

            var reencoding = startInReencodeMode;
            bool? requiresReencode = startInReencodeMode ? true : _reencodeWhenNoIdr ? null : false;
            if (requiresReencode.HasValue)
            {
                feedMode.TrySetResult(requiresReencode.Value);
            }

            // The scan runs even when the mode is already settled, so that a channel which
            // starts or stops sending IDR frames is noticed rather than treated by a verdict
            // that has since gone stale.
            var observing = true;

            try
            {
                await using (upstream.ConfigureAwait(false))
                {
                    buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
                    conditionedBuffer = ArrayPool<byte>.Shared.Rent(
                        LiveTransportStreamConditioner.GetMaximumConditionedLength(buffer.Length));

                    var conditioner = new LiveTransportStreamConditioner(
                        LiveTransportStreamConditioner.EventInformationTablePid);
                    _conditioner = conditioner;

                    if (reencoding)
                    {
                        sink = StartReencodeProcess();
                    }
                    else
                    {
                        bufferFile = OpenBufferFile();
                        sink = bufferFile;
                    }

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

                        if (firstByteTimestamp == 0)
                        {
                            firstByteTimestamp = Stopwatch.GetTimestamp();
                        }

                        if (observing)
                        {
                            var observed = ObserveIdrPresence(conditioner, firstByteTimestamp);
                            if (observed.HasValue)
                            {
                                observing = false;
                                requiresReencode ??= observed.Value && _reencodeWhenNoIdr;
                                feedMode.TrySetResult(requiresReencode.Value);
                            }
                        }

                        if (requiresReencode == true && !reencoding)
                        {
                            // Hand the flow over to the encoder without re-opening the
                            // channel: the buffer file becomes FFmpeg's output, and the
                            // stream it has been receiving becomes FFmpeg's input.
                            reencoding = true;
                            await bufferFile!.DisposeAsync().ConfigureAwait(false);
                            bufferFile = null;
                            bufferedBytes = 0;
                            sink = StartReencodeProcess();

                            // FFmpeg joins mid-flight and has missed the tables that went out
                            // at the start of the conditioned stream.
                            var tableBytes = conditioner.WriteProgramTables(buffer);
                            if (tableBytes > 0)
                            {
                                await sink.WriteAsync(buffer.AsMemory(0, tableBytes), cancellationToken).ConfigureAwait(false);
                            }
                        }

                        await sink!.WriteAsync(conditionedBuffer.AsMemory(0, conditionedBytes), cancellationToken)
                            .ConfigureAwait(false);
                        bufferedBytes += conditionedBytes;
                        UpdateCachedLayoutMatch(conditioner);

                        if (!reencoding && !ready.Task.IsCompleted && IsBufferReady(bufferedBytes, firstByteTimestamp))
                        {
                            await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
                            _dateOpenedUtc = DateTime.UtcNow;
                            ready.TrySetResult();
                        }
                    }

                    if (!ready.Task.IsCompleted && !reencoding)
                    {
                        var endOfStream = new EndOfStreamException(
                            "TVHeadend closed the live stream before sending enough MPEG-TS data.");
                        feedMode.TrySetException(endOfStream);
                        ready.TrySetException(endOfStream);
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
                // Closing FFmpeg's input lets it flush and exit; its monitor then clears the
                // sharing flag once the encoder is really gone.
                if (bufferFile is not null)
                {
                    await bufferFile.DisposeAsync().ConfigureAwait(false);
                    EnableStreamSharing = false;
                }
                else if (sink is not null)
                {
                    try
                    {
                        await sink.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // FFmpeg may already have gone away.
                    }
                }
                else
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
        /// Establishes whether the broadcast carries IDR frames and reports it for the channel,
        /// so later tunes need not measure again.
        /// </summary>
        /// <returns>
        /// Whether the broadcast carries no IDR frame, or <see langword="null"/> while the scan
        /// has not seen enough of the stream to say.
        /// </returns>
        private bool? ObserveIdrPresence(LiveTransportStreamConditioner conditioner, long firstByteTimestamp)
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

            return carriesNoIdr;
        }

        private FileStream OpenBufferFile()
        {
            return new FileStream(
                _temporaryFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                IODefaults.FileStreamBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        /// <summary>
        /// Starts the encoder and returns the stream its input is written to.
        /// </summary>
        private Stream StartReencodeProcess()
        {
            _logger.LogInformation(
                "TVHeadend live stream {UniqueId}: the broadcast carries no IDR frame, re-encoding the video so clients can start decoding",
                UniqueId);

            // A detection phase may have written to this path already. Left in place, the wait
            // for output would be satisfied by those stale bytes and the probe would describe
            // the original stream instead of the re-encoded one.
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
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in BuildReencodeArguments(_temporaryFilePath))
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
            _ = MonitorReencodeFeed(process, _lifetimeCancellationTokenSource.Token);
            return process.StandardInput.BaseStream;
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
