using System;
using System.Net;
using System.Net.Http;
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
    /// Serves TVHeadend artwork through Jellyfin rather than sending Jellyfin to TVHeadend.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An image reaches Jellyfin as a URL, which Jellyfin then fetches with an HTTP client of its
    /// own. That client knows nothing of TVHeadend, so a server that requires authentication --
    /// the ordinary case -- answers it with 401 and the item has no picture. There is no way to
    /// hand Jellyfin a header along with the address. It is the same for a channel logo, an EPG
    /// programme image and a recording's poster, so one route answers all three.
    /// </para>
    /// <para>
    /// Putting the credentials in the URL, which an earlier version did, does not work and never
    /// did: <c>HttpClient</c> ignores the userinfo component of a URI and sends no
    /// <c>Authorization</c> header for it. All it achieved was writing the TVHeadend password into
    /// Jellyfin's database as an image path, and into the log on every failure.
    /// </para>
    /// <para>
    /// Fetching the logo here settles both. The address Jellyfin is given points at Jellyfin, so it
    /// needs no credentials, and the request to TVHeadend is made by this plugin with the header it
    /// already uses everywhere else.
    /// </para>
    /// </remarks>
    [ApiController]
    [Route("TVHeadend")]
    public class TvHeadendImagesController : ControllerBase
    {
        private readonly TvheadendConnection _connection;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TvHeadendImagesController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvHeadendImagesController"/> class.
        /// </summary>
        /// <param name="connection">The TVHeadend connection.</param>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger.</param>
        public TvHeadendImagesController(
            TvheadendConnection connection,
            IHttpClientFactory httpClientFactory,
            ILogger<TvHeadendImagesController> logger)
        {
            _connection = connection;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// The route artwork is served from, for the token that names it.
        /// </summary>
        /// <param name="token">The unguessable name of the image.</param>
        /// <returns>The path, relative to the server root.</returns>
        public static string ImagePathFor(string token)
        {
            ArgumentException.ThrowIfNullOrEmpty(token);

            return "/TVHeadend/Artwork/" + token;
        }

        /// <summary>
        /// The route artwork is served from when it has to be padded into a square first.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A separate address rather than a flag on the token, so the same picture can be
        /// published both ways: a broadcaster's own artwork is served as it is, and a logo
        /// standing in for artwork is padded. The token still names only a path.
        /// </para>
        /// <para>
        /// The path still ends in "poster", from when padding meant a 2:3 frame rather than a
        /// square. Changing it would change every published address, and a recording keeps the
        /// first picture it is given -- so the rename would cost everybody a reset to buy a better
        /// word.
        /// </para>
        /// </remarks>
        /// <param name="token">The unguessable name of the image.</param>
        /// <returns>The path, relative to the server root.</returns>
        public static string PaddedPathFor(string token)
        {
            ArgumentException.ThrowIfNullOrEmpty(token);

            return "/TVHeadend/Artwork/" + token + "/poster";
        }

        /// <summary>
        /// Serves a piece of artwork.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Anonymous because Jellyfin's image pipeline fetches it without a session, the same way
        /// it fetches any other remote image. What protects it is the address: the token carries a
        /// tag derived from a secret only this server knows, so a caller cannot mint one.
        /// </para>
        /// <para>
        /// The token names a path below the TVHeadend web root and nothing else. It is resolved
        /// against the configured endpoint and nowhere else, so the address this fetches from --
        /// and therefore the only address the credentials can reach -- is fixed by configuration
        /// rather than by anything in the request.
        /// </para>
        /// </remarks>
        /// <param name="token">The unguessable name of the image.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The image.</returns>
        [HttpGet("Artwork/{token}")]
        [HttpGet("Artwork/{token}/poster")]
        [AllowAnonymous]

        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetArtwork(string token, CancellationToken cancellationToken)
        {
            if (!TvheadendAccessToken.TryRead(token, Plugin.Instance.Configuration.RecordingAccessSecret, out var encoded)
                || !TvheadendArtwork.TryDecode(encoded, out var path))
            {
                return NotFound();
            }

            var endpoint = await _connection.GetHttpEndpointAsync(cancellationToken).ConfigureAwait(false);

            // Checked again on the way out, not only on the way in. The token cannot be forged,
            // but this is the one line that decides where the credentials go, and it should not
            // depend on a caller elsewhere having got it right.
            if (TvheadendArtwork.PathOnTvheadend(path, endpoint.BaseUrl) is not { } safe)
            {
                _logger.LogWarning("TVHeadend artwork: refused a path that is not on the server -- {Path}", path);
                return NotFound();
            }

            var upstream = endpoint.CreateApiUrl(safe);

            using var request = new HttpRequestMessage(HttpMethod.Get, upstream);
            foreach (var header in endpoint.CreateHeaders())
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            var client = _httpClientFactory.CreateClient();

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogError(exception, "TVHeadend artwork {Path} could not be fetched", safe);
                return StatusCode(StatusCodes.Status502BadGateway);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TVHeadend answered {StatusCode} for the artwork {Path}",
                    (int)response.StatusCode,
                    safe);

                response.Dispose();
                return StatusCode(response.StatusCode == HttpStatusCode.NotFound
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status502BadGateway);
            }

            // Read whole rather than streamed. Artwork is a few kilobytes, and Jellyfin's
            // image pipeline wants a complete body it can hash and store.
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.ToString();
            response.Dispose();

            if (Request.Path.Value?.EndsWith("/poster", StringComparison.OrdinalIgnoreCase) == true
                && PadSafely(body, safe) is { } padded)
            {
                return File(padded, "image/png");
            }

            return File(body, string.IsNullOrEmpty(contentType) ? "image/png" : contentType);
        }

        /// <summary>
        /// Pads a picture to poster shape, and gives up rather than failing if it cannot.
        /// </summary>
        /// <remarks>
        /// The padding is drawn with the SkiaSharp the server already loads, a dependency this
        /// plugin compiles against but does not ship. If a future server carries a version this
        /// cannot bind to, the failure lands here and nowhere else: the original picture is served
        /// instead, which is what happened before padding existed. A cosmetic improvement must not
        /// be able to take the artwork down with it.
        /// </remarks>
        /// <param name="body">The picture as TVHeadend sent it.</param>
        /// <param name="path">The path it came from, for the log.</param>
        /// <returns>The padded picture, or <see langword="null"/> to serve the original.</returns>
        private byte[]? PadSafely(byte[] body, string path)
        {
            try
            {
                return SquareCanvas.Pad(body);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "TVHeadend artwork {Path} could not be padded; serving it unchanged", path);
                return null;
            }
        }
    }
}
