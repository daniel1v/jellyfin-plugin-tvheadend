using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
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
public sealed class LiveTvService : ILiveTvService, ISupportsDirectStreamProvider
{
    private readonly TvheadendConnection _connection;
    private readonly LiveStreamOpener _opener;
    private readonly TvheadendDvr _dvr;
    private readonly TvheadendGuide _guide;
    private readonly ChannelItemIds _itemIds;
    private readonly ILogger<LiveTvService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvService"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="connection">The TVHeadend connection.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="configurationManager">The Jellyfin configuration manager.</param>
    /// <param name="applicationHost">The Jellyfin application host.</param>
    public LiveTvService(
        ILoggerFactory loggerFactory,
        TvheadendConnection connection,
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        IConfigurationManager configurationManager,
        IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(connection);

        _logger = loggerFactory.CreateLogger<LiveTvService>();
        _connection = connection;
        _itemIds = new ChannelItemIds(libraryManager);

        var bufferDirectory = LiveBufferDirectory.Resolve(configurationManager);
        LiveBufferDirectory.RemoveOrphaned(bufferDirectory, _logger);

        _opener = new LiveStreamOpener(connection, httpClientFactory, applicationHost, bufferDirectory, _logger);
        _dvr = new TvheadendDvr(connection, _logger);
        _guide = new TvheadendGuide(connection, _logger);
    }

    /// <inheritdoc />
    public string Name => "TVHclient LiveTvService";

    /// <inheritdoc />
    public string HomePageUrl => "https://tvheadend.org/";

    /// <summary>
    /// Gets when the recordings last changed, which is what Jellyfin polls to refresh them.
    /// </summary>
    public DateTime LastRecordingChange => _dvr.LastRecordingChange;

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        await _connection.WaitForInitialSyncAsync(cancellationToken).ConfigureAwait(false);

        _connection.Channels.SetTypeForOther(_connection.Settings.ChannelTypeForOther);
        var channels = _connection.Channels.ToChannelInfos();
        var endpoint = _connection.HttpEndpoint;

        foreach (var channel in channels)
        {
            var known = _connection.Channels.Get(channel.Id);
            channel.ImageUrl = endpoint.ResolveImageUrl(known?.Icon);
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

        var reusable = currentLiveStreams
            .OfType<TvheadendLiveStream>()
            .FirstOrDefault(stream => CanBeReusedFor(stream, channelId));

        if (reusable is not null)
        {
            reusable.ConsumerCount++;
            _logger.LogInformation(
                "Live TV: channel {ChannelId} is already running and now has {ConsumerCount} consumers",
                channelId,
                reusable.ConsumerCount);
            return reusable;
        }

        return await _opener.OpenAsync(channelId, GetMediaSourceId(channelId), cancellationToken)
            .ConfigureAwait(false);
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
        return _connection.Dvr.GetTimers();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
    {
        await _connection.WaitForInitialSyncAsync(cancellationToken).ConfigureAwait(false);
        return _connection.SeriesRules.ToSeriesTimers();
    }

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        => Task.FromResult(new SeriesTimerInfo
        {
            PrePaddingSeconds = Plugin.Instance.Configuration.Pre_Padding,
            PostPaddingSeconds = Plugin.Instance.Configuration.Post_Padding,
            RecordAnyChannel = true,
            RecordAnyTime = true,
            RecordNewOnly = false,
        });

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => _dvr.CreateTimerAsync(info, cancellationToken);

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
        => _dvr.UpdateTimerAsync(updatedTimer, cancellationToken);

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        => _dvr.CancelTimerAsync(timerId, cancellationToken);

    /// <summary>
    /// Deletes a recording. Not part of Jellyfin's live TV contract; the recordings channel
    /// calls it when a viewer deletes an item.
    /// </summary>
    /// <param name="recordingId">The TVHeadend entry identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once TVHeadend has accepted it.</returns>
    public Task DeleteRecordingAsync(string recordingId, CancellationToken cancellationToken)
        => _dvr.DeleteRecordingAsync(recordingId, cancellationToken);

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
    /// Gets the recordings TVHeadend holds, for the recordings channel.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The recordings.</returns>
    public async Task<IReadOnlyList<MyRecordingInfo>> GetRecordingsAsync(CancellationToken cancellationToken)
    {
        await _connection.WaitForInitialSyncAsync(cancellationToken).ConfigureAwait(false);
        return _connection.Dvr.GetRecordings();
    }

    /// <summary>
    /// Reports whether an open stream can serve a request for a channel.
    /// </summary>
    /// <remarks>
    /// A live stream is shared by every viewer of the same channel: it is one TVHeadend
    /// subscription writing one ring buffer, and each reader joins at its own entry point.
    /// </remarks>
    /// <param name="stream">The open stream.</param>
    /// <param name="channelId">The channel being requested.</param>
    /// <returns>Whether the stream may be shared.</returns>
    internal static bool CanBeReusedFor(TvheadendLiveStream stream, string channelId)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return stream is { EnableStreamSharing: true, HasBuffer: true }
            && string.Equals(stream.ChannelId, channelId, StringComparison.OrdinalIgnoreCase);
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
