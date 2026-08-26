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
    /// Serves TVHeadend channel logos through Jellyfin rather than sending Jellyfin to TVHeadend.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A channel's image reaches Jellyfin as a URL, which Jellyfin then fetches with an HTTP client
    /// of its own. That client knows nothing of TVHeadend, so a server that requires
    /// authentication -- the ordinary case -- answers it with 401 and the channel has no logo.
    /// There is no way to hand Jellyfin a header along with the address.
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
        /// The route a channel's logo is served from, for the token that names it.
        /// </summary>
        /// <param name="token">The unguessable name of the channel.</param>
        /// <returns>The path, relative to the server root.</returns>
        public static string ImagePathFor(string token)
        {
            ArgumentException.ThrowIfNullOrEmpty(token);

            return "/TVHeadend/Channels/" + token + "/image";
        }

        /// <summary>
        /// Serves a channel's logo.
        /// </summary>
        /// <remarks>
        /// Anonymous because Jellyfin's image pipeline fetches it without a session, the same way
        /// it fetches any other remote image. What protects it is the address: the token carries a
        /// tag derived from a secret only this server knows.
        /// </remarks>
        /// <param name="token">The unguessable name of the channel.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The logo.</returns>
        [HttpGet("Channels/{token}/image")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetChannelImage(string token, CancellationToken cancellationToken)
        {
            if (!TvheadendAccessToken.TryRead(token, Plugin.Instance.Configuration.RecordingAccessSecret, out var channelId))
            {
                return NotFound();
            }

            // What the channel says its icon is, from the catalog the HTSP connection keeps. The
            // token names the channel and nothing else, so no caller can choose the address this
            // fetches from.
            var icon = _connection.Channels.Get(channelId)?.Icon;
            if (string.IsNullOrEmpty(icon))
            {
                return NotFound();
            }

            var endpoint = await _connection.GetHttpEndpointAsync(cancellationToken).ConfigureAwait(false);
            var upstream = endpoint.ResolveImageUrl(icon);
            if (string.IsNullOrEmpty(upstream))
            {
                return NotFound();
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, upstream);

            // Only to TVHeadend. A channel icon can be an absolute URL an EPG provider supplied,
            // pointing anywhere at all, and sending the TVHeadend credentials to some other host
            // would hand them to whoever runs it.
            if (upstream.StartsWith(endpoint.BaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var header in endpoint.CreateHeaders())
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
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
                _logger.LogError(exception, "TVHeadend channel {ChannelId}: its logo could not be fetched", channelId);
                return StatusCode(StatusCodes.Status502BadGateway);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TVHeadend answered {StatusCode} for the logo of channel {ChannelId}",
                    (int)response.StatusCode,
                    channelId);
                response.Dispose();
                return StatusCode(response.StatusCode == HttpStatusCode.NotFound
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status502BadGateway);
            }

            // Read whole rather than streamed. A logo is a few kilobytes, and Jellyfin's image
            // pipeline wants a complete body it can hash and store.
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.ToString();
            response.Dispose();

            return File(body, string.IsNullOrEmpty(contentType) ? "image/png" : contentType);
        }
    }
}
