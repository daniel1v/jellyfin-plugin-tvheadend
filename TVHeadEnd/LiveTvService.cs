using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
using TVHeadEnd.Streaming;
using TVHeadEnd.TimeoutHelper;
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
        private readonly ConcurrentDictionary<string, TvHeadendHttpLiveStream> _activeChannelStreams = new(StringComparer.OrdinalIgnoreCase);

        // Channels measured to carry no IDR frames skip the detection phase and start their
        // re-encode immediately. Persisted, so the first tune after a restart is fast too;
        // the scan keeps running alongside the encoder, so a channel that starts sending IDR
        // frames drops out of the list again by itself.
        private readonly ConcurrentDictionary<string, bool> _channelRequiresReencode = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _verdictPersistenceLock = new();

        // Probe results, keyed by channel and validated against the PMT layout they were
        // taken from. Re-probing costs about a tenth of a second, but the buffering it needs
        // costs two full seconds on every channel change, and that is what this avoids.
        private readonly ConcurrentDictionary<string, CachedChannelProbe> _channelProbeCache = new(StringComparer.OrdinalIgnoreCase);

        private readonly ILogger<LiveTvService> _logger;

        private bool _verdictsLoaded;

        public LiveTvService(
            ILoggerFactory loggerFactory,
            IMediaEncoder mediaEncoder,
            HTSConnectionHandler connectionHandler,
            ILibraryManager libraryManager,
            IHttpClientFactory httpClientFactory,
            IConfigurationManager configurationManager,
            IServerApplicationHost applicationHost)
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

            // Added for stream probing
            _mediaEncoder = mediaEncoder;

            TvHeadendHttpLiveStream.RemoveOrphanedBuffers(_configurationManager, _logger);
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
            createAutorecMessage.PutField("configName", _htsConnectionHandler.GetProfile());

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
            createTimerMessage.PutField("configName", _htsConnectionHandler.GetProfile());
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

            return list;
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

        public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(
            string channelId,
            string streamId,
            List<ILiveStream> currentLiveStreams,
            CancellationToken cancellationToken)
        {
            var mediaSourceId = _liveTvItemIdResolver.GetInternalChannelId(Name, channelId);
            var reusableStream = TvHeadendHttpLiveStream.AcquireReusable(
                currentLiveStreams,
                streamId,
                mediaSourceId);
            if (reusableStream is not null)
            {
                if (reusableStream is TvHeadendHttpLiveStream reusableTvheadendStream)
                {
                    _activeChannelStreams[mediaSourceId] = reusableTvheadendStream;
                }

                _logger.LogInformation(
                    "Live TV stream reuse: managed stream {UniqueId} now has {ConsumerCount} consumers",
                    reusableStream.UniqueId,
                    reusableStream.ConsumerCount);
                return reusableStream;
            }

            var mediaSource = await CreateOpenedChannelMediaSource(channelId, cancellationToken).ConfigureAwait(false);
            var liveStream = new TvHeadendHttpLiveStream(
                mediaSource,
                _httpClientFactory,
                _configurationManager,
                _applicationHost,
                _logger,
                _mediaEncoder.EncoderPath,
                _htsConnectionHandler.GetReencodeWhenNoIdr(),
                _htsConnectionHandler.GetLiveBufferSizeMegabytes(),
                GetKnownChannelVerdict(channelId),
                requiresReencode => RememberChannelVerdict(channelId, requiresReencode),
                _channelProbeCache.TryGetValue(channelId, out var cachedProbe) ? cachedProbe.ProgramLayout : null)
            {
                OriginalStreamId = streamId,
            };

            try
            {
                await liveStream.Open(cancellationToken).ConfigureAwait(false);

                if (liveStream.MatchesCachedLayout && cachedProbe is not null)
                {
                    cachedProbe.ApplyTo(liveStream.MediaSource);
                    _logger.LogInformation(
                        "Live TV stream start: reused the probe of channel {ChannelId}; the broadcast still announces the same elementary streams",
                        channelId);
                }
                else
                {
                    await ProbeStream(liveStream.MediaSource, cancellationToken).ConfigureAwait(false);
                    if (liveStream.ProgramLayout is not null && !liveStream.IsReencoding)
                    {
                        _channelProbeCache[channelId] = CachedChannelProbe.From(liveStream.ProgramLayout, liveStream.MediaSource);
                    }
                }

                ApplyLiveStreamOverrides(liveStream.MediaSource);

                liveStream.MediaSource.SupportsDirectPlay = true;

                // A broadcast that signals random access with recovery points instead of IDR
                // frames -- the ARD network does, ZDF does not -- offers no synchronisation
                // sample to common device decoders, which then never emit a frame. When the
                // re-encode for such streams is switched off, record the fact so a report of
                // "audio but black picture" can be traced here.
                if (!liveStream.IsReencoding && !liveStream.HasSeenIdrFrame)
                {
                    _logger.LogWarning(
                        "Live TV stream start: channel {ChannelId} carries no IDR frames and re-encoding is disabled. Many device decoders cannot start this stream and show a black picture",
                        channelId);
                }

                _activeChannelStreams[mediaSourceId] = liveStream;
                return liveStream;
            }
            catch
            {
                await liveStream.Close().ConfigureAwait(false);
                liveStream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Builds the media source for a channel from a fresh access ticket. The source is not
        /// probed here: it describes the upstream TVHeadend URL, which the managed live stream
        /// consumes but no client ever sees. Probing it would open a second subscription to a
        /// channel that is about to be received anyway, and would describe the broadcast rather
        /// than what ends up in the buffer.
        /// </summary>
        private async Task<MediaSourceInfo> CreateOpenedChannelMediaSource(
            string channelId,
            CancellationToken cancellationToken)
        {
            var streamStartStopwatch = Stopwatch.StartNew();
            var mediaSourceId = _liveTvItemIdResolver.GetInternalChannelId(Name, channelId);
            var ticket = await _channelTicketHandler.GetTicket(channelId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Live TV stream start: access ticket ready for channel {ChannelId} after {ElapsedMilliseconds} ms",
                channelId,
                streamStartStopwatch.ElapsedMilliseconds);
            _logger.LogInformation(
                "Live TV stream start: HTSP channel mapping {ChannelId} -> {ChannelName}",
                channelId,
                _htsConnectionHandler.GetChannelName(channelId) ?? "<unknown>");

            MediaSourceInfo livetvasset;
            if (_htsConnectionHandler.GetEnableSubsMaudios())
            {
                _logger.LogInformation("Live TV stream start: support for live TV subtitles and multiple audio tracks is enabled");

                // Use HTTP basic auth in HTTP header instead of TVH ticketing system for authentication to allow the users to switch subs or audio tracks at any time
                livetvasset = LiveTvMediaSourceFactory.CreateOpened(
                    mediaSourceId,
                    _htsConnectionHandler.GetHttpBaseUrl() + ticket.Path);
                livetvasset.RequiredHttpHeaders = _htsConnectionHandler.GetHeaders();
            }
            else
            {
                livetvasset = LiveTvMediaSourceFactory.CreateOpened(
                    mediaSourceId,
                    _htsConnectionHandler.GetHttpBaseUrl() + ticket.Url);
            }

            _logger.LogInformation(
                "Live TV stream start: upstream source ready for channel {ChannelId} after {ElapsedMilliseconds} ms; awaiting the managed-buffer probe",
                channelId,
                streamStartStopwatch.ElapsedMilliseconds);

            return livetvasset;
        }

        private void ApplyLiveStreamOverrides(MediaSourceInfo mediaSource)
        {
            LiveTvMediaSourceFactory.PreferCompatibleAudioTrack(mediaSource);

            if (!_htsConnectionHandler.GetForceDeinterlace())
            {
                return;
            }

            _logger.LogInformation("Live TV stream start: force video deinterlacing for all channels and recordings is enabled");
            foreach (MediaStream stream in mediaSource.MediaStreams)
            {
                if (stream.Type == MediaStreamType.Video && stream.IsInterlaced == false)
                {
                    stream.IsInterlaced = true;
                }

                stream.RealFrameRate = 50.0F;
            }
        }

        /// <summary>
        /// Describes what the managed buffer actually contains. It reads the local buffer file,
        /// never the upstream channel, so it costs no TVHeadend subscription and reports the
        /// re-encoded video where a channel goes through the encoder.
        /// </summary>
        private async Task ProbeStream(MediaSourceInfo mediaSourceInfo, CancellationToken cancellationToken)
        {
            var probeStopwatch = Stopwatch.StartNew();
            _logger.LogInformation("Live TV stream probe: reading the managed buffer");

            MediaInfoRequest req = new MediaInfoRequest
            {
                MediaType = MediaBrowser.Model.Dlna.DlnaProfileType.Video,
                MediaSource = mediaSourceInfo,
                ExtractChapters = false,
            };

            var originalRuntime = mediaSourceInfo.RunTimeTicks;
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();
            MediaInfo info = await _mediaEncoder.GetMediaInfo(req, cancellationToken).ConfigureAwait(false);
            stopWatch.Stop();
            TimeSpan ts = stopWatch.Elapsed;
            string elapsedTime = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
            _logger.LogDebug("Probe RunTime {ElapsedTime}", elapsedTime);

            if (info != null)
            {
                var mediaStreams = info.MediaStreams ?? [];

                _logger.LogInformation(
                    "Live TV stream probe: completed after {ElapsedMilliseconds} ms ({MediaStreamCount} streams, {Container})",
                    probeStopwatch.ElapsedMilliseconds,
                    mediaStreams.Count,
                    info.Container);

                _logger.LogDebug("Probe returned:");

                mediaSourceInfo.Bitrate = info.Bitrate;
                _logger.LogDebug("        BitRate:                    {BitRate}", info.Bitrate);

                // Keep the container the factory advertised, unless the stream itself has
                // already declared one. The probe reports the normalised "ts" spelling, which
                // no longer matches the client profiles that only list "mpegts".
                _logger.LogDebug("        Container:                  {Container} (probe reported {ProbedContainer})", mediaSourceInfo.Container, info.Container);

                mediaSourceInfo.MediaStreams = mediaStreams;
                _logger.LogDebug("        MediaStreams:               ");
                LogMediaStreamList(mediaStreams, "                       ");

                mediaSourceInfo.RunTimeTicks = info.RunTimeTicks;
                _logger.LogDebug("        RunTimeTicks:               {RunTimeTicks}", info.RunTimeTicks);

                mediaSourceInfo.Size = info.Size;
                _logger.LogDebug("        Size:                       {Size}", info.Size);

                mediaSourceInfo.Timestamp = info.Timestamp;
                _logger.LogDebug("        Timestamp:                  {Timestamp}", info.Timestamp);

                mediaSourceInfo.Video3DFormat = info.Video3DFormat;
                _logger.LogDebug("        Video3DFormat:              {Video3DFormat}", info.Video3DFormat);

                mediaSourceInfo.VideoType = info.VideoType;
                _logger.LogDebug("        VideoType:                  {VideoType}", info.VideoType);

                mediaSourceInfo.SupportsDirectPlay = false;
                _logger.LogDebug("        SupportsDirectPlay:         {SupportsDirectPlay}", info.SupportsDirectPlay);

                mediaSourceInfo.SupportsDirectStream = true;
                _logger.LogDebug("        SupportsDirectStream:       {SupportsDirectStream}", info.SupportsDirectStream);

                mediaSourceInfo.SupportsTranscoding = true;
                _logger.LogDebug("        SupportsTranscoding:        {SupportsTranscoding}", info.SupportsTranscoding);

                // The plugin has retained the complete probe result, including real stream
                // indices. Prevent Jellyfin's cached live TV probe from reducing it to the
                // first video and first audio stream with unknown indices.
                mediaSourceInfo.SupportsProbing = false;

                mediaSourceInfo.DefaultSubtitleStreamIndex = null;
                _logger.LogDebug("        DefaultSubtitleStreamIndex: n/a");

                if (!originalRuntime.HasValue)
                {
                    mediaSourceInfo.RunTimeTicks = null;
                    _logger.LogDebug("        Original runtime:           n/a");
                }

                var audioStream = mediaStreams.FirstOrDefault(i => i.Type == MediaStreamType.Audio);
                if (audioStream == null || audioStream.Index == -1)
                {
                    mediaSourceInfo.DefaultAudioStreamIndex = null;
                    _logger.LogDebug("        DefaultAudioStreamIndex:    n/a");
                }
                else
                {
                    mediaSourceInfo.DefaultAudioStreamIndex = audioStream.Index;
                    _logger.LogDebug("        DefaultAudioStreamIndex:    '{DefaultAudioStreamIndex}'", info.DefaultAudioStreamIndex);
                }
            }
            else
            {
                _logger.LogError(
                    "Live TV stream probe: no media information after {ElapsedMilliseconds} ms",
                    probeStopwatch.ElapsedMilliseconds);
            }
        }

        private void LogMediaStreamList(IReadOnlyList<MediaStream> theList, string prefix)
        {
            foreach (MediaStream i in theList)
            {
                LogMediaStream(i, prefix);
            }
        }

        private void LogMediaStream(MediaStream ms, string prefix)
        {
            _logger.LogDebug("{Prefix}AspectRatio             {AspectRatio}", prefix, ms.AspectRatio);
            _logger.LogDebug("{Prefix}AverageFrameRate        {AverageFrameRate}", prefix, ms.AverageFrameRate);
            _logger.LogDebug("{Prefix}BitDepth                {BitDepth}", prefix, ms.BitDepth);
            _logger.LogDebug("{Prefix}BitRate                 {BitRate}", prefix, ms.BitRate);
            _logger.LogDebug("{Prefix}ChannelLayout           {ChannelLayout}", prefix, ms.ChannelLayout); // Object
            _logger.LogDebug("{Prefix}Channels                {Channels}", prefix, ms.Channels);
            _logger.LogDebug("{Prefix}Codec                   {Codec}", prefix, ms.Codec); // Object
            _logger.LogDebug("{Prefix}CodecTag                {CodecTag}", prefix, ms.CodecTag); // Object
            _logger.LogDebug("{Prefix}Comment                 {Comment}", prefix, ms.Comment);
            _logger.LogDebug("{Prefix}DeliveryMethod          {DeliveryMethod}", prefix, ms.DeliveryMethod); // Object
            _logger.LogDebug("{Prefix}DeliveryUrl             {DeliveryUrl}", prefix, ms.DeliveryUrl);
            // _logger.LogDebug("{Prefix}ExternalId              {ExternalId}", prefix, ms.ExternalId);
            _logger.LogDebug("{Prefix}Height                  {Height}", prefix, ms.Height);
            _logger.LogDebug("{Prefix}Index                   {Index}", prefix, ms.Index);
            _logger.LogDebug("{Prefix}IsAnamorphic            {IsAnamorphic}", prefix, ms.IsAnamorphic);
            _logger.LogDebug("{Prefix}IsDefault               {IsDefault}", prefix, ms.IsDefault);
            _logger.LogDebug("{Prefix}IsExternal              {IsExternal}", prefix, ms.IsExternal);
            _logger.LogDebug("{Prefix}IsExternalUrl           {IsExternalUrl}", prefix, ms.IsExternalUrl);
            _logger.LogDebug("{Prefix}IsForced                {IsForced}", prefix, ms.IsForced);
            _logger.LogDebug("{Prefix}IsInterlaced            {IsInterlaced}", prefix, ms.IsInterlaced);
            _logger.LogDebug("{Prefix}IsTextSubtitleStream    {IsTextSubtitleStream}", prefix, ms.IsTextSubtitleStream);
            _logger.LogDebug("{Prefix}Language                {Language}", prefix, ms.Language);
            _logger.LogDebug("{Prefix}Level                   {Level}", prefix, ms.Level);
            _logger.LogDebug("{Prefix}PacketLength            {PacketLength}", prefix, ms.PacketLength);
            _logger.LogDebug("{Prefix}Path                    {Path}", prefix, ms.Path);
            _logger.LogDebug("{Prefix}PixelFormat             {PixelFormat}", prefix, ms.PixelFormat);
            _logger.LogDebug("{Prefix}Profile                 {Profile}", prefix, ms.Profile);
            _logger.LogDebug("{Prefix}RealFrameRate           {RealFrameRate}", prefix, ms.RealFrameRate);
            _logger.LogDebug("{Prefix}RefFrames               {RefFrames}", prefix, ms.RefFrames);
            _logger.LogDebug("{Prefix}SampleRate              {SampleRate}", prefix, ms.SampleRate);
            _logger.LogDebug("{Prefix}Score                   {Score}", prefix, ms.Score);
            _logger.LogDebug("{Prefix}SupportsExternalStream  {SupportsExternalStream}", prefix, ms.SupportsExternalStream);
            _logger.LogDebug("{Prefix}Type                    {Type}", prefix, ms.Type); // Object
            _logger.LogDebug("{Prefix}Width                   {Width}", prefix, ms.Width);
            _logger.LogDebug("{Prefix}========================", prefix);
        }

        public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        {
            var mediaSourceId = _liveTvItemIdResolver.GetInternalChannelId(Name, channelId);
            if (_activeChannelStreams.TryGetValue(mediaSourceId, out var activeStream))
            {
                // A stream whose buffer has gone is worse than no stream at all: the client
                // would keep requesting a source that answers 404 instead of opening a fresh one.
                if (activeStream.EnableStreamSharing && activeStream.HasBuffer)
                {
                    _logger.LogInformation(
                        "Live TV playback negotiation: returning active direct-play source {MediaSourceId} for channel {ChannelId}",
                        mediaSourceId,
                        channelId);
                    return Task.FromResult<List<MediaSourceInfo>>([activeStream.MediaSource]);
                }

                _activeChannelStreams.TryRemove(mediaSourceId, out _);
            }

            _logger.LogInformation(
                "Live TV playback negotiation: pending source {MediaSourceId} ready for channel {ChannelId} ({ChannelName}); opening is required",
                mediaSourceId,
                channelId,
                _htsConnectionHandler.GetChannelName(channelId) ?? "<unknown>");

            return Task.FromResult<List<MediaSourceInfo>>([LiveTvMediaSourceFactory.CreatePending(mediaSourceId)]);
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

        /// <summary>
        /// Gets what an earlier tune measured about a channel, loading the persisted list on
        /// first use. Not in the constructor: this service is built before the plugin instance
        /// that holds the configuration exists.
        /// </summary>
        private bool? GetKnownChannelVerdict(string channelId)
        {
            lock (_verdictPersistenceLock)
            {
                if (!_verdictsLoaded)
                {
                    _verdictsLoaded = true;
                    foreach (var persisted in Plugin.Instance.Configuration.ChannelsWithoutIdr)
                    {
                        _channelRequiresReencode.TryAdd(persisted, true);
                    }
                }
            }

            return _channelRequiresReencode.TryGetValue(channelId, out var verdict) ? verdict : null;
        }

        /// <summary>
        /// Records whether a channel needs its video re-encoded, and persists the list when it
        /// changes so the next start does not have to measure again.
        /// </summary>
        private void RememberChannelVerdict(string channelId, bool requiresReencode)
        {
            if (_channelRequiresReencode.TryGetValue(channelId, out var previous) && previous == requiresReencode)
            {
                return;
            }

            _channelRequiresReencode[channelId] = requiresReencode;

            lock (_verdictPersistenceLock)
            {
                var configuration = Plugin.Instance.Configuration;
                var channels = _channelRequiresReencode
                    .Where(entry => entry.Value)
                    .Select(entry => entry.Key)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (channels.SequenceEqual(configuration.ChannelsWithoutIdr, StringComparer.Ordinal))
                {
                    return;
                }

                configuration.ChannelsWithoutIdr = channels;
                Plugin.Instance.SaveConfiguration();
            }

            if (requiresReencode)
            {
                _logger.LogInformation(
                    "Live TV stream start: channel {ChannelId} carries no IDR frames and will be re-encoded from now on",
                    channelId);
            }
            else
            {
                _logger.LogInformation(
                    "Live TV stream start: channel {ChannelId} carries IDR frames again and no longer needs re-encoding",
                    channelId);
            }
        }

        /// <summary>
        /// A probe result together with the PMT layout it was taken from.
        /// </summary>
        private sealed class CachedChannelProbe
        {
            private CachedChannelProbe(string programLayout, string serializedMediaStreams, string? container, int? bitrate)
            {
                ProgramLayout = programLayout;
                SerializedMediaStreams = serializedMediaStreams;
                Container = container;
                Bitrate = bitrate;
            }

            public string ProgramLayout { get; }

            private string SerializedMediaStreams { get; }

            private string? Container { get; }

            private int? Bitrate { get; }

            public static CachedChannelProbe From(string programLayout, MediaSourceInfo mediaSource)
            {
                // Held serialized so that every tune gets its own instances. The source
                // handed to Jellyfin is mutated afterwards -- the default audio track is
                // marked on it, and Jellyfin fills in localized display titles -- and two
                // viewers on one channel must not share those objects.
                return new CachedChannelProbe(
                    programLayout,
                    JsonSerializer.Serialize(mediaSource.MediaStreams),
                    mediaSource.Container,
                    mediaSource.Bitrate);
            }

            public void ApplyTo(MediaSourceInfo mediaSource)
            {
                mediaSource.MediaStreams = JsonSerializer.Deserialize<List<MediaStream>>(SerializedMediaStreams) ?? [];
                mediaSource.Container = Container;
                mediaSource.Bitrate = Bitrate;

                // The plugin keeps the full probe result including real stream indices, so
                // Jellyfin must not reduce it to its own cached live TV view.
                mediaSource.SupportsProbing = false;
                mediaSource.SupportsDirectStream = true;
                mediaSource.SupportsTranscoding = true;
                mediaSource.RunTimeTicks = null;
                mediaSource.DefaultSubtitleStreamIndex = null;
            }
        }
    }
}
