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
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
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
        private readonly StreamProfileValidationStore _profileValidation;

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
            StreamProfileValidationStore profileValidation)
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
            _analyzer = new ChannelMediaAnalyzer(new MediaInspector(mediaEncoder, _logger), _logger);
            _descriptors = descriptors;
            _profileValidation = profileValidation;
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
                    var stream = await OpenRole(channelId, StreamProfileRole.Native, nativeProfile, token)
                        .ConfigureAwait(false);
                    try
                    {
                        return await _analyzer.Analyze(
                            channelId,
                            nativeProfile,
                            stream.MediaPath,
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
        /// Lists what forms of a channel Jellyfin may choose between.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This must never cost a TVHeadend subscription: Jellyfin calls it during playback
        /// negotiation, and for every channel in a list. Everything it needs comes from what
        /// earlier tunes stored and from which stream profiles are configured.
        /// </para>
        /// <para>
        /// The broadcast is always offered, and always first. A compatibility rendering is added
        /// only where it could actually help -- an MPEG-2 broadcast, and a configured profile to
        /// render it with. Which of them a client ends up with is Jellyfin's decision, made
        /// against the device profile the client sent; nothing here inspects the caller.
        /// </para>
        /// </remarks>
        /// <param name="channelId">The channel.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The sources on offer, the broadcast first.</returns>
        public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        {
            var profiles = _htsConnectionHandler.GetStreamProfiles();
            var nativeProfile = profiles.GetProfileName(StreamProfileRole.Native);
            var native = _descriptors.Get(channelId, nativeProfile);

            var sources = new List<MediaSourceInfo>
            {
                JellyfinMediaSourceMapper.CreatePending(channelId, StreamProfileRole.Native, native),
            };

            if (native is { IsMpeg2Video: true } && profiles.IsUsable(StreamProfileRole.Mpeg2H264Compatibility))
            {
                sources.Add(JellyfinMediaSourceMapper.CreatePending(
                    channelId,
                    StreamProfileRole.Mpeg2H264Compatibility,
                    JellyfinMediaSourceMapper.ProjectCompatibility(native)));
            }

            _logger.LogInformation(
                "Live TV playback negotiation: channel {ChannelId} ({ChannelName}) offers {Offers}",
                channelId,
                _htsConnectionHandler.GetChannelName(channelId) ?? "<unknown>",
                string.Join(", ", sources.Select(source => source.Name)));

            return Task.FromResult(sources);
        }

        /// <summary>
        /// Opens the form of a channel Jellyfin selected against the client's device profile.
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

            // A client that chose a source sends its identifier back. Anything else -- including
            // a client that names the channel's own item identifier, which no source carries --
            // gets the broadcast.
            var role = ChannelSourceId.Resolve(channelId, streamId) ?? StreamProfileRole.Native;

            var reusable = currentLiveStreams
                .OfType<ITvheadendStream>()
                .FirstOrDefault(stream => CanBeReusedFor(stream, channelId, role));
            if (reusable is not null)
            {
                reusable.ConsumerCount++;
                _logger.LogInformation(
                    "Live TV stream reuse: {Role} of channel {ChannelId} now has {ConsumerCount} consumers",
                    reusable.Role,
                    channelId,
                    reusable.ConsumerCount);

                // Rebuilt for every request. Jellyfin writes its verdict back onto the source it
                // evaluated, and a shared stream hands the same object to the next caller.
                PublishOpenedStream(channelId, role, reusable, _descriptors.Get(channelId, nativeProfile), streamId);
                return reusable;
            }

            var stopwatch = Stopwatch.StartNew();
            var stream = await OpenRole(channelId, role, nativeProfile, cancellationToken).ConfigureAwait(false);
            var openedAt = stopwatch.ElapsedMilliseconds;

            try
            {
                var descriptor = await DescribeOpenedStream(
                    channelId,
                    role,
                    nativeProfile,
                    stream,
                    cancellationToken).ConfigureAwait(false);

                // A compatibility profile that does not keep its promise is never published: the
                // client would be handed something no better than the broadcast, described as
                // something else. It is marked broken so the role stops being offered, and the
                // failure surfaces as an ordinary open failure for Jellyfin to fall back from.
                if (role != StreamProfileRole.Native
                    && !JellyfinMediaSourceMapper.SatisfiesContract(role, descriptor))
                {
                    _logger.LogWarning(
                        "Live TV stream start: the TVHeadend profile for {Role} produced {Container}/{Codec}, which does not satisfy the role. It will not be offered again until the profile is corrected",
                        role,
                        descriptor?.Container ?? "<unknown>",
                        descriptor?.VideoCodec ?? "<unknown>");

                    RecordProfileValidation(profiles, role, false, "the output did not satisfy the role");
                    await stream.DisposeAsync().ConfigureAwait(false);

                    throw new InvalidOperationException(FormattableString.Invariant(
                        $"The TVHeadend profile for {role} did not produce what the role promises."));
                }

                if (role != StreamProfileRole.Native)
                {
                    RecordProfileValidation(profiles, role, true, null);
                }

                PublishOpenedStream(channelId, role, stream, descriptor, streamId);
                stream.OriginalStreamId = streamId;

                _logger.LogInformation(
                    "Live TV stream start: channel {ChannelId} handed to Jellyfin after {ElapsedMilliseconds} ms as {Role} (opening took {OpenMilliseconds} ms)",
                    channelId,
                    stopwatch.ElapsedMilliseconds,
                    role,
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
        /// Opens one form of a channel, reading the TVHeadend profile its role names.
        /// </summary>
        /// <remarks>
        /// The two roles are served by different machinery on purpose. The broadcast is shared,
        /// long running and joined mid-flight, so it is conditioned into a ring buffer. A
        /// compatibility rendering is made for the session that asked for it and begins at the
        /// first byte of a transcoder started for that session, so it is spooled and served as it
        /// arrives.
        /// </remarks>
        private async Task<ITvheadendStream> OpenRole(
            string channelId,
            StreamProfileRole role,
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

            var upstreamUrl = endpoint.CreateTicketedStreamUrl(ticket.Url, profiles.GetProfileName(role));
            var spoolPath = Path.Combine(_bufferDirectory, $"tvheadend-{Guid.NewGuid():N}");

            if (role == StreamProfileRole.Native)
            {
                var broadcast = new TvheadendLiveStream(
                    channelId,
                    role,
                    upstreamUrl,
                    endpoint.CreateHeaders(),
                    JellyfinMediaSourceMapper.CreatePending(channelId, role, null),
                    spoolPath,
                    _htsConnectionHandler.GetLiveBufferSizeMegabytes(),
                    _descriptors.Get(channelId, nativeProfile) is not null,
                    _httpClientFactory,
                    _logger);

                await broadcast.Open(cancellationToken).ConfigureAwait(false);
                return broadcast;
            }

            var rendering = new CompatibilityLiveStream(
                channelId,
                role,
                CompatibilityContainer.Matroska,
                upstreamUrl,
                endpoint.CreateHeaders(),
                JellyfinMediaSourceMapper.CreatePending(channelId, role, null),
                spoolPath,
                _httpClientFactory,
                _logger);

            await rendering.Open(cancellationToken).ConfigureAwait(false);
            return rendering;
        }

        /// <summary>
        /// Establishes what an opened stream contains.
        /// </summary>
        /// <remarks>
        /// The broadcast is described once and the description kept: an unchanged PMT means the
        /// same elementary streams, and re-deriving that on every tune costs seconds for nothing.
        /// A compatibility rendering is inspected every time, because what it is depends on a
        /// TVHeadend profile this plugin does not control and cannot see.
        /// </remarks>
        private async Task<ChannelMediaDescriptor?> DescribeOpenedStream(
            string channelId,
            StreamProfileRole role,
            string? nativeProfile,
            ITvheadendStream stream,
            CancellationToken cancellationToken)
        {
            if (role != StreamProfileRole.Native)
            {
                return await _analyzer.Analyze(
                    channelId,
                    nativeProfile,
                    stream.MediaPath,
                    stream.Observation,
                    cancellationToken).ConfigureAwait(false);
            }

            var observation = stream.Observation;
            var stored = _descriptors.Get(channelId, nativeProfile);
            if (stored is not null && stored.MatchesProgram(observation.ProgramSignature))
            {
                _logger.LogInformation(
                    "Live TV stream start: reused the stored description of channel {ChannelId}; the broadcast still announces the same elementary streams",
                    channelId);

                return stored;
            }

            var descriptor = await _analyzer.Analyze(
                channelId,
                nativeProfile,
                stream.MediaPath,
                observation,
                cancellationToken).ConfigureAwait(false);
            if (descriptor is not null)
            {
                _descriptors.Record(descriptor);
            }

            return descriptor;
        }

        /// <summary>
        /// Points the opened stream's media source at what serves it.
        /// </summary>
        /// <remarks>
        /// The source keeps the identifier the caller opened it with. Handing back a different
        /// one leaves the client asking for a source that no later negotiation lists, and
        /// Jellyfin answers that by dereferencing null while preparing the stream.
        /// </remarks>
        private void PublishOpenedStream(
            string channelId,
            StreamProfileRole role,
            ITvheadendStream stream,
            ChannelMediaDescriptor? descriptor,
            string requestedId)
        {
            var container = stream is CompatibilityLiveStream compatibility
                ? compatibility.Container
                : CompatibilityContainer.TransportStream;

            stream.MediaSource = JellyfinMediaSourceMapper.CreateOpened(
                channelId,
                role,
                descriptor,
                stream.MediaPath,
                _applicationHost.GetApiUrlForLocalAccess().TrimEnd('/')
                    + "/LiveTv/LiveStreamFiles/"
                    + stream.UniqueId
                    + "/stream."
                    + container,
                container);

            if (!string.IsNullOrEmpty(requestedId))
            {
                stream.MediaSource.Id = requestedId;
            }

            LogPublishedSource(channelId, role, stream.MediaSource);
        }

        private void RecordProfileValidation(
            TvheadendStreamProfiles profiles,
            StreamProfileRole role,
            bool satisfies,
            string? detail)
        {
            profiles.RecordValidation(role, satisfies, detail);
            _profileValidation.Record(role, profiles.GetProfileName(role), satisfies);
        }

        /// <summary>
        /// Writes down the finished media source, exactly as Jellyfin will evaluate it.
        /// </summary>
        /// <remarks>
        /// Every wrong playback decision so far has come from a field that was not what it was
        /// believed to be. This prints the finished thing once, so the next disagreement between
        /// what was intended and what was published is a line in the log rather than an
        /// investigation. TVHeadend URLs and credentials are not part of it.
        /// </remarks>
        private void LogPublishedSource(string channelId, StreamProfileRole role, MediaSourceInfo source)
        {
            if (!_logger.IsEnabled(LogLevel.Debug))
            {
                return;
            }

            var video = source.MediaStreams?.FirstOrDefault(stream => stream.Type == MediaStreamType.Video);
            var audio = source.MediaStreams?.Where(stream => stream.Type == MediaStreamType.Audio).ToList() ?? [];
            var subtitles = source.MediaStreams?.Where(stream => stream.Type == MediaStreamType.Subtitle).ToList() ?? [];

            _logger.LogDebug(
                "Live TV published source: channel {ChannelId} {Role} id={SourceId} container={Container} "
                + "protocol={Protocol} path={Path} infinite={IsInfiniteStream} requiresOpening={RequiresOpening} "
                + "directPlay={SupportsDirectPlay} directStream={SupportsDirectStream} transcode={SupportsTranscoding} "
                + "video={VideoCodec} {VideoProfile}@{VideoLevel} {Width}x{Height} {FrameRate}fps interlaced={Interlaced} "
                + "audio=[{AudioCodecs}] defaultAudio={DefaultAudio} subtitles=[{SubtitleCodecs}]",
                channelId,
                role,
                source.Id,
                source.Container ?? "<none>",
                source.Protocol,
                DescribePath(source),
                source.IsInfiniteStream,
                source.RequiresOpening,
                source.SupportsDirectPlay,
                source.SupportsDirectStream,
                source.SupportsTranscoding,
                video?.Codec ?? "<none>",
                video?.Profile ?? "<none>",
                video?.Level?.ToString(CultureInfo.InvariantCulture) ?? "<none>",
                video?.Width?.ToString(CultureInfo.InvariantCulture) ?? "?",
                video?.Height?.ToString(CultureInfo.InvariantCulture) ?? "?",
                video?.RealFrameRate?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?",
                video?.IsInterlaced,
                string.Join(", ", audio.Select(stream => $"{stream.Index}:{stream.Codec}/{stream.Language}")),
                source.DefaultAudioStreamIndex?.ToString(CultureInfo.InvariantCulture) ?? "<none>",
                string.Join(", ", subtitles.Select(stream => $"{stream.Index}:{stream.Codec}")));
        }

        /// <summary>
        /// Describes where a source reads from without disclosing how to reach TVHeadend.
        /// </summary>
        private static string DescribePath(MediaSourceInfo source)
        {
            if (source.Protocol == MediaProtocol.File)
            {
                return Path.GetFileName(source.Path) ?? "<none>";
            }

            var path = source.Path;
            if (string.IsNullOrEmpty(path))
            {
                return "<none>";
            }

            var route = path.IndexOf("/LiveTv/", StringComparison.OrdinalIgnoreCase);
            return route >= 0 ? path[route..] : "<withheld>";
        }

        /// <summary>
        /// Reports whether an open stream can serve a request for a channel in a given form.
        /// </summary>
        /// <remarks>
        /// Only the broadcast is ever shared, and only with a request for the broadcast of the
        /// same channel. A compatibility rendering answers no, whoever asks: it was made for one
        /// session, a second one would arrive in the middle of a container whose header it never
        /// saw, and closing either would take the stream away from both.
        /// </remarks>
        /// <param name="stream">The open stream.</param>
        /// <param name="channelId">The channel being requested.</param>
        /// <param name="role">The form being requested.</param>
        /// <returns>Whether the stream may be shared.</returns>
        internal static bool CanBeReusedFor(ITvheadendStream stream, string channelId, StreamProfileRole role)
        {
            ArgumentNullException.ThrowIfNull(stream);

            return stream is TvheadendLiveStream { EnableStreamSharing: true, HasBuffer: true }
                && role == StreamProfileRole.Native
                && string.Equals(stream.ChannelId, channelId, StringComparison.OrdinalIgnoreCase)
                && stream.Role == role;
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
            // What an earlier run proved still counts. Without this the transitional encoder,
            // which stands down only for a proven profile, would return on every restart.
            foreach (var proven in _profileValidation.Load())
            {
                if (Enum.TryParse<StreamProfileRole>(proven.Key, out var role))
                {
                    profiles.RestoreValidation(role, proven.Value);
                }
            }

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
