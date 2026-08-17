using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using TVHeadEnd.DataHelper;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP.Responses;
using TVHeadEnd.Media;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using TVHeadEnd.TimeoutHelper;
using TVHeadEnd.Tvheadend;
using static TVHeadEnd.TicketType;

namespace TVHeadEnd
{
    public class LiveTvService : ILiveTvService, ISupportsDirectStreamProvider
    {
        /// <summary>
        /// DVR_AUTOREC_BTYPE_ALL - record any broadcast.
        /// </summary>
        private const int BroadcastTypeAll = 0;

        /// <summary>
        /// DVR_AUTOREC_BTYPE_NEW_OR_UNKNOWN - record only broadcasts flagged as new or unflagged.
        /// </summary>
        private const int BroadcastTypeNewOrUnknown = 1;

        private readonly IMediaEncoder _mediaEncoder;

        private readonly TimeSpan _timeout = TimeSpan.FromMinutes(5);

        private readonly HTSConnectionHandler _htsConnectionHandler;
        private readonly AccessTicketHandler _channelTicketHandler;
        private readonly LiveTvItemIdResolver _liveTvItemIdResolver;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfigurationManager _configurationManager;
        private readonly IServerApplicationHost _applicationHost;

        // The playback layer. This service is the adapter to Jellyfin and owns none of it: what
        // a channel is belongs to the descriptor store, how it is analysed to the analyzer,
        // which variants exist to the playback policy, and every stream to itself.
        private readonly ChannelMediaDescriptorStore _descriptors;
        private readonly ChannelMediaAnalyzer _analyzer;
        private readonly ChannelFormatPreAnalyzer _preAnalyzer;
        private readonly IPlaybackClientContextAccessor _clientContext;
        private readonly string _bufferDirectory;

        private readonly ILogger<LiveTvService> _logger;

        private bool _profilesDiscovered;

        public LiveTvService(
            ILoggerFactory loggerFactory,
            IMediaEncoder mediaEncoder,
            HTSConnectionHandler connectionHandler,
            ILibraryManager libraryManager,
            IHttpClientFactory httpClientFactory,
            IConfigurationManager configurationManager,
            IServerApplicationHost applicationHost,
            ChannelMediaDescriptorStore descriptors,
            IPlaybackClientContextAccessor clientContext)
        {
            // System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
            _logger = loggerFactory.CreateLogger<LiveTvService>();
            _logger.LogDebug("LiveTvService()");

            _htsConnectionHandler = connectionHandler;
            _liveTvItemIdResolver = new LiveTvItemIdResolver(libraryManager);
            _httpClientFactory = httpClientFactory;
            _configurationManager = configurationManager;
            _applicationHost = applicationHost;
            _htsConnectionHandler.SetLiveTvService(this);
            {
                var lifeSpan = TimeSpan.FromSeconds(15);       // Revalidate tickets every 15 seconds
                var requestTimeout = TimeSpan.FromSeconds(10); // First request retry after 10 seconds
                var retries = 2;                               // Number of times to retry getting tickets
                _channelTicketHandler = new AccessTicketHandler(loggerFactory, _htsConnectionHandler, requestTimeout, retries, lifeSpan, Channel);
            }

            _mediaEncoder = mediaEncoder;
            _clientContext = clientContext;
            _analyzer = new ChannelMediaAnalyzer(new MediaInspector(mediaEncoder, _logger), _logger);
            _descriptors = descriptors;
            _preAnalyzer = new ChannelFormatPreAnalyzer(_descriptors, _logger);

            _bufferDirectory = LiveBufferDirectory.Resolve(_configurationManager);
            LiveBufferDirectory.RemoveOrphaned(_bufferDirectory, _logger);
        }

        public DateTime LastRecordingChange { get; private set; } = DateTime.MinValue;

        public string HomePageUrl
        {
            get { return "http://tvheadend.org/"; }
        }

        public string Name
        {
            get { return "TVHclient LiveTvService"; }
        }

        public async Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.CancelSeriesTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage deleteAutorecMessage = new HTSMessage();
            deleteAutorecMessage.Method = "deleteAutorecEntry";
            deleteAutorecMessage.PutField("id", timerId);

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(_timeout);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Run(
                () =>
                {
                    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                    _htsConnectionHandler.SendMessage(deleteAutorecMessage, lbrh);
                    LastRecordingChange = DateTime.UtcNow;
                    return lbrh.GetResponse();
                },
                cancellationToken)).ConfigureAwait(false);

