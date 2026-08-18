using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;
using Tvheadend.Htsp;
using Tvheadend.Htsp.Model;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using TVHeadEnd.Tvheadend;
using TVHeadEnd.Tvheadend.Catalogs;

namespace TVHeadEnd.LiveTv;

/// <summary>
/// Opens one live channel: the media over HTTP, the description over HTSP, and the proof that the
/// two are the same service.
/// </summary>
/// <remarks>
/// <para>
/// The order matters. The HTTP <c>pass</c> subscription is opened first, which is what makes
/// TVHeadend choose and start a service; the HTSP subscription follows on the same channel and
/// attaches to the service that is already running. Opening them the other way round would leave
/// two independent choices to agree by luck.
/// </para>
/// <para>
/// Luck is not relied on either way. Which service HTSP landed on is read from the source
/// information it reports, resolved to a service through TVHeadend's HTTP API, and then checked
/// against the program map of the bytes actually arriving. If the PIDs do not agree, the
/// description is discarded rather than published: a media source combining one service's
/// metadata with another's video is wrong in a way nothing downstream could detect.
/// </para>
/// </remarks>
public sealed class LiveStreamOpener
{
    /// <summary>
    /// How long to wait for TVHeadend to describe the stream.
    /// </summary>
    /// <remarks>
    /// The server withholds its description of a video service until it has parsed a frame size,
    /// which takes as long as the broadcast's own key frame interval. Exceeding this is not fatal:
    /// the media is already flowing, and the stream is published with Jellyfin left to establish
    /// what it contains.
    /// </remarks>
    private static readonly TimeSpan DescriptionTimeLimit = TimeSpan.FromSeconds(10);

    private readonly TvheadendConnection _connection;
    private readonly TvheadendApiClient _api;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly string _bufferDirectory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveStreamOpener"/> class.
    /// </summary>
    /// <param name="connection">The TVHeadend connection.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="applicationHost">The Jellyfin application host, for the local stream address.</param>
    /// <param name="bufferDirectory">Where live buffers are written.</param>
    /// <param name="logger">The logger.</param>
    public LiveStreamOpener(
        TvheadendConnection connection,
        IHttpClientFactory httpClientFactory,
        IServerApplicationHost applicationHost,
        string bufferDirectory,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentException.ThrowIfNullOrEmpty(bufferDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _api = new TvheadendApiClient(httpClientFactory, logger);
        _httpClientFactory = httpClientFactory;
        _applicationHost = applicationHost;
        _bufferDirectory = bufferDirectory;
        _logger = logger;
    }

    /// <summary>
    /// Opens a channel.
    /// </summary>
    /// <param name="channelId">The TVHeadend channel identifier.</param>
    /// <param name="mediaSourceId">The identity the media source is to carry.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The opened stream, described and ready to hand to Jellyfin.</returns>
    public async Task<TvheadendLiveStream> OpenAsync(
        string channelId,
        string mediaSourceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);

        var stopwatch = Stopwatch.StartNew();
        var channel = _connection.Channels.Get(channelId);
        var endpoint = await _connection.GetHttpEndpointAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Live TV: opening channel {ChannelId} ({ChannelName})",
            channelId,
            channel?.Name ?? "<unknown>");

        var stream = new TvheadendLiveStream(
            channelId,
            channel?.Name,
            endpoint.CreateChannelStreamUrl(channelId, TvheadendHttpEndpoint.PassProfile),
            endpoint.CreateHeaders(),
            LiveMediaSource.CreatePending(mediaSourceId, channel?.Name ?? "Live TV"),
            Path.Combine(_bufferDirectory, FormattableString.Invariant($"tvheadend-{Guid.NewGuid():N}")),
            _connection.Settings.LiveBufferSizeMegabytes,
            _httpClientFactory,
            _logger);

