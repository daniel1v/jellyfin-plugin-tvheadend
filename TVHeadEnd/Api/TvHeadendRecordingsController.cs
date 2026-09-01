using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Tvheadend;

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
        private readonly ITvheadendHttpEndpointSource _endpoints;
        private readonly TvheadendAccessSecret _secret;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TvHeadendRecordingsController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvHeadendRecordingsController"/> class.
        /// </summary>
        /// <param name="endpoints">Where TVHeadend's HTTP interface is.</param>
        /// <param name="secret">The secret a published address is signed with.</param>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger.</param>
        public TvHeadendRecordingsController(
            ITvheadendHttpEndpointSource endpoints,
            TvheadendAccessSecret secret,
            IHttpClientFactory httpClientFactory,
            ILogger<TvHeadendRecordingsController> logger)
        {
            _endpoints = endpoints;
            _secret = secret;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// The route a recording is served from, for the token that names it.
        /// </summary>
        /// <param name="token">The unguessable name of the recording.</param>
        /// <returns>The path, relative to the server root.</returns>
        public static string StreamPathFor(string token)
        {
            ArgumentException.ThrowIfNullOrEmpty(token);

            return "/TVHeadend/Recordings/" + token + "/stream";
        }

        /// <summary>
        /// Streams a recording.
        /// </summary>
        /// <remarks>
        /// Two routes, one method. The neutral one is what recordings are published under: what a
        /// recording actually contains is TVHeadend's DVR profile to decide, and a <c>.ts</c> on
        /// the end asserted MPEG-TS of every recording including the Matroska a WebTV profile
        /// writes. The old spelling stays reachable because it is already stored in media sources
        /// somebody has, and it answers identically -- the same method, not a second one that
        /// could drift from it.
        /// </remarks>
        /// <param name="token">The unguessable name of the recording.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The recording.</returns>
        [HttpGet("Recordings/{token}/stream")]
        [HttpHead("Recordings/{token}/stream")]
        [HttpGet("Recordings/{token}/stream.ts")]
        [HttpHead("Recordings/{token}/stream.ts")]

        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status206PartialContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetRecording(string token, CancellationToken cancellationToken)
        {
            if (!TvheadendAccessToken.TryRead(token, _secret.Ensure(), out var recordingId))
            {
                return NotFound();
            }

            // Taken once, and only after a connection exists. The server's web root is part of
            // every address here and is only known from a handshake, so the synchronous property
            // can answer with a root the server never reported -- and asking twice within one
            // request could answer from two different servers if the configuration changed in
            // between.
            var endpoint = await _endpoints.GetHttpEndpointAsync(cancellationToken).ConfigureAwait(false);

            var upstream = endpoint.CreateApiUrl("dvrfile/" + recordingId);
            if (string.IsNullOrEmpty(upstream))
            {
                return NotFound();
            }

            _logger.LogInformation(
                "TVHeadend recording {RecordingId}: {Method} {Range}",
                recordingId,
                Request.Method,
                string.IsNullOrEmpty(Request.Headers.Range.ToString()) ? "whole" : Request.Headers.Range.ToString());

            // One route for HEAD and GET. They used to diverge -- HEAD proxied TVHeadend, which
            // advertises a seekable file, while GET could answer with a re-encode that has no
            // length and no ranges -- so a client that asked first and fetched second was told
            // one thing and given another.

            using var request = new HttpRequestMessage(
                HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get,
                upstream);

            foreach (var header in endpoint.CreateHeaders())
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            var client = _httpClientFactory.CreateClient();

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

            // Settled once, and stated by both verbs. A HEAD that omitted it described a
            // different representation than the GET beside it -- the same divergence the single
            // route exists to prevent.
            var contentType = DescribeContent(response);

            if (HttpMethods.IsHead(Request.Method))
            {
                Response.ContentType = contentType;
                response.Dispose();
                return new EmptyResult();
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new FileStreamResult(new UpstreamStream(body, response), contentType)
            {
                EnableRangeProcessing = false,
            };
        }

        /// <summary>
        /// What to call the bytes being passed on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// TVHeadend knows what it stored, so its answer is preferred over anything worked out
        /// here. Where it says nothing the honest answer is that these are bytes: the recording
        /// was <c>video/mp2t</c> unconditionally before, which is a claim about a container the
        /// DVR profile decides and this endpoint never inspects.
        /// </para>
        /// <para>
        /// Nothing downstream is worse off for the generic answer. Jellyfin's
        /// <c>GetStaticRemoteStreamResult</c> passes whatever arrives straight to the client and
        /// already falls back to the same value, and every decision about the container is made
        /// from the media source, which carries the analysed one.
        /// </para>
        /// </remarks>
        /// <param name="response">The answer TVHeadend gave.</param>
        /// <returns>The media type to state to the client.</returns>
        internal static string DescribeContent(HttpResponseMessage response)
        {
            var stated = response.Content.Headers.ContentType?.MediaType;

            // A server answering a byte range with a document is describing an error page, not a
            // recording, and repeating that to the client would only spread the confusion.
            if (string.IsNullOrEmpty(stated)
                || stated.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                return MediaTypeNames.Application.Octet;
            }

            return response.Content.Headers.ContentType!.ToString();
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

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

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
