using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Recordings;
using TVHeadEnd.Streaming;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd
{
    public class RecordingsChannel : IChannel, ISupportsDelete, ISupportsLatestMedia, IHasFolderAttributes, IRequiresMediaInfoCallback, IHasCacheKey
    {
        /// <summary>
        /// How many times the shape of a recording's media sources has changed since that floor.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ChannelManager rewrites a stored channel item only when the item is new or when
        /// ChannelItemInfo.DateModified is strictly later than the date it stored. It compares no
        /// part of MediaSources, and DataVersion does not help either -- that only discards the
        /// cached listing response, not the items already in the database. So an upgrade that
        /// changes how a recording is described has no way to reach the recordings somebody
        /// already has, and they keep whatever description the previous version gave them.
        /// </para>
        /// <para>
        /// What reaches them is an offset added to the recording's own anchor, not a date of its
        /// own -- see <see cref="PublishedDateFor"/> for how the two combine. For an unchanged
        /// recording the published date is greater than the stored value exactly once per
        /// increment, so each upgrade rewrites every item once and then leaves it alone; and it
        /// stays true however long after the release the plugin is installed, because it is
        /// measured from the recording rather than the calendar.
        /// </para>
        /// <para>
        /// Counted in whole days, because one increment has to clear how far short of its booking
        /// a recording fell as well as the seconds earlier versions stepped in. Raise it by one per
        /// change to the published shape.
        /// </para>
        /// </remarks>
        private const int MediaSourceSchemaRevision = 10;

        /// <summary>
        /// The floor every recording's modification date is lifted to.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It exists so that a recording TVHeadend has not touched in years still carries a date
        /// the schema revision can be counted from. It is not itself the revision, and it may only
        /// ever move <em>forward</em>: raising it raises the published date of every recording
        /// below it, which is what keeps those dates monotone; lowering it would drop them all at
        /// once and freeze every stored item.
        /// </para>
        /// <para>
        /// Moved once, from 2026-08-19, so that it sits above every date the schema-6 build
        /// published while it was briefly deployed. It carries nothing else: making a recording
        /// stopped early clear its own earlier publication is the anchor's job, not the floor's,
        /// and a floor could never have done it -- the shortfall is however long the recording had
        /// left to run, which no fixed date knows.
        /// </para>
        /// </remarks>
        private static readonly DateTime MediaSourceDateFloorUtc = new(2026, 8, 29, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Tells this run of the server apart from every other one.
        /// </summary>
        /// <remarks>
        /// The DVR revision counts changes since the connection was made, so it starts again at
        /// zero every time Jellyfin restarts -- while the channel cache it keys does not: that is
        /// written to disk and outlives the process. A restarted server would therefore ask for
        /// the listing under a key a previous run had already written, and be handed that run's
        /// recordings. Mixing in something that cannot repeat makes the first request after a
        /// restart a miss, which is the one time the cache is certainly stale.
        /// </remarks>
        private static readonly string ProcessEpoch = Guid.NewGuid().ToString("N");

        private readonly ILogger<LiveTvService> _logger;
        private readonly TvheadendConnection _connection;
        private readonly LiveTvService _liveTvService;
        private readonly IServerApplicationHost _applicationHost;
        private readonly ILibraryManager _libraryManager;
        private readonly RecordingAnalysisService _analysisService;

        public RecordingsChannel(
            ILoggerFactory loggerFactory,
            TvheadendConnection connection,
            LiveTvService liveTvService,
            RecordingAnalysisService analysisService,
            IServerApplicationHost applicationHost,
            ILibraryManager libraryManager)
        {
            _connection = connection;
            _libraryManager = libraryManager;
            _liveTvService = liveTvService;
            _analysisService = analysisService;
            _applicationHost = applicationHost;
            _logger = loggerFactory.CreateLogger<LiveTvService>();
            _logger.LogDebug("[TVHclient] RecordingsChannel()");
        }

        /// <summary>
        /// Gets the name this channel is registered under.
        /// </summary>
        /// <remarks>
        /// The single input to the identifier Jellyfin derives for the channel entity and writes
        /// onto every recording it stores, so it is stated once and shared -- changing it here
        /// alone would silently orphan every stored recording from the plugin that made it.
        /// </remarks>
        public string Name => Playback.TvheadendItems.RecordingsChannelName;

        public string Description
        {
            get
            {
                return "TVHeadEnd Recordings";
            }
        }

        [SuppressMessage(
            "Performance",
            "CA1819:Properties should not return arrays",
            Justification = "The array-typed property is mandated by MediaBrowser.Controller.Channels.IHasFolderAttributes.")]
        public string[] Attributes => ["Recordings"];

        /// <summary>
        /// Gets the version of this channel's contents. It forms part of the path Jellyfin caches
        /// a channel's listing under, so changing it discards that cache and the plugin is asked
        /// again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Derived from <see cref="MediaSourceSchemaRevision"/> rather than typed separately,
        /// because the two answer the same question and were getting different answers. A listing
        /// is cached for three hours under a path built from this string, and the cache key the
        /// channel supplies follows TVHeadend's recordings rather than the plugin -- so an upgrade
        /// that changed how a recording is described was invisible until the cache aged out,
        /// with nothing to say so. Measured: a listing cached at 18:34 was still being served at
        /// 21:29, two hours after the version that would have changed it was installed.
        /// </para>
        /// <para>
        /// One number now governs both halves of an upgrade reaching existing recordings: this
        /// discards the cached listing, and the published date rewrites the items already stored.
        /// </para>
        /// </remarks>
        public string DataVersion => "9." + MediaSourceSchemaRevision.ToString(CultureInfo.InvariantCulture);

        public string HomePageUrl
        {
            get { return "https://tvheadend.org"; }
        }

        public ChannelParentalRating ParentalRating
        {
            get { return ChannelParentalRating.GeneralAudience; }
        }

        /// <summary>
        /// Gets the key Jellyfin caches this channel's listing under.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only what actually changes the listing. This used to mix in the day, the hour and a
        /// five-minute bucket, which discarded the cache on a timer whether or not anything had
        /// happened; the recordings themselves change when TVHeadend says they do, and that is
        /// what the key follows.
        /// </para>
        /// <para>
        /// The signature matters as much as the value. It was <c>string</c> rather than
        /// <c>string?</c> and the class did not declare <see cref="IHasCacheKey"/>, so
        /// ChannelManager -- which reaches this only through that interface -- never called it at
        /// all, and every recording listing was cached under an empty key that nothing could
        /// invalidate.
        /// </para>
        /// </remarks>
        /// <param name="userId">The user the listing is for. Every user sees the same recordings.</param>
        /// <returns>The cache key.</returns>
        public string? GetCacheKey(string? userId)
            => ComposeCacheKey(ProcessEpoch, GetService().RecordingRevision);

        /// <summary>
        /// Builds the cache key from the two things that make a listing different.
        /// </summary>
        /// <param name="processEpoch">What tells this run of the server from any other.</param>
        /// <param name="recordingRevision">How many DVR changes TVHeadend has announced.</param>
        /// <returns>The cache key.</returns>
        internal static string ComposeCacheKey(string processEpoch, long recordingRevision)
            => processEpoch + "-" + recordingRevision.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Gets what kind of item a recording from this sort of channel is published as.
        /// </summary>
        /// <remarks>
        /// A radio recording published as video is a concert behind a black screen. It happened
        /// because the recording was never told what its channel carried and took the enum's
        /// default, which is TV -- see LiveTvService.GetRecordingsAsync for where it is told.
        /// </remarks>
        /// <param name="channelType">What the channel it was recorded from carries.</param>
        /// <returns>The media type to publish.</returns>
        internal static ChannelMediaType MediaTypeFor(MediaBrowser.Model.LiveTv.ChannelType channelType)
            => channelType == MediaBrowser.Model.LiveTv.ChannelType.Radio
                ? ChannelMediaType.Audio
                : ChannelMediaType.Video;

        public InternalChannelFeatures GetChannelFeatures()
        {
            return new InternalChannelFeatures
            {
                ContentTypes = new List<ChannelMediaContentType>
                 {
                      ChannelMediaContentType.Movie,
                      ChannelMediaContentType.Episode,
                      ChannelMediaContentType.Clip
                 },
                MediaTypes = new List<ChannelMediaType>
                  {
                       ChannelMediaType.Audio,
                       ChannelMediaType.Video
                  },
                SupportsContentDownloading = true
            };
        }

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            if (type == ImageType.Primary)
            {
                return Task.FromResult(new DynamicImageResponse
                {
                    Path = "https://raw.githubusercontent.com/jellyfin/jellyfin-ux/master/plugins/repository/jellyfin-plugin-tvheadend.png",
                    Protocol = MediaProtocol.Http,
                    HasImage = true
                });
            }

            return Task.FromResult(new DynamicImageResponse
            {
                HasImage = false
            });
        }

        public IEnumerable<ImageType> GetSupportedChannelImages()
        {
            return new List<ImageType>
            {
                 ImageType.Primary
            };
        }

        public bool IsEnabledFor(string userId)
        {
            return !Plugin.Instance.Configuration.HideRecordingsChannel;
        }

        private LiveTvService GetService() => _liveTvService;

        public async Task<IEnumerable<MyRecordingInfo>> GetAllRecordingsAsync(CancellationToken cancellationToken)
        {
            // Everything that has at least started. A scheduled entry has nothing to play yet,
            // and one whose file has gone would offer a recording that answers nothing.
            return await _liveTvService.GetRecordingsAsync(cancellationToken).ConfigureAwait(false);
        }

        public bool CanDelete(BaseItem item)
        {
            return !item.IsFolder;
        }

        public Task DeleteItem(string id, CancellationToken cancellationToken)
        {
            return GetService().DeleteRecordingAsync(id, cancellationToken);
        }

        public async Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
        {
            var result = await GetChannelItems(new InternalChannelItemQuery(), i => true, cancellationToken).ConfigureAwait(false);

            return result.Items.OrderByDescending(i => i.DateCreated ?? DateTime.MinValue);
        }

        public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query.FolderId))
            {
                return GetRecordingGroups(query, cancellationToken);
            }

            if (query.FolderId.StartsWith("series_", StringComparison.OrdinalIgnoreCase))
            {
                var hash = query.FolderId.Split('_')[1];
                return GetChannelItems(query, i => i.IsSeries && i.Name != null && string.Equals(i.Name.GetMD5().ToString("N"), hash, StringComparison.Ordinal), cancellationToken);
            }

            if (string.Equals(query.FolderId, "kids", StringComparison.OrdinalIgnoreCase))
            {
                return GetChannelItems(query, i => i.IsKids, cancellationToken);
            }

            if (string.Equals(query.FolderId, "movies", StringComparison.OrdinalIgnoreCase))
            {
                return GetChannelItems(query, i => i.IsMovie, cancellationToken);
            }

            if (string.Equals(query.FolderId, "news", StringComparison.OrdinalIgnoreCase))
            {
                return GetChannelItems(query, i => i.IsNews, cancellationToken);
            }

            if (string.Equals(query.FolderId, "sports", StringComparison.OrdinalIgnoreCase))
            {
                return GetChannelItems(query, i => i.IsSports, cancellationToken);
            }

            if (string.Equals(query.FolderId, "others", StringComparison.OrdinalIgnoreCase))
            {
                return GetChannelItems(query, i => !i.IsSports && !i.IsNews && !i.IsMovie && !i.IsKids && !i.IsSeries, cancellationToken);
            }

            var result = new ChannelItemResult()
            {
                Items = new List<ChannelItemInfo>()
            };

            return Task.FromResult(result);
        }

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, Func<MyRecordingInfo, bool> filter, CancellationToken cancellationToken)
        {
            _logger.LogDebug("[TVHclient] GetChannelItems - Updating TVHeadend Recording Items");

            var allRecordings = await GetAllRecordingsAsync(cancellationToken).ConfigureAwait(false);

            return new ChannelItemResult
            {
                Items = allRecordings.Where(filter).Select(ConvertToChannelItem).ToList()
            };
        }

        /// <summary>
        /// How long a recording runs, as one answer for the listing and the media source alike.
        /// </summary>
        /// <remarks>
        /// It used to be <c>EndDate - StartDate</c>, which is how long the recording was
        /// <em>scheduled</em> for. A recording stopped by hand was published at its planned length,
        /// so a client could seek into minutes that were never written. What is published now is
        /// measured from the file TVHeadend actually serves, and is absent -- not zero, not the
        /// plan -- while that file is still growing.
        /// </remarks>
        /// <param name="recording">The recording.</param>
        /// <returns>The runtime in ticks, or <see langword="null"/> where it is not knowable.</returns>
        internal static long? Runtime(MyRecordingInfo recording) => recording.RunTimeTicks;

        private ChannelItemInfo ConvertToChannelItem(MyRecordingInfo item)
        {
            _logger.LogDebug("[TVHclient] ConvertToChannelItem - Creating ChannelItemInfo");

            var channelItem = new ChannelItemInfo
            {
                Name = string.IsNullOrEmpty(item.EpisodeTitle) ? item.Name : item.EpisodeTitle,
                SeriesName = !string.IsNullOrEmpty(item.EpisodeTitle) ? item.Name : null,
                OfficialRating = item.OfficialRating,
                CommunityRating = item.CommunityRating,
                ContentType = ContentTypeFor(item),
                Genres = [.. item.Genres],
                ImageUrl = item.ImageUrl,
                Id = item.Id,
                MediaType = MediaTypeFor(item.ChannelType),
                IsLiveStream = false,

                // A placeholder, carrying no streams at all. The listing must not analyse the
                // recordings it lists -- that is one range request and one FFprobe run per
                // recording, on every listing -- and describing them from guesswork instead is
                // worse than saying nothing: Jellyfin maps streams by their position in this
                // list, so invented entries send FFmpeg's "-map" arguments to the wrong tracks.
                // What the recording contains is answered by GetChannelItemMediaInfo when
                // playback is negotiated. The Placeholder type is what tells Jellyfin this is
                // not a description it should act on; GetPlaybackMediaSources checks for exactly
                // that before it would otherwise force a remote probe of its own.
                MediaSources = [BuildPlaceholderSource(item)],

                // Stated on the item, because the source deliberately carries nothing. Without a
                // duration Jellyfin treats the recording as a stream of unknown length, which is
                // exactly right while it is still being written and exactly wrong once it is not.
                RunTimeTicks = Runtime(item),
                // ParentIndexNumber = item.ParentIndexNumber,
                PremiereDate = item.StartDate,
                DateCreated = item.StartDate,
                // Two reasons the stored item can be out of date: the recording itself changed,
                // and this plugin now describes it differently than the version that wrote the
                // stored copy. The date carries both -- TVHeadend's own, floored, plus one step
                // per description change since. Without the second an upgrade never reaches
                // recordings somebody already has.
                DateModified = PublishedDateFor(item),

                Overview = item.Overview,
                // People = item.People
                Etag = item.Status.ToString(),
            };

            return channelItem;
        }

        /// <summary>
        /// Describes what a recording contains, when Jellyfin negotiates playback for it.
        /// </summary>
        /// <remarks>
        /// This is where the analysis belongs. A listing would have to run one range request and
        /// one FFprobe over every recording it returns, on every listing, to answer a question
        /// only the recording being played actually raises.
        /// </remarks>
        /// <param name="id">The TVHeadend recording identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The media sources for the recording.</returns>
        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);

            // Looked up once. The identifier the source is addressed by, the runtime and whether
            // the analysis may be kept all come from the same recording, and three separate
            // lookups of it are three chances for them to disagree.
            var recording = await FindRecordingAsync(id, cancellationToken).ConfigureAwait(false);

            var source = BuildRecordingMediaSource(id, recording);

            // The address, not the name. Path is the virtual file the client is told about and is
            // always set; whether the recording can be reached at all is EncoderPath.
            if (string.IsNullOrEmpty(source.EncoderPath))
            {
                return [source];
            }

            // A recording still being written grows, and what a sample of its opening says about
            // it is not yet the whole truth -- an audio track the broadcaster adds later would be
            // missing from the description for as long as the server runs. A finished one is
            // finished, so the analysis of it holds.
            var finished = recording is not null
                && recording.Status != MediaBrowser.Model.LiveTv.RecordingStatus.InProgress;

            var analysis = await _analysisService.AnalyseAsync(id, finished, cancellationToken).ConfigureAwait(false);
            if (!RecordingDescriber.Describe(source, analysis))
            {
                // Undescribed, and deliberately still without invented streams. Jellyfin falls
                // back to what it can work out itself rather than being told something untrue.
                return [source];
            }

            source.RunTimeTicks = recording is null ? null : Runtime(recording);

            return [source];
        }

        /// <summary>
        /// Finds one recording among the ones TVHeadend holds.
        /// </summary>
        /// <param name="id">The TVHeadend recording identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The recording, or <see langword="null"/> if the server no longer lists it.</returns>
        private async Task<MyRecordingInfo?> FindRecordingAsync(string id, CancellationToken cancellationToken)
        {
            var recordings = await GetAllRecordingsAsync(cancellationToken).ConfigureAwait(false);
            return recordings.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        }

        /// <summary>
        /// The identifier a client sends back as MediaSourceId: the recording item's own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Jellyfin gives an ordinary library item a media source whose identifier <em>is</em> the
        /// item's -- <c>BaseItem.GetVersionInfo</c> writes <c>item.Id.ToString("N")</c> -- and
        /// clients are built on that. The native Android app, asked to play something it holds no
        /// media source for, sends the item identifier as the media source identifier; the server
        /// then keeps only the source that matches it. Measured: with any other identifier the
        /// response carries no sources and no play session, the app's resolver fails, and the
        /// screen stays black with no error anywhere.
        /// </para>
        /// <para>
        /// It also has to be readable as a GUID, which an item identifier is. Two places
        /// downstream parse it as one -- <c>DynamicHlsHelper.GetMasterPlaylistInternal</c>
        /// unconditionally, and <c>StreamingHelpers.GetStreamingState</c> when its lookup finds
        /// nothing. And it is the one GUID a saved source may carry:
        /// <c>MediaSourceManager.GetStaticMediaSources</c> discards a saved source whose
        /// identifier parses as a GUID unless it is the item's own, so the placeholder can share
        /// it rather than needing a second identity.
        /// </para>
        /// </remarks>
        /// <param name="libraryManager">Jellyfin's library, which owns the derivation.</param>
        /// <param name="recording">The recording, which decides the item type the identifier is derived with.</param>
        /// <returns>The media source identifier.</returns>
        internal static string RecordingMediaSourceId(ILibraryManager libraryManager, MyRecordingInfo recording)
        {
            ArgumentNullException.ThrowIfNull(recording);

            return Playback.TvheadendItems.RecordingItemId(
                    libraryManager,
                    recording.Id ?? string.Empty,
                    Playback.TvheadendItems.RecordingItemType(MediaTypeFor(recording.ChannelType), ContentTypeFor(recording)))
                .ToString("N", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// What kind of thing a recording is published as.
        /// </summary>
        /// <remarks>
        /// Read twice and therefore stated once: the channel item is published with it, and the
        /// item identifier is derived from it. Two spellings of this would be two different items.
        /// </remarks>
        /// <param name="recording">The recording.</param>
        /// <returns>The content type.</returns>
        internal static ChannelMediaContentType ContentTypeFor(MyRecordingInfo recording)
        {
            ArgumentNullException.ThrowIfNull(recording);

            if (recording.IsMovie)
            {
                return ChannelMediaContentType.Movie;
            }

            return recording.IsSeries ? ChannelMediaContentType.Episode : ChannelMediaContentType.Clip;
        }

        /// <summary>
        /// The modification date a recording is published with: a version marker, not a fact about
        /// the recording.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Jellyfin rewrites a stored channel item only when this is strictly greater than the
        /// value it holds, so it is the plugin's only way of reaching recordings somebody already
        /// has. That makes it a persistence version, and the two jobs cannot be done by one value:
        /// the recording's real activity time must be truthful -- see
        /// <see cref="MyRecordingInfo.DateLastUpdated"/> -- while this must only ever rise, even
        /// when the truth about a recording turns out to be earlier than what was published for it
        /// before.
        /// </para>
        /// <para>
        /// <c>max(real activity, floor) + revision days + state seconds</c>. The anchor follows
        /// what the recording actually did, so a real change always raises it and can never be
        /// masked. The scheduled stop is deliberately not in it: putting it there would make the
        /// anchor stop moving for every recording that ends below its booking, which is the whole
        /// population this correction is about.
        /// </para>
        /// <para>
        /// The revision step is a day, and that size is the point. Every earlier version published
        /// from the scheduled stop and stepped in seconds, so this has to clear however far short
        /// of its booking a recording fell -- an amount no fixed date can know in advance, but
        /// which is bounded by the length of the booking. A day covers any recording anybody
        /// makes. It is a constant offset rather than a fixed future date, so it lifts every
        /// recording equally and blocks nothing: a later real change still rises above it.
        /// </para>
        /// <para>
        /// The state step carries the one transition the anchor cannot. A server too old to send
        /// the file list gives the same anchor while recording and once completed, and that
        /// transition is exactly when the final runtime becomes known and has to be stored.
        /// </para>
        /// <para>
        /// Nothing here reads the clock or anything else that differs between runs: the same
        /// recording in the same state publishes the same value after a restart, on any machine.
        /// A value derived from the current time would be later than the stored date on every
        /// listing and rewrite every recording for ever.
        /// </para>
        /// </remarks>
        /// <param name="recording">The recording being published.</param>
        /// <returns>The date to publish.</returns>
        internal static DateTime PublishedDateFor(MyRecordingInfo recording)
        {
            ArgumentNullException.ThrowIfNull(recording);

            // What the recording did, or failing that when it was due to begin -- an entry with no
            // file has done nothing, and its scheduled start is the only thing left to hang on.
            var anchor = recording.DateLastUpdated ?? recording.StartDate;

            if (anchor < MediaSourceDateFloorUtc)
            {
                anchor = MediaSourceDateFloorUtc;
            }

            return anchor
                .AddDays(MediaSourceSchemaRevision)
                .AddSeconds(ProgressOrdinal(recording.Status));
        }

        /// <summary>
        /// How far through its life the recording is, as a step the published date can carry.
        /// </summary>
        /// <remarks>
        /// One second apiece, well inside the minute the schema revision moves in, so the two
        /// cannot run into one another.
        /// </remarks>
        /// <param name="status">The recording's status.</param>
        /// <returns>The step.</returns>
        private static int ProgressOrdinal(MediaBrowser.Model.LiveTv.RecordingStatus status) => status switch
        {
            MediaBrowser.Model.LiveTv.RecordingStatus.InProgress => 1,
            MediaBrowser.Model.LiveTv.RecordingStatus.Completed => 2,
            _ => 0,
        };

        /// <summary>
        /// The source a listing reports: a placeholder, standing for a recording nobody has asked
        /// to play yet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It carries no streams, because the listing does not analyse and must not guess.
        /// </para>
        /// <para>
        /// Its identifier must <b>not</b> be readable as a GUID, and that is not a stylistic
        /// choice. This is a saved source, and <c>MediaSourceManager.GetStaticMediaSources</c>
        /// keeps a saved source only when its identifier fails to parse as a GUID, or parses to
        /// the item's own identifier, or names a library item the user can see. A GUID derived
        /// from the recording is none of those, so the placeholder is discarded, the item is left
        /// with no static source at all, and <c>GetPlaybackMediaSources</c> throws on
        /// <c>mediaSources[0]</c> before any of this plugin is reached. Measured: every
        /// PlaybackInfo request answered 500 with an ArgumentOutOfRangeException.
        /// </para>
        /// <para>
        /// The described source keeps the GUID, because it is dynamic and that filter never sees
        /// it, and because two places downstream parse it as a GUID. The two identifiers are
        /// therefore different by necessity -- see <see cref="RecordingMediaSourceId"/> -- and
        /// Jellyfin arranges for that to be harmless by dropping every placeholder before playback
        /// is decided, so what a client is offered to name is always the described source.
        /// </para>
        /// </remarks>
        /// <param name="recording">The recording the listing is standing in for.</param>
        /// <returns>The placeholder source.</returns>
        private MediaSourceInfo BuildPlaceholderSource(MyRecordingInfo recording)
        {
            ArgumentNullException.ThrowIfNull(recording);

            return BuildPlaceholderSource(RecordingMediaSourceId(_libraryManager, recording));
        }

        /// <inheritdoc cref="BuildPlaceholderSource(MyRecordingInfo)"/>
        /// <param name="mediaSourceId">The recording item's identifier.</param>
        /// <returns>The placeholder source.</returns>
        internal static MediaSourceInfo BuildPlaceholderSource(string mediaSourceId)
        {
            ArgumentException.ThrowIfNullOrEmpty(mediaSourceId);

            return new MediaSourceInfo
            {
                Id = mediaSourceId,
                Type = MediaSourceType.Placeholder,
                Protocol = MediaProtocol.Http,

                // The starting assumption, which the analysis replaces with whatever the
                // recording turns out to be. Written under the one name this plugin gives
                // MPEG-TS rather than spelled out, so it cannot drift from the live path.
                Container = SourceContainer.TransportStream,
                MediaStreams = [],
            };
        }

        /// <summary>
        /// The source a recording is published as: a file to the client, an address to Jellyfin.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same split live TV uses. A client is told the plainest thing there is -- a whole,
        /// seekable file it may play as it stands -- while the server reaches the bytes over the
        /// proxy this plugin serves. <c>EncodingHelper.AttachMediaSourceInfo</c> prefers
        /// <c>EncoderPath</c> and <c>EncoderProtocol</c> whenever both are set, so
        /// <c>state.InputProtocol</c> becomes HTTP and a static request is answered by
        /// <c>GetStaticRemoteStreamResult</c>, which forwards the client's Range header upstream
        /// and returns the upstream status, Content-Range, Content-Length and Accept-Ranges
        /// unaltered. Seeking therefore works exactly as it did.
        /// </para>
        /// <para>
        /// Saying <c>File</c> is not decoration: <c>StreamBuilder.SortMediaSources</c> ranks a
        /// direct-played file above everything else, and this is what puts a recording and a
        /// channel on the same footing as any other item in the library.
        /// </para>
        /// </remarks>
        /// <param name="id">The TVHeadend recording identifier.</param>
        /// <param name="recording">
        /// The recording as TVHeadend lists it, which decides the item type the identifier is
        /// derived with. <see langword="null"/> when the server no longer lists it: the identifier
        /// is then derived as a plain video, which is what an unclassified recording is stored as.
        /// A recording the server has forgotten cannot be played anyway, so the fallback only has
        /// to be harmless.
        /// </param>
        private MediaSourceInfo BuildRecordingMediaSource(string id, MyRecordingInfo? recording)
            => BuildRecordingSource(
                id,
                recording is not null
                    ? RecordingMediaSourceId(_libraryManager, recording)
                    : Playback.TvheadendItems.RecordingItemId(_libraryManager, id, typeof(Video))
                        .ToString("N", CultureInfo.InvariantCulture),
                BuildRecordingUrl(id));

        /// <inheritdoc cref="BuildRecordingMediaSource"/>
        /// <param name="id">The TVHeadend recording identifier.</param>
        /// <param name="mediaSourceId">The recording item's identifier, which the source is addressed by.</param>
        /// <param name="url">The address this plugin serves the recording from.</param>
        /// <returns>The source.</returns>
        internal static MediaSourceInfo BuildRecordingSource(string id, string mediaSourceId, string url)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);
            ArgumentException.ThrowIfNullOrEmpty(mediaSourceId);

            return new MediaSourceInfo
            {
                Path = VirtualRecordingPath(id),
                Protocol = MediaProtocol.File,
                EncoderPath = url,
                EncoderProtocol = MediaProtocol.Http,
                Id = mediaSourceId,

                // Replaced by whatever the sample turns out to be. TVHeadend's DVR profile
                // decides the container, and a server on one of the WebTV profiles writes
                // Matroska, so this is a starting point rather than a claim.
                Container = SourceContainer.TransportStream,
                AnalyzeDurationMs = 2000,
                MediaStreams = [],
            };
        }

        /// <summary>
        /// The name a recording carries as a file, for a file nobody opens.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The recording lives on the TVHeadend server, so there is no local file to name and
        /// none is invented. Nothing on this server reads it: <c>MediaSourceInfo.Path</c> is a
        /// plain property, <c>StreamBuilder</c> never looks at it, and the one place that would
        /// have -- <c>AttachMediaSourceInfo</c> -- takes <c>EncoderPath</c> instead. It exists so
        /// that a source claiming to be a file says which file it means, in logs and in a
        /// playback report.
        /// </para>
        /// <para>
        /// Deliberately not shaped like a real path. A client configured for direct file access
        /// resolves what it is given against its own filesystem, and a plausible-looking path is
        /// exactly the one that could accidentally resolve to something else.
        /// </para>
        /// </remarks>
        private static string VirtualRecordingPath(string id)
            => "TVHeadend/Recordings/" + id;

        /// <summary>
        /// Builds the address Jellyfin fetches a recording from: this plugin's own endpoint, not
        /// TVHeadend's.
        /// </summary>
        /// <remarks>
        /// <para>
        /// TVHeadend drops the connection when FFmpeg seeks back to the start after analysing the
        /// stream, and Jellyfin has no way to tell FFmpeg not to. Serving the recording here
        /// turns every seek into a fresh request upstream, which TVHeadend answers reliably, and
        /// puts recordings where live TV already is.
        /// </para>
        /// <para>
        /// The address says nothing about the container, because at the point it is built nothing
        /// knows it: TVHeadend's DVR profile decides that, and the answer arrives with the
        /// analysis. The old <c>stream.ts</c> spelling claimed MPEG-TS of every recording,
        /// including the Matroska a WebTV profile writes.
        /// </para>
        /// </remarks>
        private string BuildRecordingUrl(string id)
        {
            try
            {
                var secret = Api.TvheadendAccessSecret.Ensure(_logger);
                return _applicationHost.GetApiUrlForLocalAccess().TrimEnd('/')
                    + Api.TvHeadendRecordingsController.StreamPathFor(Api.TvheadendAccessToken.Create(id, secret));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TVHclient] RecordingsChannel: could not build a playback path for recording {RecordingId}", id);
                return string.Empty;
            }
        }

        private async Task<ChannelItemResult> GetRecordingGroups(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            _logger.LogDebug("[TVHclient] GetRecordingGroups - Updateing TVHeadend Recording Items");

            var allRecordings = await GetAllRecordingsAsync(cancellationToken).ConfigureAwait(false);
            var result = new ChannelItemResult();
            var items = new List<ChannelItemInfo>();

            var series = allRecordings
                .Where(i => i.IsSeries)
                .ToLookup(i => i.Name, StringComparer.OrdinalIgnoreCase);

            items.AddRange(series.OrderBy(i => i.Key).Select(i => new ChannelItemInfo
            {
                Name = i.Key,
                FolderType = ChannelFolderType.Container,
                Id = "series_" + (i.Key ?? string.Empty).GetMD5().ToString("N"),
                Type = ChannelItemType.Folder,
                ImageUrl = i.First().ImageUrl
            }));

            var kids = allRecordings.FirstOrDefault(i => i.IsKids);

            if (kids != null)
            {
                items.Add(new ChannelItemInfo
                {
                    Name = "Kids",
                    FolderType = ChannelFolderType.Container,
                    Id = "kids",
                    Type = ChannelItemType.Folder,
                    ImageUrl = kids.ImageUrl
                });
            }

            var movies = allRecordings.FirstOrDefault(i => i.IsMovie);
            if (movies != null)
            {
                items.Add(new ChannelItemInfo
                {
                    Name = "Movies",
                    FolderType = ChannelFolderType.Container,
                    Id = "movies",
                    Type = ChannelItemType.Folder,
                    ImageUrl = movies.ImageUrl
                });
            }

            var news = allRecordings.FirstOrDefault(i => i.IsNews);
            if (news != null)
            {
                items.Add(new ChannelItemInfo
                {
                    Name = "News",
                    FolderType = ChannelFolderType.Container,
                    Id = "news",
                    Type = ChannelItemType.Folder,
                    ImageUrl = news.ImageUrl
                });
            }

            var sports = allRecordings.FirstOrDefault(i => i.IsSports);
            if (sports != null)
            {
                items.Add(new ChannelItemInfo
                {
                    Name = "Sports",
                    FolderType = ChannelFolderType.Container,
                    Id = "sports",
                    Type = ChannelItemType.Folder,
                    ImageUrl = sports.ImageUrl
                });
            }

            var other = allRecordings.FirstOrDefault(i => !i.IsSports && !i.IsNews && !i.IsMovie && !i.IsKids && !i.IsSeries);
            if (other != null)
            {
                items.Add(new ChannelItemInfo
                {
                    Name = "Others",
                    FolderType = ChannelFolderType.Container,
                    Id = "others",
                    Type = ChannelItemType.Folder,
                    ImageUrl = other.ImageUrl
                });
            }

            result.Items = items;
            return result;
        }
    }
}
