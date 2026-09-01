using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Configuration;
using TVHeadEnd.LiveTv;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd;

/// <summary>
/// The adapter between Jellyfin's live TV contract and TVHeadend.
/// </summary>
/// <remarks>
/// <para>
/// Only an adapter. What a channel is belongs to the catalogues, opening one to
/// <see cref="LiveStreamOpener"/>, recording to <see cref="TvheadendDvr"/> and the guide to
/// <see cref="TvheadendGuide"/>; this translates Jellyfin's questions into theirs and their
/// answers back.
/// </para>
/// <para>
/// Exactly one media source is offered per channel, because TVHeadend delivers exactly one thing.
/// Whether a client plays it as it is, remuxes it or transcodes it is Jellyfin's decision, taken
/// against the device profile the client sent and taken again once the stream is open and
/// described. There is no second playback policy here.
/// </para>
/// </remarks>
public sealed class LiveTvService : ILiveTvService, ISupportsDirectStreamProvider, ISupportsNewTimerIds
{
    private readonly TvheadendConnection _connection;
    private readonly LiveStreamOpener _opener;
    private readonly TvheadendDvr _dvr;
    private readonly TvheadendGuide _guide;
    private readonly ChannelItemIds _itemIds;
    private readonly PlaybackClient _client;
    private readonly OpenLiveStreams _openStreams;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IPluginPreferencesSource _preferences;
    private readonly Api.TvheadendArtwork _artwork;

    private readonly ILogger<LiveTvService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvService"/> class.
    /// </summary>
    /// <param name="connection">The TVHeadend connection.</param>
    /// <param name="opener">Opens a channel's live stream.</param>
    /// <param name="dvr">Reads and writes TVHeadend's timers and rules.</param>
    /// <param name="guide">Reads the programme guide.</param>
    /// <param name="itemIds">The identifiers Jellyfin gave this plugin's items.</param>
    /// <param name="artwork">How an image reference becomes an address Jellyfin can fetch.</param>
    /// <param name="client">Who is asking, as far as the authenticated session says.</param>
    /// <param name="applicationHost">The Jellyfin application host.</param>
    /// <param name="preferences">The plugin's own behaviour settings.</param>
    /// <param name="openStreams">Where an opened stream is recorded, so a request naming only
    /// its media source can be answered with the live stream it stands for.</param>
    /// <param name="logger">The logger.</param>
    public LiveTvService(
        TvheadendConnection connection,
        LiveStreamOpener opener,
        TvheadendDvr dvr,
        TvheadendGuide guide,
        ChannelItemIds itemIds,
        Api.TvheadendArtwork artwork,
        PlaybackClient client,
        IServerApplicationHost applicationHost,
        IPluginPreferencesSource preferences,
        OpenLiveStreams openStreams,
        ILogger<LiveTvService> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _connection = connection;
        _opener = opener;
        _dvr = dvr;
        _guide = guide;
        _itemIds = itemIds;
        _artwork = artwork;
        _client = client;
        _applicationHost = applicationHost;
        _preferences = preferences;
        _openStreams = openStreams;
    }

    /// <inheritdoc />
    public string Name => "TVHclient LiveTvService";

    /// <inheritdoc />
    public string HomePageUrl => "https://tvheadend.org/";

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        await _connection.WaitForInitialSyncAsync(cancellationToken).ConfigureAwait(false);

        var channels = JellyfinChannelMapper.ToChannelInfos(
            _connection.Channels.GetChannels(),
            _connection.ChannelTags,
            _connection.Settings.ChannelTypeForOther);
        var endpoint = _connection.HttpEndpoint;

        foreach (var channel in channels)
        {
            var known = _connection.Channels.Get(channel.Id);
            // Padded, like every other place a logo stands in a picture frame. Jellyfin draws a
            // channel's image edge to edge in its tile, and a logo that fills its frame reads as a
            // mistake rather than as a logo. The address changes with the padding, and
            // GuideManager replaces a channel image whose path has changed, so this needs nothing
            // of anybody.
            channel.ImageUrl = _artwork.PaddedAddressFor(known?.Icon, null, endpoint);

            channel.HasImage = !string.IsNullOrEmpty(channel.ImageUrl);
        }

