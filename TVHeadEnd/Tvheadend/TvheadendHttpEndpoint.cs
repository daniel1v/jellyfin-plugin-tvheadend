using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web;

namespace TVHeadEnd.Tvheadend
{
    /// <summary>
    /// Where TVHeadend's HTTP interface is, and how to address it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single place that composes a TVHeadend URL. Playback and domain code ask for a stream
    /// address and receive one; none of them knows the host, the web root, the profile parameter
    /// or how the request is authenticated.
    /// </para>
    /// <para>
    /// Credentials never appear in a URL. An earlier arrangement put them there whenever
    /// multi-audio support was switched on, which tied an unrelated capability to the
    /// authentication method and, worse, meant a media source handed to a client carried the
    /// TVHeadend password. Authentication is a header, always, and what a client may switch
    /// between is a separate question answered by the stream profile.
    /// </para>
    /// </remarks>
    public sealed class TvheadendHttpEndpoint
    {
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

            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(_userName + ":" + _password));
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
        /// Builds the address a ticketed stream is read from.
        /// </summary>
        /// <remarks>
        /// A TVHeadend access ticket authorises one stream without credentials, which is what
        /// lets the plugin fetch it without sending the password on every request. The profile
        /// still has to be appended, because the ticket authorises the path and not the form.
        /// </remarks>
        /// <param name="ticketPath">The path the ticket handler returned.</param>
        /// <param name="profile">The stream profile, or <see langword="null"/> for the server default.</param>
        /// <returns>The stream URL.</returns>
        public string CreateTicketedStreamUrl(string ticketPath, string? profile)
        {
            ArgumentException.ThrowIfNullOrEmpty(ticketPath);

            var url = BaseUrl + ticketPath;
            if (string.IsNullOrEmpty(profile))
            {
                return url;
            }

            return url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?")
                + "profile=" + HttpUtility.UrlEncode(profile);
        }

        /// <summary>
        /// Builds the address of a JSON API endpoint.
        /// </summary>
        /// <param name="path">The API path, such as "api/profile/list".</param>
        /// <returns>The API URL.</returns>
        public string CreateApiUrl(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);

            return BaseUrl + "/" + path.TrimStart('/');
        }
    }
}
