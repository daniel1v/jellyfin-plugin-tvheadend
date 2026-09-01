using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Core.Media;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.LiveTv;

/// <summary>
/// Opens one live channel.
/// </summary>
/// <remarks>
/// <para>
/// The whole of it: fetch TVHeadend's <c>pass</c> stream, wait until the conditioner has the
/// program tables and a point a decoder can start at, and hand Jellyfin a media source describing
/// what that program map says. Nothing else is consulted, so nothing else can be slow, be refused
/// for want of a permission, or disagree with the bytes.
/// </para>
/// <para>
/// This used to run a second HTSP subscription and two administrator-only API calls to describe
/// the same stream. Every one of those existed to answer a question the transport stream answers
/// by itself.
/// </para>
/// </remarks>
public sealed class LiveStreamOpener
{
    private readonly TvheadendConnection _connection;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly PlaybackClient _client;
    private readonly string _bufferDirectory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveStreamOpener"/> class.
    /// </summary>
    /// <param name="connection">The TVHeadend connection, for the channel list and the endpoint.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="applicationHost">The Jellyfin application host, for the local stream address.</param>
    /// <param name="client">Who is asking, for the one decision that depends on it.</param>
    /// <param name="buffer">Where live buffers are written.</param>
    /// <param name="logger">The logger.</param>
    public LiveStreamOpener(
        TvheadendConnection connection,
        IHttpClientFactory httpClientFactory,
        IServerApplicationHost applicationHost,
        PlaybackClient client,
        LiveBufferLocation buffer,
        ILogger<LiveStreamOpener> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _httpClientFactory = httpClientFactory;
        _applicationHost = applicationHost;
        _client = client;
        _bufferDirectory = buffer.Path;
        _logger = logger;
    }

    /// <summary>
    /// Opens a channel.
    /// </summary>
    /// <param name="channelId">The TVHeadend channel identifier.</param>
    /// <param name="mediaSourceId">The identity the media source is to carry.</param>
    /// <param name="channelType">What the channel list says this channel is.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The opened stream, described and ready to hand to Jellyfin.</returns>
    public async Task<TvheadendLiveStream> OpenAsync(
        string channelId,
        string mediaSourceId,
        ChannelType channelType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);

        var stopwatch = Stopwatch.StartNew();
        var channel = _connection.Channels.Get(channelId);

        // Asked once, here, while the request that carries the answer is still in flight. The
        // stream is opened on this thread but read on another, by which time there is no request.
        var needsIdrToStart = _client.NeedsIdrEntryPoint;
        var endpoint = await _connection.GetHttpEndpointAsync(cancellationToken).ConfigureAwait(false);
        var name = channel?.Name ?? "Live TV";

        var stream = new TvheadendLiveStream(
            channelId,
            channel?.Name,
            endpoint.CreateChannelStreamUrl(channelId, TvheadendHttpEndpoint.PassProfile),
            endpoint.CreateHeaders(),
            LiveMediaSource.CreatePending(mediaSourceId, name),
            Path.Combine(_bufferDirectory, FormattableString.Invariant($"tvheadend-{Guid.NewGuid():N}")),
            _connection.Settings.LiveBufferSizeMegabytes,
            _httpClientFactory,
            _logger,
            needsIdrToStart);

        try
        {
            await stream.Open(cancellationToken).ConfigureAwait(false);

            var streamUrl = _applicationHost.GetApiUrlForLocalAccess().TrimEnd('/')
                + "/LiveTv/LiveStreamFiles/" + stream.UniqueId + "/stream.ts";

            // Already in hand: the conditioner parsed it on the way past, and the stream is not
            // published until it has one.
            var programMap = stream.ProgramMap;
            var description = programMap is null
                ? null
                : LiveStreamDescription.FromProgramMap(programMap, channelType);

            if (description is null)
            {
                // The program map does not carry what this kind of channel needs -- no video on a
                // television channel, no audio on a radio one. There is nothing to fall back to:
                // a probe would read the same stream to reach the same table, and publishing an
                // undescribed source only moves the failure to the client. Failing here at least
                // says what went wrong, in one line, immediately.
                var missing = channelType == ChannelType.Radio ? "audio" : "video";
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The program map of channel {channelId} ({name}) names no {missing} stream, so the channel cannot be described. Program map: {programMap?.Describe() ?? "<none received>"}"));
            }

            stream.MediaSource = LiveMediaSource.CreateOpened(
                mediaSourceId,
                name,
                stream.MediaPath,
                streamUrl,
                description,
                stream.RequiresVideoReencode);

            LogOpenedStream(stream, programMap, description, stopwatch.ElapsedMilliseconds);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void LogOpenedStream(
        TvheadendLiveStream stream,
        ProgramMapTable? programMap,
        LiveStreamDescription description,
        long elapsedMilliseconds)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var video = description.Streams.FirstOrDefault(media => media.Type == MediaStreamType.Video);
        var audio = description.Streams.Where(media => media.Type == MediaStreamType.Audio).ToList();
        var subtitles = description.Streams.Where(media => media.Type == MediaStreamType.Subtitle).ToList();

        _logger.LogDebug(
            "Live TV: channel {ChannelId} ({ChannelName}) ready in {ElapsedMilliseconds} ms; "
            + "program map [{ProgramMap}]; video {VideoCodec} at index {VideoIndex}; "
            + "audio [{Audio}]; subtitles [{Subtitles}]; startedOnConfirmedRandomAccessPoint={StartedOnRap}",
            stream.ChannelId,
            stream.ChannelName ?? "<unknown>",
            elapsedMilliseconds,
            programMap?.Describe() ?? "<none>",
            video?.Codec ?? "-",
            video?.Index ?? -1,
            string.Join(", ", audio.Select(media => string.Create(
                CultureInfo.InvariantCulture,
                $"{media.Index}:{media.Codec ?? "?"}/{media.Language ?? "-"}"))),
            string.Join(", ", subtitles.Select(media => string.Create(
                CultureInfo.InvariantCulture,
                $"{media.Index}:{media.Codec ?? "?"}/{media.Language ?? "-"}"))),
            stream.StartedOnConfirmedRandomAccessPoint);
    }
}
