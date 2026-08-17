using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Tvheadend
{
    /// <summary>
    /// Asks TVHeadend which stream profiles exist.
    /// </summary>
    /// <remarks>
    /// Purely so the settings page can offer the real names and say whether a configured one
    /// exists. Discovery needs an account permitted to read the admin API; where that permission
    /// is absent the request fails, the settings fall back to a free-text field, and nothing else
    /// changes -- a profile that cannot be listed still works if its name is right.
    /// </remarks>
    public sealed class TvheadendProfileDiscovery
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvheadendProfileDiscovery"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="logger">The logger.</param>
        public TvheadendProfileDiscovery(IHttpClientFactory httpClientFactory, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(httpClientFactory);
            ArgumentNullException.ThrowIfNull(logger);

            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Lists the stream profiles TVHeadend offers.
        /// </summary>
        /// <param name="endpoint">Where TVHeadend is.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The profile names, or <see langword="null"/> when the server could not be asked --
        /// which is not an error, only an absence of information.
        /// </returns>
        public async Task<IReadOnlyCollection<string>?> ListProfiles(
            TvheadendHttpEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(endpoint);

            try
            {
                using var client = _httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.CreateApiUrl("api/profile/list"));
                foreach (var header in endpoint.CreateHeaders())
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "TVHeadend stream profiles: the server answered {StatusCode} to the profile listing, so profiles have to be entered by name",
                        (int)response.StatusCode);
                    return null;
                }

                var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var bodyScope = body.ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!document.RootElement.TryGetProperty("entries", out var entries)
                    || entries.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var names = new List<string>();
                foreach (var entry in entries.EnumerateArray())
                {
                    // TVHeadend reports the profile name under "val"; "key" is its identifier.
                    if (entry.TryGetProperty("val", out var value)
                        && value.ValueKind == JsonValueKind.String
                        && value.GetString() is { Length: > 0 } name)
                    {
                        names.Add(name);
                    }
                }

                _logger.LogInformation("TVHeadend stream profiles: the server offers {Count} profile(s)", names.Count);
                return names;
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
            {
                _logger.LogInformation(
                    exception,
                    "TVHeadend stream profiles: could not be listed, so profiles have to be entered by name");
                return null;
            }
        }
    }
}
