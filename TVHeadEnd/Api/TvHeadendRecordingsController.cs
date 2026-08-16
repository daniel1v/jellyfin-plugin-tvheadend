using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
            using var request = new HttpRequestMessage(
                HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get,
                upstream);

            foreach (var header in _connectionHandler.GetHeaders())
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // Whatever the client asked for is asked of TVHeadend, on a connection of its own.
            var range = Request.Headers.Range.ToString();
            if (!string.IsNullOrEmpty(range))
            {
                request.Headers.TryAddWithoutValidation("Range", range);
            }

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

            Response.StatusCode = (int)response.StatusCode;

            // Stated here because TVHeadend does not state it, although it honours ranges. Without
            // it a client has no reason to believe it may seek.
            Response.Headers.AcceptRanges = "bytes";

            if (response.Content.Headers.ContentLength is { } length)
            {
                Response.ContentLength = length;
            }

            if (response.Content.Headers.ContentRange is { } contentRange)
            {
                Response.Headers.ContentRange = contentRange.ToString();
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