        return channels;
    }

    /// <summary>
    /// Lists what Jellyfin may choose between for a channel, which is one thing.
    /// </summary>
    /// <remarks>
    /// Must never cost a TVHeadend subscription: Jellyfin calls this during playback negotiation
    /// and for every channel in a list. Nothing here touches the network.
    /// </remarks>
    /// <param name="channelId">The channel.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The single source on offer.</returns>
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var channel = _connection.Channels.Get(channelId);

        return Task.FromResult<List<MediaSourceInfo>>(
        [
            LiveMediaSource.CreatePending(GetMediaSourceId(channelId), channel?.Name ?? "Live TV"),
        ]);
    }

    /// <inheritdoc />
    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(
        string channelId,
        string streamId,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentLiveStreams);

        // Jellyfin serialises this: MediaSourceManager.OpenLiveStreamInternal holds its own lock
        // across the whole call, so two viewers starting the same channel arrive here one after
        // the other and the second finds the first stream to reuse. A lock of our own would only
        // duplicate that.
        var needsIdrToStart = _client.NeedsIdrEntryPoint;
        var reusable = currentLiveStreams
            .OfType<TvheadendLiveStream>()
            .FirstOrDefault(stream => CanBeReusedFor(stream, channelId, needsIdrToStart));

        // Who is watching, not how many times they asked. A client whose first attempt at
        // playback fails negotiates again, and Jellyfin answers by asking for the stream once
        // more; recognising that as the same viewer is what keeps the stream from being held
        // open by attempts that were abandoned.
        var consumer = _client.ResolveConsumerId();

        // Which device is watching, so that the request that plays the stream can find it again.
        // One channel can have several streams open at once -- two viewers whose profiles differ
        // get a rendering each -- and they all carry the same media source identifier.
        var device = _client.DeviceId;

        if (reusable is not null)
        {
            _openStreams.Register(GetMediaSourceId(channelId), device, reusable);

            if (reusable.Consumers.Acquire(consumer))
            {
                _logger.LogInformation(
                    "Live TV: channel {ChannelId} is already running and now has {ConsumerCount} viewers",
                    channelId,
                    reusable.ConsumerCount);
            }
            else
            {
                _logger.LogDebug(
                    "Live TV: channel {ChannelId} is already running for this viewer, who is negotiating again",
                    channelId);
            }

            return reusable;
        }

        var opened = await _opener.OpenAsync(
                channelId,
                GetMediaSourceId(channelId),
                JellyfinChannelMapper.ChannelTypeFor(
                    _connection.Channels.Get(channelId),
                    _connection.Settings.ChannelTypeForOther),
                cancellationToken)
            .ConfigureAwait(false);

        // Only once it is open. A stream that failed to open is never registered, so a failed
        // attempt cannot leave a viewer behind holding one.
        opened.Consumers.Acquire(consumer);
        _openStreams.Register(GetMediaSourceId(channelId), device, opened);
        return opened;
    }

    /// <summary>
    /// The fallback for services that cannot manage their own live streams.
    /// </summary>
    /// <remarks>
    /// Jellyfin only takes this branch for a service that does not implement
    /// <see cref="ISupportsDirectStreamProvider"/>, so this one never reaches it. Answering would
    /// mean handing out the bare TVHeadend address: a second subscription for a channel already
    /// being received, and a stream that has passed neither the conditioner nor anything that
    /// knows where a decoder may start in it.
    /// </remarks>
    /// <param name="channelId">The channel to open.</param>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Never returns; always throws.</returns>
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "TVHeadend channels are served through the managed live stream. "
            + "Open them with GetChannelStreamWithDirectStreamProvider.");

    /// <inheritdoc />
    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
    {
        // Jellyfin disposes the stream itself, which is what releases the buffer and the HTSP
        // subscription. Nothing is left for this to do.
        _logger.LogDebug("Live TV: Jellyfin closed stream {StreamId}", id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        string channelId,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        await _connection.WaitForInitialSyncAsync(cancellationToken).ConfigureAwait(false);
        return await _guide.GetProgramsAsync(channelId, startDateUtc, endDateUtc, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
    {
        await _connection.WaitForInitialSyncAsync(cancellationToken).ConfigureAwait(false);
        return [.. _connection.Dvr.GetEntries().Where(JellyfinDvrMapper.IsTimer).Select(JellyfinDvrMapper.ToTimer)];
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
    {
        await _connection.WaitForInitialSyncAsync(cancellationToken).ConfigureAwait(false);
        return JellyfinSeriesRuleMapper.ToSeriesTimers(
            _connection.SeriesRules.GetRules(),
            await _connection.GetServerOffsetAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        => Task.FromResult(new SeriesTimerInfo
        {
            PrePaddingSeconds = _preferences.Current.PrePaddingSeconds,
            PostPaddingSeconds = _preferences.Current.PostPaddingSeconds,

            // The configured default, and the only place it belongs. A series rule is written
            // with the priority it carries, so that editing one does not reset it -- which means
            // a new one has to arrive here already carrying the default rather than picking it up
            // at the moment it is written. LiveTvManager copies this onto every timer it creates.
            Priority = _connection.Settings.Priority,
            RecordAnyChannel = true,
            RecordAnyTime = true,
            RecordNewOnly = false,
        });

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => _dvr.CreateTimerAsync(info, cancellationToken);

    /// <summary>
    /// Schedules a recording and answers with the identifier TVHeadend gave it.
    /// </summary>
    /// <remarks>
    /// Jellyfin prefers this over <see cref="CreateTimerAsync"/> wherever a service offers it, and
    /// keeps what comes back as the timer's external identifier. Without it Jellyfin had no
    /// identifier for a timer it had just created: it announced the new timer under nothing at
    /// all, and only the next refresh of the whole list connected the recording to the thing that
    /// had asked for it.
    /// </remarks>
    /// <param name="info">The timer to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The TVHeadend identifier of the new entry.</returns>
    public Task<string> CreateTimer(TimerInfo info, CancellationToken cancellationToken)
        => _dvr.CreateTimerAsync(info, cancellationToken);

    /// <summary>
    /// Creates a series rule and answers with the identifier TVHeadend gave it.
    /// </summary>
    /// <param name="info">The series timer to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The TVHeadend identifier of the new rule.</returns>
    public Task<string> CreateSeriesTimer(SeriesTimerInfo info, CancellationToken cancellationToken)
        => _dvr.CreateSeriesTimerAsync(info, cancellationToken);

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
        => _dvr.UpdateTimerAsync(updatedTimer, cancellationToken);

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        => _dvr.CancelTimerAsync(timerId, cancellationToken);

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => _dvr.CreateSeriesTimerAsync(info, cancellationToken);

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => _dvr.UpdateSeriesTimerAsync(info, cancellationToken);

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        => _dvr.CancelSeriesTimerAsync(timerId, cancellationToken);

    /// <inheritdoc />
    public Task ResetTuner(string id, CancellationToken cancellationToken)
        => throw new NotSupportedException("TVHeadend manages its own tuners.");

    /// <summary>
    /// Reports whether an open stream can serve a request for a channel.
    /// </summary>
    /// <remarks>
    /// A live stream is shared by every viewer of the same channel: it is one TVHeadend
    /// subscription writing one ring buffer, and each reader joins at its own entry point.
    /// </remarks>
    /// <param name="stream">The open stream.</param>
    /// <param name="channelId">The channel being requested.</param>
    /// <param name="needsIdrToStart">Whether the asking client's decoder needs IDR pictures.</param>
    /// <returns>Whether the stream may be shared.</returns>
    internal static bool CanBeReusedFor(TvheadendLiveStream stream, string channelId, bool needsIdrToStart)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream is not { EnableStreamSharing: true, HasBuffer: true }
            || !string.Equals(stream.ChannelId, channelId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A stream opened for a decoder that needs IDR pictures carries a media source with
        // direct play withdrawn, so that Jellyfin re-encodes the video. Handing it to anyone else
        // would transcode a channel that plays perfectly well as it is.
        if (stream.RequiresVideoReencode)
        {
            return needsIdrToStart;
        }

        // And the other way round: a broadcast whose access point holds no IDR is a stream such a
        // decoder never starts on, however much of it is already buffered. It opens its own,
        // which is the same bytes with a media source that asks Jellyfin to re-encode them.
        // And the other way round: a broadcast whose entry points hold no IDR is a stream such a
        // decoder never starts on, however much of it is already buffered. It opens its own, which
        // is the same bytes with a media source that asks Jellyfin to re-encode them.
        return !needsIdrToStart || stream.OffersIdrJoins;
    }

    /// <summary>
    /// Gets the identity the channel's one media source carries.
    /// </summary>
    /// <remarks>
    /// The channel's own Jellyfin item identifier, which is what Jellyfin's convention for a
    /// single source is and what clients ask for when they have made no choice. Jellyfin for
    /// Android sends the item identifier as the media source identifier by default, and the
    /// server matches that against the offered sources with an ordinal comparison before it will
    /// auto-open the stream; an identifier of this plugin's own invention would simply not be
    /// found, and playback would stall with the source never opened.
    /// </remarks>
    private string GetMediaSourceId(string channelId) => _itemIds.GetInternalChannelId(Name, channelId);
}
