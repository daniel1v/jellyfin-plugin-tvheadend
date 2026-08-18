using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Tvheadend;

/// <summary>
/// Reads the parts of TVHeadend's HTTP API this plugin needs to tie a channel to a service and a
/// service's streams to their PIDs.
/// </summary>
/// <remarks>
/// <para>
/// HTSP does not carry this. Its channel announcements name a channel's services only by display
/// name, and its stream descriptions carry <c>es_index</c> without the PID behind it, so the
/// mapping from what HTSP describes to what actually arrives over HTTP cannot be built from HTSP
/// alone.
/// </para>
/// <para>
/// <c>service/streams</c> requires an administrator account. That is a real requirement of this
/// design rather than an oversight, and it is the reason the plugin documents one: without it
/// the elementary streams can still be described, but not placed at the index FFmpeg will give
/// them, and the plugin says so by letting Jellyfin probe instead of guessing.
/// </para>
/// </remarks>
public sealed class TvheadendApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvheadendApiClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public TvheadendApiClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the services a channel is mapped to.
    /// </summary>
    /// <param name="endpoint">Where TVHeadend is.</param>
    /// <param name="channelUuid">The channel's identity, as HTSP reported it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The service identities, in the order the server lists them.</returns>
    public async Task<IReadOnlyList<string>> GetChannelServicesAsync(
        TvheadendHttpEndpoint endpoint,
        string channelUuid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(channelUuid);

        var document = await GetJsonAsync(
            endpoint,
            "api/idnode/load?uuid=" + Uri.EscapeDataString(channelUuid),
            cancellationToken).ConfigureAwait(false);

        if (document is null)
        {
            return [];
        }

        using (document)
        {
            var services = ReadParameter(document.RootElement, channelUuid, "services");
            if (services is not { ValueKind: JsonValueKind.Array })
            {
                return [];
            }

            return [.. services.Value.EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : null)
                .Where(value => !string.IsNullOrEmpty(value))
                .Select(value => value!)];
        }
    }

    /// <summary>
    /// Gets the multiplex and name of a service, which is what identifies it against the source
    /// information a subscription reports.
    /// </summary>
    /// <param name="endpoint">Where TVHeadend is.</param>
    /// <param name="serviceUuid">The service's identity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The multiplex identity and the service name, either of which may be absent.</returns>
    public async Task<(string? MuxUuid, string? ServiceName)> GetServiceIdentityAsync(
        TvheadendHttpEndpoint endpoint,
        string serviceUuid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(serviceUuid);

        var document = await GetJsonAsync(
            endpoint,
            "api/idnode/load?uuid=" + Uri.EscapeDataString(serviceUuid),
            cancellationToken).ConfigureAwait(false);

        if (document is null)
        {
            return (null, null);
        }

        using (document)
        {
            return (
                ReadStringParameter(document.RootElement, serviceUuid, "multiplex_uuid"),
                ReadStringParameter(document.RootElement, serviceUuid, "svcname"));
        }
    }

    /// <summary>
    /// Gets the elementary streams of a service, with the PID each one is carried on.
    /// </summary>
    /// <remarks>
    /// The filtered list is preferred where the server produces one, because that is the set of
    /// streams a subscription actually receives; the full list stands in when it is empty, which
    /// is what an idle service reports.
    /// </remarks>
    /// <param name="endpoint">Where TVHeadend is.</param>
    /// <param name="serviceUuid">The service's identity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The description, or <see langword="null"/> when the API would not answer.</returns>
    public async Task<ServiceDescription?> GetServiceStreamsAsync(
        TvheadendHttpEndpoint endpoint,
        string serviceUuid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(serviceUuid);

        var document = await GetJsonAsync(
            endpoint,
            "api/service/streams?uuid=" + Uri.EscapeDataString(serviceUuid),
            cancellationToken).ConfigureAwait(false);

        if (document is null)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var components = ReadComponents(root, "fstreams");
            if (components.Count == 0)
            {
                components = ReadComponents(root, "streams");
            }

            var name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

            return new ServiceDescription(serviceUuid, name, components);
        }
    }

    private static List<ServiceComponent> ReadComponents(JsonElement root, string property)
    {
        var result = new List<ServiceComponent>();
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!element.TryGetProperty("pid", out var pidElement)
                || !pidElement.TryGetInt32(out var pid))
            {
                continue;
            }

            var type = element.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;

            // The PCR and PMT pseudo-entries carry a PID but no index; they are not elementary
            // streams and must not occupy one.
            int? index = element.TryGetProperty("index", out var indexElement)
                && indexElement.TryGetInt32(out var parsedIndex)
                    ? parsedIndex
                    : null;

            result.Add(new ServiceComponent(index, pid, type));
        }

        return result;
    }

    private static JsonElement? ReadParameter(JsonElement root, string uuid, string parameter)
    {
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.TryGetProperty("uuid", out var entryUuid)
                && entryUuid.ValueKind == JsonValueKind.String
                && !string.Equals(entryUuid.GetString(), uuid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!entry.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var candidate in parameters.EnumerateArray())
            {
                if (candidate.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && string.Equals(id.GetString(), parameter, StringComparison.Ordinal)
                    && candidate.TryGetProperty("value", out var value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? ReadStringParameter(JsonElement root, string uuid, string parameter)
    {
        var value = ReadParameter(root, uuid, parameter);
        return value is { ValueKind: JsonValueKind.String } ? value.Value.GetString() : null;
    }

    private async Task<JsonDocument?> GetJsonAsync(
        TvheadendHttpEndpoint endpoint,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.CreateApiUrl(path));
            foreach (var header in endpoint.CreateHeaders())
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "TVHeadend answered {StatusCode} for {Path}; the plugin will do without what it would have said",
                    (int)response.StatusCode,
                    path);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonDocument.Parse(content);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug(
                exception,
                "TVHeadend's HTTP API could not be read at {Path}; the plugin will do without what it would have said",
                path);
            return null;
        }
    }
}
