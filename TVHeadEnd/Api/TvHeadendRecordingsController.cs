using System;
using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Streaming;

namespace TVHeadEnd.Api
{
    /// <summary>
    /// Serves TVHeadend recordings through Jellyfin rather than sending clients to TVHeadend.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TVHeadend answers a range request over its recording endpoint, but does not advertise
    /// <c>Accept-Ranges</c>, and it drops the connection when FFmpeg -- having read forward to
    /// analyse the stream -- seeks back to the start. FFmpeg reports "Unable to seek back to the
    /// start" and the input dies before a frame is produced, which is why recordings did not
    /// play. The option that would settle it, <c>-seekable 0</c>, does not appear anywhere in
    /// Jellyfin, so a plugin cannot ask for it.
    /// </para>
    /// <para>
    /// Delivering the recording here removes the question. Every range becomes a fresh request
    /// upstream, which TVHeadend answers reliably, and this endpoint states the
    /// <c>Accept-Ranges</c> that TVHeadend omits -- so seeking works rather than merely not
    /// failing. It also puts recordings where live TV already is: served by the plugin, not by
    /// the other server.
    /// </para>
    /// </remarks>
    [ApiController]
    [Route("TVHeadend")]
    public class TvHeadendRecordingsController : ControllerBase
    {
        /// <summary>
        /// How far into a recording to look for a point a decoder can start from. A broadcast
        /// offers one every couple of seconds, so this is generous.
        /// </summary>
        private const int StartScanLength = 8 * 1024 * 1024;

        private const int ScanChunkLength = 65536;

        private readonly HTSConnectionHandler _connectionHandler;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TvHeadendRecordingsController> _logger;