        try
        {
            // First, so that TVHeadend picks a service and starts it. The metadata subscription
            // then attaches to what is already running instead of choosing for itself.
            await stream.Open(cancellationToken).ConfigureAwait(false);

            await DescribeAsync(stream, channel, endpoint, mediaSourceId, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "Live TV: channel {ChannelId} ready after {ElapsedMilliseconds} ms",
                channelId,
                stopwatch.ElapsedMilliseconds);

            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task DescribeAsync(
        TvheadendLiveStream stream,
        TvheadendChannel? channel,
        TvheadendHttpEndpoint endpoint,
        string mediaSourceId,
        CancellationToken cancellationToken)
    {
        var streamUrl = _applicationHost.GetApiUrlForLocalAccess().TrimEnd('/')
            + "/LiveTv/LiveStreamFiles/" + stream.UniqueId + "/stream.ts";

        HtspSubscription? subscription = null;
        LiveStreamDescription? description = null;

        try
        {
            if (int.TryParse(stream.ChannelId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
            {
                subscription = await _connection.SubscribeForMetadataAsync(numericId, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogDebug(
                    "Live TV: HTSP subscription {SubscriptionId} opened for channel {ChannelId}",
                    subscription.SubscriptionId,
                    stream.ChannelId);

                var start = await subscription.WaitForStartAsync(cancellationToken)
                    .WaitAsync(DescriptionTimeLimit, cancellationToken)
                    .ConfigureAwait(false);

                description = await BuildDescriptionAsync(stream, channel, endpoint, start, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning(
                    "Live TV: channel identifier {ChannelId} is not an HTSP channel number, so the stream cannot be described over HTSP",
                    stream.ChannelId);
            }
        }
        catch (Exception exception) when (exception is HtspException or TimeoutException or InvalidOperationException)
        {
            // The media is already flowing. A stream that could not be described is still a
            // stream; Jellyfin is simply left to establish what is in it, which is what it does
            // for every other live TV plugin.
            _logger.LogWarning(
                "Live TV: channel {ChannelId} could not be described over HTSP ({Reason}); Jellyfin will inspect the stream itself",
                stream.ChannelId,
                exception.Message);
        }

        stream.MediaSource = LiveMediaSource.CreateOpened(
            mediaSourceId,
            channel?.Name ?? "Live TV",
            stream.MediaPath,
            streamUrl,
            description);

        LogPublishedSource(stream);

        if (subscription is not null)
        {
            // Kept for the life of the stream. A later description means the broadcast changed
            // shape, and the media source is corrected rather than left describing what used to
            // be there.
            stream.AttachMetadata(
                subscription,
                async start =>
                {
                    var updated = await BuildDescriptionAsync(stream, channel, endpoint, start, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (updated is { IsUsable: true })
                    {
                        stream.MediaSource.MediaStreams = [.. updated.Streams];

                        // Set together with the streams, never apart: the first description may
                        // have arrived too late to be published, and a source carrying real
                        // stream indices while still inviting a probe is describing itself twice.
                        stream.MediaSource.SupportsProbing = false;

                        _logger.LogInformation(
                            "Live TV: channel {ChannelId} was described again by TVHeadend; the media source now lists {StreamCount} streams",
                            stream.ChannelId,
                            updated.Streams.Count);
                    }
                });
        }
    }

    private async Task<LiveStreamDescription?> BuildDescriptionAsync(
        TvheadendLiveStream stream,
        TvheadendChannel? channel,
        TvheadendHttpEndpoint endpoint,
        HtspSubscriptionStart start,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Live TV: channel {ChannelId} source information: {SourceInfo}",
            stream.ChannelId,
            start.SourceInfo);

        var programMap = stream.ProgramMap;
        if (programMap is null)
        {
            _logger.LogDebug(
                "Live TV: channel {ChannelId} has no complete program map yet, so its streams cannot be placed",
                stream.ChannelId);
            return null;
        }

        var service = await ResolveServiceAsync(channel, endpoint, start.SourceInfo, cancellationToken)
            .ConfigureAwait(false);
        if (service is null)
        {
            _logger.LogInformation(
                "Live TV: the TVHeadend service behind channel {ChannelId} could not be resolved, so stream indices cannot be established. "
                + "This needs an account with administrator rights, which the plugin's README explains",
                stream.ChannelId);
            return null;
        }

        if (!LiveStreamDescription.AgreesWith(programMap, service))
        {
            _logger.LogWarning(
                "Live TV: channel {ChannelId} is being delivered with PIDs [{DeliveredPids}] but the service HTSP reported carries [{ServicePids}]. "
                + "The two halves of the stream are not the same service, so the HTSP description is discarded",
                stream.ChannelId,
                string.Join(", ", programMap.GetPids()),
                string.Join(", ", service.GetPids()));
            return null;
        }

        var description = LiveStreamDescription.Build(start, programMap, service);
        if (description is null)
        {
            return null;
        }

        LogStreamMapping(stream.ChannelId, start, programMap, service, description);
        return description;
    }

    /// <summary>
    /// Works out which TVHeadend service is behind a channel.
    /// </summary>
    /// <remarks>
    /// A channel with one mapped service is the whole answer. A channel with several is decided by
    /// stable identity -- the multiplex the subscription reports, and the service name within it --
    /// rather than by position or by guessing which one TVHeadend prefers.
    /// </remarks>
    private async Task<ServiceDescription?> ResolveServiceAsync(
        TvheadendChannel? channel,
        TvheadendHttpEndpoint endpoint,
        HtspSourceInfo sourceInfo,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(channel?.Uuid))
        {
            return null;
        }

        var services = await _api.GetChannelServicesAsync(endpoint, channel.Uuid, cancellationToken)
            .ConfigureAwait(false);
        if (services.Count == 0)
        {
            return null;
        }

        if (services.Count == 1)
        {
            return await _api.GetServiceStreamsAsync(endpoint, services[0], cancellationToken)
                .ConfigureAwait(false);
        }

        if (!sourceInfo.IdentifiesService)
        {
            _logger.LogInformation(
                "Live TV: channel {ChannelName} maps to {ServiceCount} services and TVHeadend did not say which one it tuned. "
                + "An account with the anonymise right withholds that, and without it the service cannot be identified",
                channel.Name,
                services.Count);
            return null;
        }

        foreach (var candidate in services)
        {
            var (muxUuid, serviceName) = await _api.GetServiceIdentityAsync(endpoint, candidate, cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(muxUuid, sourceInfo.MuxUuid, StringComparison.OrdinalIgnoreCase)
                && string.Equals(serviceName, sourceInfo.Service, StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "Live TV: channel {ChannelName} resolved to service {ServiceUuid} on multiplex {MuxUuid}",
                    channel.Name,
                    candidate,
                    muxUuid);

                return await _api.GetServiceStreamsAsync(endpoint, candidate, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _logger.LogWarning(
            "Live TV: none of the {ServiceCount} services mapped to channel {ChannelName} matches the multiplex and name TVHeadend reported",
            services.Count,
            channel.Name);
        return null;
    }

    private void LogStreamMapping(
        string channelId,
        HtspSubscriptionStart start,
        ProgramMapTable programMap,
        ServiceDescription service,
        LiveStreamDescription description)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var lines = new List<string>();
        for (var index = 0; index < programMap.Entries.Count && index < description.Streams.Count; index++)
        {
            var entry = programMap.Entries[index];
            var htsp = start.Streams.FirstOrDefault(stream => service.GetPid(stream.Index) == entry.Pid);
            var mapped = description.Streams[index];

            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"jellyfin[{index}]<-pid {entry.Pid}<-es_index {htsp?.Index.ToString(CultureInfo.InvariantCulture) ?? "-"} {htsp?.Type ?? "?"}=>{mapped.Codec ?? "?"}"));
        }

        _logger.LogDebug(
            "Live TV: channel {ChannelId} stream mapping: {Mapping}",
            channelId,
            string.Join("; ", lines));
    }

    private void LogPublishedSource(TvheadendLiveStream stream)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var source = stream.MediaSource;
        var video = source.MediaStreams?.FirstOrDefault(media => media.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
        var audio = source.MediaStreams?.Where(media => media.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio).ToList() ?? [];

        _logger.LogDebug(
            "Live TV: channel {ChannelId} published as id={SourceId} container={Container} probing={SupportsProbing} "
            + "video={VideoCodec} {Width}x{Height} {FrameRate}fps audio=[{Audio}]",
            stream.ChannelId,
            source.Id,
            source.Container,
            source.SupportsProbing,
            video?.Codec ?? "<none>",
            video?.Width?.ToString(CultureInfo.InvariantCulture) ?? "?",
            video?.Height?.ToString(CultureInfo.InvariantCulture) ?? "?",
            video?.RealFrameRate?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?",
            string.Join(", ", audio.Select(media => string.Create(
                CultureInfo.InvariantCulture,
                $"{media.Index}:{media.Codec}/{media.Language}/{media.Channels}ch"))));
    }
}
