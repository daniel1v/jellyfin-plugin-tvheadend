using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
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
        private readonly HTSConnectionHandler _connectionHandler;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly RecordingsChannel _recordings;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILogger<TvHeadendRecordingsController> _logger;

        public TvHeadendRecordingsController(
            HTSConnectionHandler connectionHandler,
            IHttpClientFactory httpClientFactory,
            RecordingsChannel recordings,
            IMediaEncoder mediaEncoder,
            ILogger<TvHeadendRecordingsController> logger)
        {
            _connectionHandler = connectionHandler;
            _httpClientFactory = httpClientFactory;
            _recordings = recordings;
            _mediaEncoder = mediaEncoder;
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

            _logger.LogInformation(
                "TVHeadend recording {RecordingId}: {Method} {Range}",
                recordingId,
                Request.Method,
                string.IsNullOrEmpty(Request.Headers.Range.ToString()) ? "whole" : Request.Headers.Range.ToString());

            var client = _httpClientFactory.CreateClient();

            if (!HttpMethods.IsHead(Request.Method) && _recordings.RequiresReencode(recordingId))
            {
                return await ServeReencoded(client, upstream, recordingId, cancellationToken).ConfigureAwait(false);
            }

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
        /// Serves the recording re-encoded, for a broadcast that carries no IDR frame.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Nothing cheaper works for these. Withholding direct play and the remux still leaves
        /// Jellyfin copying the video inside its transcoding job -- the stream matches the target
        /// codec, and that decision is made there -- so the frames a decoder needs are still
        /// absent. Only re-encoding creates them, which is exactly what the live path does for
        /// the same broadcasts, with the same arguments.
        /// </para>
        /// <para>
        /// FFmpeg is fed rather than pointed at the recording. Letting it open the source itself
        /// is what made it seek back after analysing, which TVHeadend answers by dropping the
        /// connection. Seeking is given up for these recordings: an encoder's output has no
        /// length to seek within, and the alternative is not playing them at all.
        /// </para>
        /// </remarks>
        private async Task<ActionResult> ServeReencoded(
            HttpClient client,
            string upstream,
            string recordingId,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, upstream);
            foreach (var header in _connectionHandler.GetHeaders())
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                return StatusCode(StatusCodes.Status502BadGateway);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _mediaEncoder.EncoderPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            foreach (var argument in TvHeadendHttpLiveStream.BuildReencodeArguments())
            {
                startInfo.ArgumentList.Add(argument);
            }

            var encoder = new Process { StartInfo = startInfo };
            if (!encoder.Start())
            {
                encoder.Dispose();
                response.Dispose();
                return StatusCode(StatusCodes.Status500InternalServerError);
            }

            _logger.LogInformation(
                "TVHeadend recording {RecordingId}: carries no IDR frame, serving it re-encoded",
                recordingId);

            _ = PumpIntoEncoder(response, encoder, recordingId, cancellationToken);
            _ = DrainEncoderErrors(encoder);

            // No length and no ranges: what comes out is produced as it is read.
            Response.Headers.AcceptRanges = "none";
            Response.StatusCode = StatusCodes.Status200OK;

            return new FileStreamResult(new EncodedStream(encoder, response), "video/mp2t")
            {
                EnableRangeProcessing = false,
            };
        }

        private async Task PumpIntoEncoder(
            HttpResponseMessage response,
            Process encoder,
            string recordingId,
            CancellationToken cancellationToken)
        {
            try
            {
                var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    await body.CopyToAsync(encoder.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away, or the encoder did. Its own disposal cleans up.
            }
            finally
            {
                try
                {
                    encoder.StandardInput.Close();
                }
                catch (IOException)
                {
                    // Already gone.
                }
            }
        }

        private async Task DrainEncoderErrors(Process encoder)
        {
            try
            {
                var tail = await encoder.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(tail))
                {
                    _logger.LogDebug("TVHeadend recording re-encode: {Message}", tail.Trim());
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// The encoder's output, keeping the encoder and its source alive while it is read.
        /// </summary>
        private sealed class EncodedStream(Process encoder, HttpResponseMessage response) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
                => encoder.StandardOutput.BaseStream.Read(buffer, offset, count);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => encoder.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => encoder.StandardOutput.BaseStream.ReadAsync(buffer, offset, count, cancellationToken);

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
                    try
                    {
                        if (!encoder.HasExited)
                        {
                            encoder.Kill(entireProcessTree: true);
                        }
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Already terminating.
                    }

                    encoder.Dispose();
                    response.Dispose();
                }

                base.Dispose(disposing);
            }
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
