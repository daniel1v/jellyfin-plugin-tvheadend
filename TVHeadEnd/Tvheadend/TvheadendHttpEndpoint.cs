using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web;

namespace TVHeadEnd.Tvheadend;

/// <summary>
/// Where TVHeadend's HTTP interface is, and how to address it.
/// </summary>
/// <remarks>
/// <para>
/// The single place that composes a TVHeadend URL. Everything else asks for an address and
/// receives one; none of it knows the host, the web root, the profile parameter or how the
/// request is authenticated.
/// </para>
/// <para>
/// Credentials never appear in a URL. An earlier arrangement put them there whenever multi-audio
/// support was switched on, which tied an unrelated capability to the authentication method and,
/// worse, meant a media source handed to a client carried the TVHeadend password. Authentication
/// is a header, always.
/// </para>
/// </remarks>
public sealed class TvheadendHttpEndpoint
{
    /// <summary>
    /// The TVHeadend stream profile that forwards the broadcast untouched.
    /// </summary>
    /// <remarks>
    /// The only profile this plugin ever asks for. TVHeadend is the tuner, not the encoder: it
    /// hands over the original transport stream with its own PCR, program tables and random
    /// access points intact, and what a given client can do with that is Jellyfin's decision to
    /// make against the device profile.
    /// </remarks>
    public const string PassProfile = "pass";

    private readonly string _userName;
    private readonly string _password;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvheadendHttpEndpoint"/> class.
    /// </summary>
    /// <param name="serverName">The TVHeadend host.</param>
    /// <param name="httpPort">The TVHeadend HTTP port.</param>
    /// <param name="webRoot">The web root the server reported, which may be empty.</param>
    /// <param name="userName">The user name, which may be empty.</param>
    /// <param name="password">The password, which may be empty.</param>
    public TvheadendHttpEndpoint(string serverName, int httpPort, string? webRoot, string? userName, string? password)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverName);

        _userName = userName ?? string.Empty;
        _password = password ?? string.Empty;
        BaseUrl = string.Create(
            CultureInfo.InvariantCulture,
            $"http://{serverName}:{httpPort}{webRoot ?? string.Empty}");
    }

    /// <summary>
    /// Gets the base URL, without credentials.
    /// </summary>
    public string BaseUrl { get; }

    /// <summary>
    /// Gets a value indicating whether requests have to be authenticated.
    /// </summary>
    public bool RequiresAuthentication => !string.IsNullOrEmpty(_userName);

    /// <summary>
    /// Builds the headers a request to TVHeadend needs.
    /// </summary>
    /// <returns>The headers, empty when the server needs no authentication.</returns>
    public IReadOnlyDictionary<string, string> CreateHeaders()
    {
        if (!RequiresAuthentication)
        {
            return new Dictionary<string, string>();
        }

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(_userName + ":" + _password));
        return new Dictionary<string, string>
        {
            ["Authorization"] = "Basic " + credentials,
        };
    }

    /// <summary>
    /// Builds the address a channel is streamed from.
    /// </summary>
    /// <param name="channelId">The TVHeadend channel identifier.</param>
    /// <param name="profile">The stream profile, or <see langword="null"/> for the server default.</param>
    /// <returns>The stream URL.</returns>
    public string CreateChannelStreamUrl(string channelId, string? profile)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);

        var url = BaseUrl + "/stream/channelid/" + HttpUtility.UrlEncode(channelId);
        return string.IsNullOrEmpty(profile)
            ? url
            : url + "?profile=" + HttpUtility.UrlEncode(profile);
    }

    /// <summary>
    /// Turns an image reference from an HTSP message into an absolute URL.
    /// </summary>
    /// <remarks>
    /// TVHeadend's image references are version dependent: below the per-field threshold the
    /// server sends an absolute URL, between HTSP v8 and v14 a root-relative
    /// <c>/imagecache/N</c> path, and from v15 on a relative <c>imagecache/N</c> path. An EPG
    /// provider may also supply an absolute URL of its own. Anything not already absolute is
    /// resolved against this endpoint, so every negotiated version yields a usable address.
    /// </remarks>
    /// <param name="image">The raw image value.</param>
    /// <returns>An absolute URL, or <see langword="null"/> when no image was supplied.</returns>
    public string? ResolveImageUrl(string? image)
    {
        if (string.IsNullOrEmpty(image))
        {
            return null;
        }

        if (image.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return image;
        }

        return BaseUrl + "/" + image.TrimStart('/');
    }

    /// <summary>
    /// Builds the address of a JSON API endpoint.
    /// </summary>
    /// <param name="path">The API path, such as "api/service/streams".</param>
    /// <returns>The API URL.</returns>
    public string CreateApiUrl(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return BaseUrl + "/" + path.TrimStart('/');
    }
}
