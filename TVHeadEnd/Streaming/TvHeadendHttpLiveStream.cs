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
            _temporaryFilePath = Path.Combine(configurationManager.GetTranscodePath(), $"tvheadend-{UniqueId}.ts");
            MediaSource = mediaSource;
            ConsumerCount = 1;
            EnableStreamSharing = true;
            OriginalStreamId = string.Empty;
            TunerHostId = string.Empty;
        }

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

            var streamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pumpTask = PumpToTemporaryFile(client, response, streamStarted, _lifetimeCancellationTokenSource.Token);

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

            if ((DateTime.UtcNow - _dateOpenedUtc).TotalSeconds > 10 && stream.Length > LiveEdgeCatchUpLength)
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
            HttpClient client,
            HttpResponseMessage response,
            TaskCompletionSource streamStarted,
            CancellationToken cancellationToken)
        {
            byte[]? buffer = null;
            byte[]? conditionedBuffer = null;
            long bufferedBytes = 0;
            long firstByteTimestamp = 0;
            try
            {
                using (client)
                using (response)
                {
                    var upstream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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
                            while (true)
                            {
                                int bytesRead = await upstream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
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

                                await output.WriteAsync(conditionedBuffer.AsMemory(0, conditionedBytes), cancellationToken).ConfigureAwait(false);
                                bufferedBytes += conditionedBytes;
                                if (firstByteTimestamp == 0)
                                {
                                    firstByteTimestamp = Stopwatch.GetTimestamp();
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
                                streamStarted.TrySetException(new EndOfStreamException(
                                    "TVHeadend closed the live stream before sending MPEG-TS data."));
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
                EnableStreamSharing = false;
                if (buffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (conditionedBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(conditionedBuffer);
                }
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
