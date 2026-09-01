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
using TVHeadEnd.Compatibility.Jellyfin12;
using TVHeadEnd.Configuration;
using TVHeadEnd.LiveTv;
using TVHeadEnd.Recordings;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd
{
    public class RecordingsChannel : IChannel, ISupportsDelete, ISupportsLatestMedia, IHasFolderAttributes, IRequiresMediaInfoCallback, IHasCacheKey
    {
        /// <summary>
        /// Tells this run of the server apart from every other one.
        /// </summary>
        /// <remarks>
        /// The DVR revision counts changes since the connection was made, so it starts again at
        /// zero every time Jellyfin restarts -- while the channel cache it keys does not: that is
        /// written to disk and outlives the process. Mixing in something that cannot repeat makes
        /// the first request after a restart a miss, which is the one time the cache is stale.
        /// </remarks>
        private static readonly string ProcessEpoch = Guid.NewGuid().ToString("N");

        private readonly ILogger<RecordingsChannel> _logger;
        private readonly TvheadendRecordings _recordings;
        private readonly TvheadendDvr _dvr;
        private readonly RecordingMediaSourceFactory _sources;
        private readonly IPluginPreferencesSource _preferences;
        private readonly RecordingAnalysisService _analysisService;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingsChannel"/> class.
        /// </summary>
        /// <param name="recordings">What TVHeadend holds, described as recordings.</param>
        /// <param name="dvr">Where a deletion is sent.</param>
        /// <param name="sources">The media sources a recording is published with.</param>
        /// <param name="analysisService">What a sample of a recording contains.</param>
        /// <param name="preferences">Whether this channel is offered at all.</param>
        /// <param name="logger">The logger.</param>
        public RecordingsChannel(
            TvheadendRecordings recordings,
            TvheadendDvr dvr,
            RecordingMediaSourceFactory sources,
            RecordingAnalysisService analysisService,
            IPluginPreferencesSource preferences,
            ILogger<RecordingsChannel> logger)
        {
            _recordings = recordings;
            _dvr = dvr;
            _sources = sources;
            _analysisService = analysisService;
            _preferences = preferences;
            _logger = logger;
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
        /// Derived from <see cref="RecordingPublicationVersion.SchemaRevision"/> rather than typed separately,
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
        public string DataVersion => "9." + RecordingPublicationVersion.SchemaRevision.ToString(CultureInfo.InvariantCulture);

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
            => ComposeCacheKey(ProcessEpoch, _recordings.Revision);

        /// <summary>
        /// Builds the cache key from the two things that make a listing different.
        /// </summary>
        /// <param name="processEpoch">What tells this run of the server from any other.</param>
        /// <param name="recordingRevision">How many DVR changes TVHeadend has announced.</param>
        /// <returns>The cache key.</returns>
        internal static string ComposeCacheKey(string processEpoch, long recordingRevision)
            => processEpoch + "-" + recordingRevision.ToString(CultureInfo.InvariantCulture);

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
            // No picture of its own, and none borrowed. What used to be here was fetched at runtime
            // from the official plugin repository -- another project's artwork, on another
            // project's server, for a plugin that is no longer that one. Saying honestly that
            // there is no image is better than a link somebody else may move.
            return Task.FromResult(new DynamicImageResponse
            {
                HasImage = false,
            });
        }

        /// <inheritdoc />
        /// <remarks>
        /// None. The channel has no artwork of its own, and offering a slot it never fills makes
        /// Jellyfin ask for a picture that is not coming.
        /// </remarks>
        public IEnumerable<ImageType> GetSupportedChannelImages() => [];

        public bool IsEnabledFor(string userId)
        {
            return !_preferences.Current.HideRecordingsChannel;
        }

        public async Task<IEnumerable<MyRecordingInfo>> GetAllRecordingsAsync(CancellationToken cancellationToken)
        {
            // Everything that has at least started. A scheduled entry has nothing to play yet,
            // and one whose file has gone would offer a recording that answers nothing.
            return await _recordings.GetAllAsync(cancellationToken).ConfigureAwait(false);
        }

        public bool CanDelete(BaseItem item)
        {
            return !item.IsFolder;
        }

        public Task DeleteItem(string id, CancellationToken cancellationToken)
        {
            return _dvr.DeleteRecordingAsync(id, cancellationToken);
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
            _logger.LogDebug("TVHeadend recordings: listing the recordings for a folder");

            var allRecordings = await GetAllRecordingsAsync(cancellationToken).ConfigureAwait(false);

            return new ChannelItemResult
            {
                Items = allRecordings.Where(filter).Select(ConvertToChannelItem).ToList()
            };
        }

        private ChannelItemInfo ConvertToChannelItem(MyRecordingInfo item)
        {
            _logger.LogDebug("TVHeadend recordings: describing one recording as a channel item");

            return RecordingItemMapper.BuildChannelItem(item, _sources.PlaceholderFor(item));
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
            var recording = await _recordings.FindAsync(id, cancellationToken).ConfigureAwait(false);

            var source = _sources.SourceFor(id, recording);

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

            source.RunTimeTicks = recording is null ? null : RecordingItemMapper.Runtime(recording);

            return [source];
        }

        private async Task<ChannelItemResult> GetRecordingGroups(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            _logger.LogDebug("TVHeadend recordings: listing the folders recordings are grouped into");

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
