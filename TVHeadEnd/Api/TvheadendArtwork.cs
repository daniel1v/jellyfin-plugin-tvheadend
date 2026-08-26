using System;
using System.Text;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.Api
{
    /// <summary>
    /// Turns a TVHeadend image reference into an address Jellyfin can actually fetch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One rule for every kind of artwork this plugin publishes -- a channel logo, an EPG
    /// programme image, a recording's poster -- because they arrive as the same kind of reference
    /// and fail in the same way. Jellyfin fetches an image URL with an HTTP client of its own,
    /// which knows nothing of TVHeadend, so anything on a TVHeadend that requires authentication
    /// comes back 401 and the item has no picture.
    /// </para>
    /// <para>
    /// So a reference that points at TVHeadend is published as an address on this plugin, and the
    /// one request that needs the credentials is made by the code that has them. A reference that
    /// points somewhere else -- an EPG provider's own artwork -- is published unchanged, because
    /// it needs no credentials and must not be sent any.
    /// </para>
    /// </remarks>
    public sealed class TvheadendArtwork
    {
        private readonly IServerApplicationHost _applicationHost;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvheadendArtwork"/> class.
        /// </summary>
        /// <param name="applicationHost">The Jellyfin application host, for this server's address.</param>
        /// <param name="logger">The logger.</param>
        public TvheadendArtwork(IServerApplicationHost applicationHost, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(applicationHost);
            ArgumentNullException.ThrowIfNull(logger);

            _applicationHost = applicationHost;
            _logger = logger;
        }

        /// <summary>
        /// The address to publish for an image TVHeadend named.
        /// </summary>
        /// <param name="reference">The raw reference from the HTSP message.</param>
        /// <param name="endpoint">The TVHeadend endpoint the reference is relative to.</param>
        /// <returns>The address, or <see langword="null"/> when there is no image.</returns>
        public string? AddressFor(string? reference, TvheadendHttpEndpoint endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);

            if (string.IsNullOrEmpty(reference))
            {
                return null;
            }

            // Somewhere that is not TVHeadend. Published as it stands: Jellyfin can fetch it
            // itself, and routing it through here would only add a hop -- and a chance of the
            // credentials going somewhere they must not.
            if (PathOnTvheadend(reference, endpoint.BaseUrl) is not { } path)
            {
                return reference;
            }

            try
            {
                var secret = TvheadendAccessSecret.Ensure(_logger);
                return _applicationHost.GetApiUrlForLocalAccess().TrimEnd('/')
                    + TvHeadendImagesController.ImagePathFor(TvheadendAccessToken.Create(Encode(path), secret));
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "TVHeadend: could not publish an address for the image {Reference}", reference);
                return null;
            }
        }

        /// <summary>
        /// The address to publish for an image, falling back to a second reference where the
        /// first names nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What the fallback is for: an EPG that carries no artwork. Broadcast DVB EIT has no
        /// field for a picture, so a recording made from it has none either, and a library of
        /// blank tiles is the result. The channel's own logo is at least true -- it says which
        /// broadcaster this came from -- and it is not a poster invented for the programme.
        /// </para>
        /// <para>
        /// It sticks, and that is worth knowing. Jellyfin's channel manager sets an item's image
        /// only when the item has none, so once the logo is stored, artwork that arrives later
        /// does not replace it on that item.
        /// </para>
        /// </remarks>
        /// <param name="reference">The image the item itself names.</param>
        /// <param name="fallback">What to fall back on, typically the channel's logo.</param>
        /// <param name="endpoint">The TVHeadend endpoint the references are relative to.</param>
        /// <returns>The address, or <see langword="null"/> when neither names anything.</returns>
        public string? AddressFor(string? reference, string? fallback, TvheadendHttpEndpoint endpoint)
            => AddressFor(reference, endpoint) ?? AddressFor(fallback, endpoint);

        /// <summary>
        /// The path a reference names on the TVHeadend server, or <see langword="null"/> when it
        /// names something on another host.
        /// </summary>
        /// <remarks>
        /// TVHeadend's references are version dependent: an absolute URL below the per-field
        /// threshold, a root-relative <c>/imagecache/N</c> between HTSP v8 and v14, and a relative
        /// <c>imagecache/N</c> from v15 on. An EPG provider may supply an absolute URL of its own,
        /// pointing anywhere at all, and that is the one case that is not ours.
        /// </remarks>
        /// <param name="reference">The raw reference.</param>
        /// <param name="baseUrl">The TVHeadend base URL.</param>
        /// <returns>The relative path, or <see langword="null"/>.</returns>
        internal static string? PathOnTvheadend(string? reference, string baseUrl)
        {
            if (string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(baseUrl))
            {
                return null;
            }

            var relative = reference;

            if (reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var root = baseUrl.TrimEnd('/');
                if (!reference.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                relative = reference[(root.Length + 1)..];
            }

            relative = relative.TrimStart('/');

            // A reference that climbs out of the web root, or carries a query of its own, is not
            // something TVHeadend produced for an image, and this refuses rather than forwards it.
            return relative.Length == 0
                || relative.Contains("..", StringComparison.Ordinal)
                || relative.Contains('?', StringComparison.Ordinal)
                || relative.Contains('#', StringComparison.Ordinal)
                    ? null
                    : relative;
        }

        /// <summary>
        /// Writes a path so that it survives a URL path segment.
        /// </summary>
        /// <remarks>
        /// Hex rather than base64: a path contains slashes, which cannot appear in the segment the
        /// token occupies, and hex produces only characters that can -- none of which is the
        /// hyphen the token separates its tag with.
        /// </remarks>
        /// <param name="path">The path.</param>
        /// <returns>The encoded path.</returns>
        internal static string Encode(string path)
            => Convert.ToHexString(Encoding.UTF8.GetBytes(path));

        /// <summary>
        /// Reads back what <see cref="Encode"/> wrote.
        /// </summary>
        /// <param name="encoded">The encoded path.</param>
        /// <param name="path">The path.</param>
        /// <returns>Whether it could be read.</returns>
        internal static bool TryDecode(string? encoded, out string path)
        {
            path = string.Empty;
            if (string.IsNullOrEmpty(encoded))
            {
                return false;
            }

            try
            {
                path = Encoding.UTF8.GetString(Convert.FromHexString(encoded));
            }
            catch (FormatException)
            {
                return false;
            }

            return path.Length > 0;
        }
    }
}