        public TvHeadendRecordingsController(
            HTSConnectionHandler connectionHandler,
            IHttpClientFactory httpClientFactory,
            ILogger<TvHeadendRecordingsController> logger)
        {
            _connectionHandler = connectionHandler;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Streams a recording.
        /// </summary>
        /// <param name="token">The unguessable name of the recording.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The recording.</returns>
        [HttpGet("Recordings/{token}/stream.ts")]
        [HttpHead("Recordings/{token}/stream.ts")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status206PartialContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetRecording(string token, CancellationToken cancellationToken)
        {
            if (!RecordingAccessToken.TryRead(token, Plugin.Instance.Configuration.RecordingAccessSecret, out var recordingId))
            {
                return NotFound();
            }

            var upstream = _connectionHandler.GetAuthenticatedUrl("dvrfile/" + recordingId);
            if (string.IsNullOrEmpty(upstream))
            {
                return NotFound();
            }

            using var client = _httpClientFactory.CreateClient();

            // Where a decoder can start, established afresh every time. A recording can be cut
            // after it was made, so a remembered offset would eventually point into the middle
            // of a picture; finding it again costs a short read.
            var start = await FindStartOffset(client, upstream, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "TVHeadend recording {RecordingId}: serving from offset {StartOffset} (requested {Range})",
                recordingId,
                start,
                string.IsNullOrEmpty(Request.Headers.Range.ToString()) ? "all" : Request.Headers.Range.ToString());

            using var request = new HttpRequestMessage(
                HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get,
                upstream);

            foreach (var header in _connectionHandler.GetHeaders())
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // The recording is presented as though it began at that point, so every offset the
            // client works with is shifted by it -- including the one it asks for.
            var requested = ParseRange(Request.Headers.Range.ToString());
            request.Headers.TryAddWithoutValidation(
                "Range",
                requested.To.HasValue
                    ? $"bytes={start + requested.From}-{start + requested.To.Value}"
                    : $"bytes={start + requested.From}-");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogError(exception, "TVHeadend recording {RecordingId} could not be fetched", recordingId);
                return StatusCode(StatusCodes.Status502BadGateway);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TVHeadend answered {StatusCode} for recording {RecordingId}",
                    (int)response.StatusCode,
                    recordingId);
                response.Dispose();
                return StatusCode(response.StatusCode == HttpStatusCode.NotFound
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status502BadGateway);
            }

            // Stated here because TVHeadend does not state it, although it honours ranges. Without
            // it a client has no reason to believe it may seek.
            Response.Headers.AcceptRanges = "bytes";

            var upstreamRange = response.Content.Headers.ContentRange;
            var total = upstreamRange?.Length ?? response.Content.Headers.ContentLength;
            var shiftedTotal = total.HasValue ? Math.Max(0, total.Value - start) : (long?)null;

            if (response.Content.Headers.ContentLength is { } length)
            {
                Response.ContentLength = length;
            }

            if (string.IsNullOrEmpty(Request.Headers.Range.ToString()))
            {
                // Asked for the whole thing, and gets the whole of what is playable.
                Response.StatusCode = StatusCodes.Status200OK;
            }
            else
            {
                Response.StatusCode = StatusCodes.Status206PartialContent;
                if (upstreamRange?.From is { } from && upstreamRange.To is { } to && shiftedTotal.HasValue)
                {
                    Response.Headers.ContentRange = $"bytes {from - start}-{to - start}/{shiftedTotal.Value}";
                }
            }

            if (HttpMethods.IsHead(Request.Method))
            {
                response.Dispose();
                return new EmptyResult();
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new FileStreamResult(new UpstreamStream(body, response), "video/mp2t")
            {
                EnableRangeProcessing = false,
            };
        }

        /// <summary>
        /// Reads the opening of the recording to find the first point a decoder can start from.
        /// </summary>
        /// <remarks>
        /// TVHeadend begins writing wherever the broadcast happened to be, so a recording starts
        /// in the middle of a group of pictures, before any parameter set. FFmpeg reports
        /// "non-existing SPS 0 referenced" and "no frame!", finds neither dimensions nor a frame
        /// rate, and Jellyfin then advertises RESOLUTION=0x0 and FRAME-RATE=90000 in the HLS
        /// manifest -- which is what a client configures its decoder from.
        /// </remarks>
        private async Task<long> FindStartOffset(HttpClient client, string upstream, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, upstream);
            foreach (var header in _connectionHandler.GetHeaders())
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            request.Headers.TryAddWithoutValidation("Range", $"bytes=0-{StartScanLength - 1}");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(ScanChunkLength);
            var conditioned = ArrayPool<byte>.Shared.Rent(
                LiveTransportStreamConditioner.GetMaximumConditionedLength(ScanChunkLength));
            try
            {
                var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    var read = await body.ReadAsync(buffer.AsMemory(0, ScanChunkLength), cancellationToken).ConfigureAwait(false);
                    if (read == 0 || !SourceContainer.IsTransportStream(buffer.AsSpan(0, read)))
                    {
                        // Not a transport stream; nothing here knows how to find a starting point
                        // in it, and it is served exactly as it arrives.
                        return 0;
                    }

                    var conditioner = new LiveTransportStreamConditioner(
                        LiveTransportStreamConditioner.EventInformationTablePid);

                    while (read > 0)
                    {
                        conditioner.Condition(buffer.AsSpan(0, read), conditioned);
                        if (conditioner.HasStarted)
                        {
                            return conditioner.StartOffset;
                        }

                        read = await body.ReadAsync(buffer.AsMemory(0, ScanChunkLength), cancellationToken).ConfigureAwait(false);
                    }
                }

                // No random access point within the scan. Serving from the beginning is no worse
                // than refusing, and the analysis will show what the recording is.
                return 0;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                ArrayPool<byte>.Shared.Return(conditioned);
            }
        }

        private static (long From, long? To) ParseRange(string? range)
        {
            if (string.IsNullOrEmpty(range) || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                return (0, null);
            }

            var span = range["bytes=".Length..].Split(',')[0];
            var parts = span.Split('-');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var from))
            {
                return (0, null);
            }

            return long.TryParse(parts[1], out var to) ? (from, to) : (from, null);
        }

        /// <summary>
        /// Keeps the upstream response alive for as long as its body is being read.
        /// </summary>
        private sealed class UpstreamStream(System.IO.Stream inner, HttpResponseMessage response)
            : System.IO.Stream
        {
            public override bool CanRead => inner.CanRead;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => inner.ReadAsync(buffer, cancellationToken);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => inner.ReadAsync(buffer, offset, count, cancellationToken);

            public override void Flush()
            {
            }

            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                    response.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