            if (twtRes.HasTimeout)
            {
                _logger.LogError("LiveTvService.CancelSeriesTimerAsync: can't delete recording because the timeout was reached");
            }
            else
            {
                HTSMessage deleteAutorecResponse = twtRes.Result;
                bool success = deleteAutorecResponse.GetInt("success", 0) == 1;
                if (!success)
                {
                    if (deleteAutorecResponse.ContainsField("error"))
                    {
                        _logger.LogError("LiveTvService.CancelSeriesTimerAsync: can't delete recording: '{Why}'", deleteAutorecResponse.GetString("error"));
                    }
                    else if (deleteAutorecResponse.ContainsField("noaccess"))
                    {
                        _logger.LogError("LiveTvService.CancelSeriesTimerAsync: can't delete recording: '{Why}'", deleteAutorecResponse.GetString("noaccess"));
                    }
                }
            }
        }

        public async Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.CancelTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage cancelTimerMessage = new HTSMessage();
            cancelTimerMessage.Method = "cancelDvrEntry";
            cancelTimerMessage.PutField("id", timerId);

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(_timeout);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Run(
                () =>
                {
                    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                    _htsConnectionHandler.SendMessage(cancelTimerMessage, lbrh);
                    LastRecordingChange = DateTime.UtcNow;
                    return lbrh.GetResponse();
                },
                cancellationToken)).ConfigureAwait(false);

            if (twtRes.HasTimeout)
            {
                _logger.LogError("LiveTvService.CancelTimerAsync: can't cancel timer because the timeout was reached");
            }
            else
            {
                HTSMessage cancelTimerResponse = twtRes.Result;
                bool success = cancelTimerResponse.GetInt("success", 0) == 1;
                if (!success)
                {
                    if (cancelTimerResponse.ContainsField("error"))
                    {
                        _logger.LogError("LiveTvService.CancelTimerAsync: can't cancel timer: '{Why}'", cancelTimerResponse.GetString("error"));
                    }
                    else if (cancelTimerResponse.ContainsField("noaccess"))
                    {
                        _logger.LogError("LiveTvService.CancelTimerAsync: can't cancel timer: '{Why}'", cancelTimerResponse.GetString("noaccess"));
                    }
                }
            }
        }

        public async Task CloseLiveStream(string id, CancellationToken cancellationToken)
        {
            await Task.Run(
                () =>
                {
                    _logger.LogDebug("LiveTvService.CloseLiveStream: closed stream for subscriptionId: {Id}", id);
                    return id;
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);

            int timeOut = await WaitForInitialLoadTask(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.CreateSeriesTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage createAutorecMessage = new HTSMessage();
            createAutorecMessage.Method = "addAutorecEntry";
            BuildAutorecFields(createAutorecMessage, info);
            createAutorecMessage.PutField("configName", _htsConnectionHandler.GetDvrProfile());

            await SendAutorecMessage(createAutorecMessage, nameof(CreateSeriesTimerAsync), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Fills in the autorec fields shared by addAutorecEntry and updateAutorecEntry.
        /// </summary>
        /// <param name="message">The message to populate.</param>
        /// <param name="info">The series timer to translate.</param>
        private void BuildAutorecFields(HTSMessage message, SeriesTimerInfo info)
        {
            message.PutField("title", info.Name);

            // A negative channelId means "any channel" from HTSP v25 on; older servers treat an
            // absent channelId the same way, so it is only sent for a channel-bound timer.
            if (!info.RecordAnyChannel && !string.IsNullOrEmpty(info.ChannelId))
            {
                message.PutField("channelId", Convert.ToInt32(info.ChannelId, CultureInfo.InvariantCulture));
            }
            else if (_htsConnectionHandler.GetNegotiatedProtocolVersion() > 24)
            {
                message.PutField("channelId", -1);
            }

            if (info.Days != null && info.Days.Count > 0 && info.Days.Count < 7)
            {
                message.PutField("daysOfWeek", AutorecDataHelper.GetDaysOfWeekFromList(info.Days));
            }

            // "start"/"startWindow" are minutes from midnight, -1 meaning any time.
            if (info.RecordAnyTime)
            {
                message.PutField("start", -1);
                message.PutField("startWindow", -1);
            }
            else
            {
                int start = AutorecDataHelper.GetMinutesFromMidnight(info.StartDate);
                message.PutField("start", start);
                message.PutField("startWindow", (start + 30) % (24 * 60));
            }

            // Padding is exchanged in minutes; 0 falls back to the DVR configuration.
            message.PutField("startExtra", (long)(info.PrePaddingSeconds / 60));
            message.PutField("stopExtra", (long)(info.PostPaddingSeconds / 60));
            message.PutField("priority", _htsConnectionHandler.GetPriority());
            message.PutField("broadcastType", info.RecordNewOnly ? BroadcastTypeNewOrUnknown : BroadcastTypeAll);
        }

        /// <summary>
        /// Sends an autorec message and logs whatever TVHeadend reports back.
        /// </summary>
        /// <param name="message">The autorec message to send.</param>
        /// <param name="caller">The calling method, used for log context.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the operation.</returns>
        private async Task SendAutorecMessage(HTSMessage message, string caller, CancellationToken cancellationToken)
        {
            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(_timeout);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Run(
                () =>
                {
                    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                    _htsConnectionHandler.SendMessage(message, lbrh);
                    LastRecordingChange = DateTime.UtcNow;
                    return lbrh.GetResponse();
                },
                cancellationToken)).ConfigureAwait(false);

            if (twtRes.HasTimeout)
            {
                _logger.LogError("LiveTvService.{Caller}: can't change series timer because the timeout was reached", caller);
                return;
            }

            HTSMessage response = twtRes.Result;
            if (response.GetInt("success", 0) == 1)
            {
                return;
            }

            if (response.ContainsField("error"))
            {
                _logger.LogError("LiveTvService.{Caller}: can't change series timer: '{Why}'", caller, response.GetString("error"));
            }
            else if (response.ContainsField("noaccess"))
            {
                _logger.LogError("LiveTvService.{Caller}: can't change series timer: user is not allowed to record", caller);
            }
        }

        public async Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.CreateTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage createTimerMessage = new HTSMessage();
            createTimerMessage.Method = "addDvrEntry";
            createTimerMessage.PutField("channelId", info.ChannelId);
            createTimerMessage.PutField("start", DateTimeHelper.GetUnixUtcTimeFromUtcDateTime(info.StartDate));
            createTimerMessage.PutField("stop", DateTimeHelper.GetUnixUtcTimeFromUtcDateTime(info.EndDate));
            createTimerMessage.PutField("startExtra", (long)(info.PrePaddingSeconds / 60));
            createTimerMessage.PutField("stopExtra", (long)(info.PostPaddingSeconds / 60));
            createTimerMessage.PutField("priority", _htsConnectionHandler.GetPriority()); // info.Priority delivers always 0 - no GUI
            createTimerMessage.PutField("configName", _htsConnectionHandler.GetDvrProfile());
            createTimerMessage.PutField("description", info.Overview);
            createTimerMessage.PutField("title", info.Name);
            createTimerMessage.PutField("creator", Plugin.Instance.Configuration.Username);

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(_timeout);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Run(
                () =>
                {
                    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                    _htsConnectionHandler.SendMessage(createTimerMessage, lbrh);
                    return lbrh.GetResponse();
                },
                cancellationToken)).ConfigureAwait(false);

            if (twtRes.HasTimeout)
            {
                _logger.LogError("LiveTvService.CreateTimerAsync: can't create timer because the timeout was reached");
            }
            else
            {
                HTSMessage createTimerResponse = twtRes.Result;
                bool success = createTimerResponse.GetInt("success", 0) == 1;
                if (!success)
                {
                    if (createTimerResponse.ContainsField("error"))
                    {
                        _logger.LogError("LiveTvService.CreateTimerAsync: can't create timer: '{Why}'", createTimerResponse.GetString("error"));
                    }
                    else if (createTimerResponse.ContainsField("noaccess"))
                    {
                        _logger.LogError("LiveTvService.CreateTimerAsync: can't create timer: '{Why}'", createTimerResponse.GetString("noaccess"));
                    }
                }
            }
        }

        public async Task DeleteRecordingAsync(string recordingId, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("LiveTvService.DeleteRecordingAsync: call cancelled or timed out");
                return;
            }

            HTSMessage deleteRecordingMessage = new HTSMessage();
            deleteRecordingMessage.Method = "deleteDvrEntry";
            deleteRecordingMessage.PutField("id", recordingId);

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(_timeout);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Run(
                () =>
                {
                    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                    _htsConnectionHandler.SendMessage(deleteRecordingMessage, lbrh);
                    LastRecordingChange = DateTime.UtcNow;
                    return lbrh.GetResponse();
                },
                cancellationToken)).ConfigureAwait(false);

            if (twtRes.HasTimeout)
            {
                _logger.LogError("LiveTvService.DeleteRecordingAsync: can't delete recording because the timeout was reached");
            }
            else
            {
                HTSMessage deleteRecordingResponse = twtRes.Result;
                bool success = deleteRecordingResponse.GetInt("success", 0) == 1;
                if (!success)
                {
                    if (deleteRecordingResponse.ContainsField("error"))
                    {
                        _logger.LogError("LiveTvService.DeleteRecordingAsync: can't delete recording: '{Why}'", deleteRecordingResponse.GetString("error"));
                    }
                    else if (deleteRecordingResponse.ContainsField("noaccess"))
                    {
                        _logger.LogError("LiveTvService.DeleteRecordingAsync: can't delete recording: '{Why}'", deleteRecordingResponse.GetString("noaccess"));
                    }
                }
            }
        }

        public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("LiveTvService.GetChannelsAsync: call cancelled or timed out - returning empty list");
                return new List<ChannelInfo>();
            }

            TaskWithTimeoutRunner<IEnumerable<ChannelInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<ChannelInfo>>(_timeout);
            TaskWithTimeoutResult<IEnumerable<ChannelInfo>> twtRes = await
                twtr.RunWithTimeout(_htsConnectionHandler.BuildChannelInfos(cancellationToken)).ConfigureAwait(false);

            if (twtRes.HasTimeout)
            {
                return new List<ChannelInfo>();
            }

            var list = twtRes.Result.ToList();

            foreach (var channel in list)
            {
                if (string.IsNullOrEmpty(channel.ImageUrl))
                {
                    channel.ImageUrl = _htsConnectionHandler.GetChannelImageUrl(channel.Id);
                }
            }

            // Descriptions of channels TVHeadend no longer offers are of no use to anyone.
            var channelIds = list.Select(channel => channel.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();
            _descriptors.RemoveMissingChannels(channelIds);

            if (Plugin.Instance.Configuration.AnalyzeChannelFormatsOnRefresh)
            {
                await AnalyzeUnknownChannels(channelIds, cancellationToken).ConfigureAwait(false);
            }

            return list;
        }

        /// <summary>
        /// Describes the channels nothing current is known about, one at a time.
        /// </summary>
        /// <remarks>
        /// Awaited rather than started and forgotten, so a cancelled refresh really does stop the
        /// analysis and no tuner is left occupied by work nobody is waiting for.
        /// </remarks>
        private async Task AnalyzeUnknownChannels(IReadOnlyCollection<string> channelIds, CancellationToken cancellationToken)
        {
            var nativeProfile = _htsConnectionHandler.GetStreamProfiles().GetProfileName(StreamProfileRole.Native);

            await _preAnalyzer.Run(
                channelIds,
                nativeProfile,
                async (channelId, token) =>
                {
                    // Deliberately the native profile only: a compatibility profile would start a
                    // transcoder on the TVHeadend server for a channel nobody is watching.
                    var stream = await OpenVariant(channelId, PlaybackVariant.Native, nativeProfile, token)
                        .ConfigureAwait(false);
                    try
                    {
                        return await _analyzer.Analyze(
                            channelId,
                            nativeProfile,
                            stream.Buffer.Path,
                            stream.Observation,
                            token).ConfigureAwait(false);
                    }
                    finally
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// The <see cref="ILiveTvService"/> fallback for services that cannot manage their own
        /// live streams. Jellyfin only takes this branch for services that do not implement
        /// <see cref="ISupportsDirectStreamProvider"/>, so this one never reaches it. Answering
        /// it would mean handing out the bare TVHeadend URL: a second subscription for a channel
        /// that is already being received, and a stream that has passed neither the conditioner
        /// nor the re-encode that broadcasts without IDR frames need.
        /// </summary>
        /// <param name="channelId">The channel to open.</param>
        /// <param name="streamId">The stream identifier chosen by the client.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Never returns; always throws.</returns>
        public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException(
                "TVHeadend channels are served through the managed live stream. " +
                "Open them with GetChannelStreamWithDirectStreamProvider.");
        }

        /// <summary>
        /// Offers the variants of a channel, without opening anything.
        /// </summary>
        /// <remarks>
        /// This must never cost a TVHeadend subscription: Jellyfin calls it during playback
        /// negotiation, and for every channel in a list. Everything it needs comes from what
        /// earlier tunes stored and from which stream profiles are configured.
        /// </remarks>
        /// <param name="channelId">The channel.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The variants on offer, native first.</returns>
        public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        {
            var profiles = _htsConnectionHandler.GetStreamProfiles();
            var nativeProfile = profiles.GetProfileName(StreamProfileRole.Native);
            var native = _descriptors.Get(channelId, nativeProfile);

            var offers = PlaybackVariantPolicy.SelectVariants(
                native,
                GetVariantAvailability(profiles),
                _clientContext.Current);

            // The variant offered first answers to the channel's own item identifier as well.
            // Clients that do not pick a source send that identifier back, and if nothing carries
            // it the stream is never opened -- which is exactly what stopped every Android
            // playback once the variants were given identifiers of their own.
            var itemId = _liveTvItemIdResolver.GetInternalChannelId(Name, channelId);
            var sources = offers
                .Select((offer, index) => JellyfinMediaSourceMapper.CreatePending(
                    channelId,
                    offer,
                    native,
                    _descriptors.Get(channelId, nativeProfile, offer.Variant.ToString()),
                    index == 0 ? itemId : null,
                    offers.Count > 1))
                .ToList();

            _logger.LogInformation(
                "Live TV playback negotiation: channel {ChannelId} ({ChannelName}) offers {Offers} to {Client}",
                channelId,
                _htsConnectionHandler.GetChannelName(channelId) ?? "<unknown>",
                string.Join(", ", offers.Select(offer => offer.Variant.ToString())),
                _clientContext.Current.Describe());

            return Task.FromResult(sources);
        }

        /// <summary>
        /// Opens a channel in the variant Jellyfin selected against the client's device profile.
        /// </summary>
        /// <param name="channelId">The channel to open.</param>
        /// <param name="streamId">The media source identifier Jellyfin selected.</param>
        /// <param name="currentLiveStreams">The streams Jellyfin already holds open.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The opened live stream.</returns>
        public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(
            string channelId,
            string streamId,
            List<ILiveStream> currentLiveStreams,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(currentLiveStreams);

            var profiles = _htsConnectionHandler.GetStreamProfiles();
            var nativeProfile = profiles.GetProfileName(StreamProfileRole.Native);

            // A client that chose a variant sends its identifier back. One that did not sends the
            // channel's item identifier -- which Jellyfin strips to null before it reaches here
            // -- and then it gets whatever was offered first.
            var requested = PlaybackVariantId.Resolve(channelId, streamId)
                ?? FirstOfferedVariant(channelId, nativeProfile, profiles);

            // The opened source has to answer to the same identifier the negotiation listed it
            // under, because the client asks for it again by that identifier and Jellyfin matches
            // the two by exact string comparison. Handing back a different one is answered with a
            // null dereference deep inside the streaming helper.
            var publishedId = string.IsNullOrEmpty(streamId)
                ? _liveTvItemIdResolver.GetInternalChannelId(Name, channelId)
                : streamId;

            // Reuse is keyed by channel and role together, so a broadcast and a rendering of it
            // can never be handed out for one another.
            var reusable = currentLiveStreams
                .OfType<TvheadendLiveStream>()
                .FirstOrDefault(stream => stream.EnableStreamSharing
                    && stream.HasBuffer
                    && string.Equals(stream.ChannelId, channelId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(stream.VariantRole, requested.ToString(), StringComparison.Ordinal));
            if (reusable is not null)
            {
                reusable.ConsumerCount++;
                _logger.LogInformation(
                    "Live TV stream reuse: {Role} of channel {ChannelId} now has {ConsumerCount} consumers",
                    reusable.VariantRole,
                    channelId,
                    reusable.ConsumerCount);
                return reusable;
            }

            var stopwatch = Stopwatch.StartNew();
            var stream = await OpenVariant(channelId, requested, nativeProfile, cancellationToken).ConfigureAwait(false);
            var openedAt = stopwatch.ElapsedMilliseconds;

            try
            {
                var observation = stream.Observation;
                var descriptor = await DescribeOpenedStream(
                    channelId,
                    requested,
                    nativeProfile,
                    stream,
                    observation,
                    cancellationToken).ConfigureAwait(false);

                // Only one thing forces a change of variant mid-open, and only towards safety: a
                // broadcast that turns out to offer no place an affected client's decoder can
                // start. The corrected description is stored either way.
                var reconciled = PlaybackVariantPolicy.ReconcileAfterOpen(
                    requested,
                    observation.RandomAccess,
                    GetVariantAvailability(profiles),
                    _clientContext.Current);

                if (reconciled != requested)
                {
                    _logger.LogInformation(
                        "Live TV stream start: channel {ChannelId} signals random access without IDR frames and {Client} cannot start on that, so {Role} is used instead",
                        channelId,
                        _clientContext.Current.Describe(),
                        reconciled);

                    await stream.DisposeAsync().ConfigureAwait(false);
                    return await OpenReconciled(channelId, reconciled, nativeProfile, publishedId, cancellationToken)
                        .ConfigureAwait(false);
                }

                PublishOpenedStream(channelId, requested, stream, descriptor, publishedId);
                stream.OriginalStreamId = streamId;

                _logger.LogInformation(
                    "Live TV stream start: channel {ChannelId} handed to Jellyfin after {ElapsedMilliseconds} ms as {Role} (opening took {OpenMilliseconds} ms)",
                    channelId,
                    stopwatch.ElapsedMilliseconds,
                    requested,
                    openedAt);

                return stream;
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Opens one variant of a channel, reading the TVHeadend profile its role names.
        /// </summary>
        private async Task<TvheadendLiveStream> OpenVariant(
            string channelId,
            PlaybackVariant variant,
            string? nativeProfile,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var ticket = await _channelTicketHandler.GetTicket(channelId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Live TV stream start: access ticket ready for channel {ChannelId} after {ElapsedMilliseconds} ms",
                channelId,
                stopwatch.ElapsedMilliseconds);
            _logger.LogInformation(
                "Live TV stream start: HTSP channel mapping {ChannelId} -> {ChannelName}",
                channelId,
                _htsConnectionHandler.GetChannelName(channelId) ?? "<unknown>");

            var endpoint = _htsConnectionHandler.GetHttpEndpoint();
            var profiles = _htsConnectionHandler.GetStreamProfiles();
            await EnsureProfilesDiscovered(endpoint, profiles, cancellationToken).ConfigureAwait(false);
            // Where the normalization role is asked for but no TVHeadend profile fills it, the
            // broadcast is fetched natively and normalized here instead. Transitional: it goes
            // when a validated TVHeadend profile makes it unnecessary.
            var normalizeHere = variant == PlaybackVariant.H264IdrNormalization
                && LegacyNormalizationAvailable(profiles);
            var profile = profiles.GetProfileName(normalizeHere ? StreamProfileRole.Native : ToRole(variant));

            var upstreamUrl = endpoint.CreateTicketedStreamUrl(ticket.Url, profile);
            var describedAlready = _descriptors.Get(channelId, nativeProfile, VariantRoleName(variant)) is not null;

            if (normalizeHere)
            {
                _logger.LogInformation(
                    "Live TV stream start: channel {ChannelId} is normalized by the plugin's transitional encoder, because no TVHeadend profile fills the {Role} role",
                    channelId,
                    StreamProfileRole.H264IdrNormalization);
            }

            var stream = new TvheadendLiveStream(
                channelId,
                variant.ToString(),
                upstreamUrl,
                endpoint.CreateHeaders(),
                JellyfinMediaSourceMapper.CreatePending(channelId, new VariantOffer(variant, true), null),
                Path.Combine(_bufferDirectory, $"tvheadend-{Guid.NewGuid():N}"),
                _htsConnectionHandler.GetLiveBufferSizeMegabytes(),
                describedAlready,
                _httpClientFactory,
                _logger,
                normalizeHere ? _mediaEncoder.EncoderPath : null);

            await stream.Open(cancellationToken).ConfigureAwait(false);
            return stream;
        }

        private async Task<ILiveStream> OpenReconciled(
            string channelId,
            PlaybackVariant variant,
            string? nativeProfile,
            string publishedId,
            CancellationToken cancellationToken)
        {
            var stream = await OpenVariant(channelId, variant, nativeProfile, cancellationToken).ConfigureAwait(false);
            try
            {
                var descriptor = await DescribeOpenedStream(
                    channelId,
                    variant,
                    nativeProfile,
                    stream,
                    stream.Observation,
                    cancellationToken).ConfigureAwait(false);

                PublishOpenedStream(channelId, variant, stream, descriptor, publishedId);
                stream.OriginalStreamId = publishedId;
                return stream;
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Analyses what an opened stream actually contains and stores it.
        /// </summary>
        /// <remarks>
        /// The analysis reads the local buffer, never the upstream channel. For a compatibility
        /// role the result is also checked against what the role promises, and the role is marked
        /// invalid if the configured TVHeadend profile does not deliver it.
        /// </remarks>
        private async Task<ChannelMediaDescriptor?> DescribeOpenedStream(
            string channelId,
            PlaybackVariant variant,
            string? nativeProfile,
            TvheadendLiveStream stream,
            TransportObservation observation,
            CancellationToken cancellationToken)
        {
            var role = VariantRoleName(variant);
            var stored = _descriptors.Get(channelId, nativeProfile, role);
            if (stored is not null && stored.MatchesProgram(observation.ProgramSignature))
            {
                _logger.LogInformation(
                    "Live TV stream start: reused the stored description of channel {ChannelId}; the broadcast still announces the same elementary streams",
                    channelId);

                // The random access verdict always comes from this tune, even when everything
                // else is reused: an identical PMT proves nothing about the GOP structure.
                return stored with { RandomAccess = observation.RandomAccess };
            }

            var descriptor = await _analyzer.Analyze(
                channelId,
                nativeProfile,
                stream.Buffer.Path,
                observation,
                cancellationToken).ConfigureAwait(false);
            if (descriptor is null)
            {
                return null;
            }

            descriptor = descriptor with { VariantRole = role };
            _descriptors.Record(descriptor);

            // What the plugin's own transitional encoder produces says nothing about the
            // TVHeadend profile of that role, and recording it as validation would report a
            // profile as proven that was never used.
            var profiles = _htsConnectionHandler.GetStreamProfiles();
            if (variant != PlaybackVariant.Native
                && !(variant == PlaybackVariant.H264IdrNormalization && LegacyNormalizationAvailable(profiles)))
            {
                var satisfies = JellyfinMediaSourceMapper.SatisfiesContract(variant, descriptor);
                profiles.RecordValidation(
                    ToRole(variant),
                    satisfies,
                    satisfies ? null : $"produced {descriptor.VideoCodec} with {descriptor.RandomAccess} random access");

                if (!satisfies)
                {
                    _logger.LogWarning(
                        "Live TV stream start: the TVHeadend profile for {Role} produced {Codec} with {RandomAccess} random access, which does not satisfy the role. It will not be offered again until the profile is corrected",
                        variant,
                        descriptor.VideoCodec,
                        descriptor.RandomAccess);
                }
            }

            return descriptor;
        }

        /// <summary>
        /// Points the opened stream's media source at its buffer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The source keeps the identifier the caller opened it with. Handing back a different
        /// one leaves the client asking for a source that no later negotiation lists, and
        /// Jellyfin answers that by dereferencing null while preparing the stream.
        /// </para>
        /// <para>
        /// A recovery-point broadcast served natively to a client that cannot cold-start one is
        /// not offered for direct play. Jellyfin then transcodes it, which is no cure -- it
        /// copies the video -- but handing the client a source it cannot start is not one
        /// either. The cure is the normalized variant; this only keeps the broken pairing from
        /// winning by default.
        /// </para>
        /// </remarks>
        private void PublishOpenedStream(
            string channelId,
            PlaybackVariant variant,
            TvheadendLiveStream stream,
            ChannelMediaDescriptor? descriptor,
            string requestedId)
        {
            var supportsDirectPlay = variant != PlaybackVariant.Native
                || descriptor?.RandomAccess != H264RandomAccessKind.RecoveryOpenGop
                || !PlaybackQuirkPolicy.Applies(_clientContext.Current, PlaybackQuirk.H264DvbRecoveryOpenGopColdStart);

            stream.MediaSource = JellyfinMediaSourceMapper.CreateOpened(
                channelId,
                new VariantOffer(variant, supportsDirectPlay),
                descriptor,
                stream.Buffer.Path,
                _applicationHost.GetApiUrlForLocalAccess().TrimEnd('/')
                    + "/LiveTv/LiveStreamFiles/"
                    + stream.UniqueId
                    + "/stream.ts");

            if (!string.IsNullOrEmpty(requestedId))
            {
                stream.MediaSource.Id = requestedId;
            }
        }

        /// <summary>
        /// Asks TVHeadend once which stream profiles it offers, so the settings page can say
        /// whether a configured name exists.
        /// </summary>
        /// <remarks>
        /// Done here rather than during playback negotiation, which must stay free of network
        /// work, and awaited rather than started and forgotten. A server that will not answer
        /// costs one failed request per lifetime and changes nothing else: a profile that cannot
        /// be listed still works if its name is right.
        /// </remarks>
        private async Task EnsureProfilesDiscovered(
            TvheadendHttpEndpoint endpoint,
            TvheadendStreamProfiles profiles,
            CancellationToken cancellationToken)
        {
            if (_profilesDiscovered)
            {
                return;
            }

            _profilesDiscovered = true;
            var discovery = new TvheadendProfileDiscovery(_httpClientFactory, _logger);
            var names = await discovery.ListProfiles(endpoint, cancellationToken).ConfigureAwait(false);
            profiles.ApplyDiscovery(names);

            foreach (var status in profiles.GetStatus())
            {
                _logger.LogInformation(
                    "TVHeadend stream profile {Role}: {ProfileName} is {State}{Detail}",
                    status.Role,
                    string.IsNullOrEmpty(status.ProfileName) ? "<not configured>" : status.ProfileName,
                    status.State,
                    string.IsNullOrEmpty(status.Detail) ? string.Empty : " -- " + status.Detail);
            }
        }

        /// <summary>
        /// Returns the variant a caller receives when it did not pick one.
        /// </summary>
        /// <remarks>
        /// Decided the same way the offer was, from the same facts and the same request context,
        /// so the source that answers to the channel's item identifier is the one the client was
        /// shown first.
        /// </remarks>
        private PlaybackVariant FirstOfferedVariant(
            string channelId,
            string? nativeProfile,
            TvheadendStreamProfiles profiles)
        {
            var offers = PlaybackVariantPolicy.SelectVariants(
                _descriptors.Get(channelId, nativeProfile),
                GetVariantAvailability(profiles),
                _clientContext.Current);

            return offers.Count > 0 ? offers[0].Variant : PlaybackVariant.Native;
        }

        private static bool LegacyNormalizationAvailable(TvheadendStreamProfiles profiles)
            => !profiles.IsUsable(StreamProfileRole.H264IdrNormalization)
                && Plugin.Instance.Configuration.EnableLegacyH264Fallback;

        private PlaybackVariantAvailability GetVariantAvailability(TvheadendStreamProfiles profiles)
            => new(
                profiles.IsUsable(StreamProfileRole.Mpeg2H264Compatibility),
                profiles.IsUsable(StreamProfileRole.H264IdrNormalization)
                    || LegacyNormalizationAvailable(profiles));

        private static StreamProfileRole ToRole(PlaybackVariant variant)
            => variant switch
            {
                PlaybackVariant.Mpeg2H264Compatibility => StreamProfileRole.Mpeg2H264Compatibility,
                PlaybackVariant.H264IdrNormalization => StreamProfileRole.H264IdrNormalization,
                _ => StreamProfileRole.Native,
            };

        private static string? VariantRoleName(PlaybackVariant variant)
            => variant == PlaybackVariant.Native ? null : variant.ToString();

        public async Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        {
            return await Task.Run(
                () =>
                {
                    return new SeriesTimerInfo
                    {
                        PrePaddingSeconds = Plugin.Instance.Configuration.Pre_Padding,
                        PostPaddingSeconds = Plugin.Instance.Configuration.Post_Padding,
                        RecordAnyChannel = true,
                        RecordAnyTime = true,
                        RecordNewOnly = false
                    };
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.GetProgramsAsync: call cancelled or timed out - returning empty list");
                return new List<ProgramInfo>();
            }

            GetEventsResponseHandler currGetEventsResponseHandler = new GetEventsResponseHandler(startDateUtc, endDateUtc, _logger, cancellationToken);

            HTSMessage queryEvents = new HTSMessage();
            queryEvents.Method = "getEvents";
            queryEvents.PutField("channelId", Convert.ToInt32(channelId, CultureInfo.InvariantCulture));
            queryEvents.PutField("maxTime", ((DateTimeOffset)endDateUtc).ToUnixTimeSeconds());
            _htsConnectionHandler.SendMessage(queryEvents, currGetEventsResponseHandler);

            _logger.LogDebug("LiveTvService.GetProgramsAsync: ask TVH for events of channel '{Chanid}'", channelId);

            TaskWithTimeoutRunner<IEnumerable<ProgramInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<ProgramInfo>>(_timeout);
            TaskWithTimeoutResult<IEnumerable<ProgramInfo>> twtRes = await
                twtr.RunWithTimeout(currGetEventsResponseHandler.GetEvents(channelId, cancellationToken)).ConfigureAwait(false);

            if (twtRes.HasTimeout)
            {
                _logger.LogDebug("LiveTvService.GetProgramsAsync: timeout reached while calling for events of channel '{Chanid}'", channelId);
                return new List<ProgramInfo>();
            }

            var programs = twtRes.Result.ToList();

            // From HTSP v34 on the server sends imagecache references relative to the web root
            // instead of absolute URLs, so they have to be resolved against the TVH endpoint.
            foreach (var program in programs)
            {
                program.ImageUrl = _htsConnectionHandler.ResolveImageUrl(program.ImageUrl);
                program.HasImage = !string.IsNullOrEmpty(program.ImageUrl);
            }

            return programs;
        }

        public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.GetSeriesTimersAsync: call cancelled ot timed out - returning empty list");
                return new List<SeriesTimerInfo>();
            }

            TaskWithTimeoutRunner<IEnumerable<SeriesTimerInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<SeriesTimerInfo>>(_timeout);
            TaskWithTimeoutResult<IEnumerable<SeriesTimerInfo>> twtRes = await
                twtr.RunWithTimeout(_htsConnectionHandler.BuildAutorecInfos(cancellationToken)).ConfigureAwait(false);

            if (twtRes.HasTimeout)
            {
                return new List<SeriesTimerInfo>();
            }

            return twtRes.Result;
        }

        public async Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        {
            // Retrieve the 'Pending' recordings

            int timeOut = await WaitForInitialLoadTask(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.GetTimersAsync: call cancelled or timed out - returning empty list");
                return new List<TimerInfo>();
            }

            TaskWithTimeoutRunner<IEnumerable<TimerInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<TimerInfo>>(_timeout);
            TaskWithTimeoutResult<IEnumerable<TimerInfo>> twtRes = await
                twtr.RunWithTimeout(_htsConnectionHandler.BuildPendingTimersInfos(cancellationToken)).ConfigureAwait(false);

            if (twtRes.HasTimeout)
            {
                return new List<TimerInfo>();
            }

            return twtRes.Result;
        }

        public Task ResetTuner(string id, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);

            int timeOut = await WaitForInitialLoadTask(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.UpdateSeriesTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage updateAutorecMessage = new HTSMessage();
            updateAutorecMessage.Method = "updateAutorecEntry";
            updateAutorecMessage.PutField("id", info.Id);
            BuildAutorecFields(updateAutorecMessage, info);

            await SendAutorecMessage(updateAutorecMessage, nameof(UpdateSeriesTimerAsync), cancellationToken).ConfigureAwait(false);
        }

        public async Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
        {
            int timeOut = await WaitForInitialLoadTask(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.UpdateTimerAsync: call cancelled or timed out");
                return;
            }

            HTSMessage updateTimerMessage = new HTSMessage();
            updateTimerMessage.Method = "updateDvrEntry";
            updateTimerMessage.PutField("id", updatedTimer.Id);
            updateTimerMessage.PutField("startExtra", (long)(updatedTimer.PrePaddingSeconds / 60));
            updateTimerMessage.PutField("stopExtra", (long)(updatedTimer.PostPaddingSeconds / 60));

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(_timeout);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Run(() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnectionHandler.SendMessage(updateTimerMessage, lbrh);
                LastRecordingChange = DateTime.UtcNow;
                return lbrh.GetResponse();
            })).ConfigureAwait(false);

            if (twtRes.HasTimeout)
            {
                _logger.LogError("LiveTvService.UpdateTimerAsync: can't update timer because the timeout was reached");
            }
            else
            {
                HTSMessage updateTimerResponse = twtRes.Result;
                bool success = updateTimerResponse.GetInt("success", 0) == 1;
                if (!success)
                {
                    if (updateTimerResponse.ContainsField("error"))
                    {
                        _logger.LogError("LiveTvService.UpdateTimerAsync: can't update timer: '{Why}'", updateTimerResponse.GetString("error"));
                    }
                    else if (updateTimerResponse.ContainsField("noaccess"))
                    {
                        _logger.LogError("LiveTvService.UpdateTimerAsync: can't update timer: '{Why}'", updateTimerResponse.GetString("noaccess"));
                    }
                }
            }
        }

        /***********/
        /* Helpers */
        /***********/

        private Task<int> WaitForInitialLoadTask(CancellationToken cancellationToken)
        {
            return Task.Run(() => _htsConnectionHandler.WaitForInitialLoad(cancellationToken), cancellationToken);
        }
    }
}
