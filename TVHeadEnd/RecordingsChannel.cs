using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Streaming;
using TVHeadEnd.TimeoutHelper;

namespace TVHeadEnd
{
    public class RecordingsChannel : IChannel, ISupportsDelete, ISupportsLatestMedia, IHasFolderAttributes
    {
        /// <summary>
        /// How much of a recording is fetched to analyse it. The program tables and a sample of
        /// every elementary stream sit at the very front; this is generous for that and still a
        /// tenth of a second over a local network.
        /// </summary>
        private const int AnalysisSampleLength = 8 * 1024 * 1024;

        private readonly TimeSpan _timeout = TimeSpan.FromMinutes(5);
        private readonly ILogger<LiveTvService> _logger;
        private readonly HTSConnectionHandler _htsConnectionHandler;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SourceDescriber _sourceDescriber;

        // A finished recording never changes, so what an analysis found holds for as long as the
        // server runs. Without this every listing of a folder would analyse its contents again.
        private readonly ConcurrentDictionary<string, MediaSourceInfo> _describedRecordings = new(StringComparer.OrdinalIgnoreCase);

        public RecordingsChannel(
            ILoggerFactory loggerFactory,
            HTSConnectionHandler htsConnectionHandler,
            IMediaEncoder mediaEncoder,
            IHttpClientFactory httpClientFactory)
        {
            _htsConnectionHandler = htsConnectionHandler;
            _httpClientFactory = httpClientFactory;
            _logger = loggerFactory.CreateLogger<LiveTvService>();
            _sourceDescriber = new SourceDescriber(mediaEncoder, _logger);
            _logger.LogDebug("[TVHclient] RecordingsChannel()");
        }

        public string Name
        {
            get
            {
                return "TVHeadEnd Recordings";
            }
        }

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
        /// Gets the version of this channel's contents. Jellyfin keys its stored channel items on
        /// this, so raising it once rebuilds them -- which is what clears the placeholder media
        /// source that earlier versions of this plugin saved onto every recording.
        /// </summary>
        public string DataVersion => "2";

        public string HomePageUrl
        {
            get { return "https://tvheadend.org"; }
        }

        public ChannelParentalRating ParentalRating
        {
            get { return ChannelParentalRating.GeneralAudience; }
        }

        public string GetCacheKey(string userId)
        {
            var now = DateTime.UtcNow;

            var values = new List<string>();

            values.Add(now.DayOfYear.ToString(CultureInfo.InvariantCulture));
            values.Add(now.Hour.ToString(CultureInfo.InvariantCulture));

            double minute = now.Minute;
            minute /= 5;

            values.Add(Math.Floor(minute).ToString(CultureInfo.InvariantCulture));

            values.Add(GetService().LastRecordingChange.Ticks.ToString(CultureInfo.InvariantCulture));

            return string.Join("-", values.ToArray());
        }

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

        private LiveTvService GetService()
        {
            return _htsConnectionHandler.GetLiveTvService()
                ?? throw new InvalidOperationException("The TVHeadend LiveTvService has not been registered yet");
        }

        private Task<int> WaitForInitialLoadTask(CancellationToken cancellationToken)
        {
            return Task.Run(() => _htsConnectionHandler.WaitForInitialLoad(cancellationToken), cancellationToken);
        }

        public async Task<IEnumerable<MyRecordingInfo>> GetAllRecordingsAsync(CancellationToken cancellationToken)
        {
            // retrieve all 'Pending', 'Inprogress' and 'Completed' recordings
            // we don't deliver the 'Pending' recordings

            int timeOut = await WaitForInitialLoadTask(cancellationToken).ConfigureAwait(false);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("[TVHclient] GetAllRecordingsAsync - Not initialized ");
                return [];
            }

            TaskWithTimeoutRunner<IEnumerable<MyRecordingInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<MyRecordingInfo>>(_timeout);
            TaskWithTimeoutResult<IEnumerable<MyRecordingInfo>> twtRes = await
                twtr.RunWithTimeout(_htsConnectionHandler.BuildDvrInfos(cancellationToken)).ConfigureAwait(false);

            if (twtRes.HasTimeout)
            {
                _logger.LogDebug("[TVHclient] GetAllRecordingsAsync - Timeout");
                return [];
            }

            return twtRes.Result;
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
            var selected = allRecordings.Where(filter).ToList();

            // Described here rather than on demand. Jellyfin looks a media source up by its
            // identifier among the sources stored with the item, and only what a listing reported
            // is stored -- a source handed over later through IRequiresMediaInfoCallback is never
            // found, and the request fails before playback starts.
            var sources = await DescribeRecordings(selected, cancellationToken).ConfigureAwait(false);

            return new ChannelItemResult
            {
                Items = selected.Select(info => ConvertToChannelItem(info, sources[info.Id ?? string.Empty])).ToList()
            };
        }

        /// <summary>
        /// Describes every recording in the listing, reusing what is already known. A finished
        /// recording never changes, so an analysis of one holds for as long as the server runs.
        /// </summary>
        private async Task<Dictionary<string, MediaSourceInfo>> DescribeRecordings(
            IReadOnlyList<MyRecordingInfo> recordings,
            CancellationToken cancellationToken)
        {
            var described = new Dictionary<string, MediaSourceInfo>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<MyRecordingInfo>();

            foreach (var recording in recordings)
            {
                var id = recording.Id ?? string.Empty;
                if (_describedRecordings.TryGetValue(id, out var cached))
                {
                    described[id] = cached;
                }
                else if (!described.ContainsKey(id))
                {
                    described[id] = BuildRecordingMediaSource(id);
                    pending.Add(recording);
                }
            }

            if (pending.Count == 0)
            {
                return described;
            }

            var stopwatch = Stopwatch.StartNew();

            // A handful at a time: each one is a range request plus an analysis of a few
            // megabytes, and a full listing would otherwise be as slow as it is long.
            using var concurrency = new SemaphoreSlim(4);
            await Task.WhenAll(pending.Select(async recording =>
            {
                var id = recording.Id ?? string.Empty;
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var source = described[id];
                    if (await DescribeRecording(id, source, cancellationToken).ConfigureAwait(false))
                    {
                        source.RunTimeTicks = Runtime(recording);
                        _describedRecordings[id] = source;
                    }
                }
                finally
                {
                    concurrency.Release();
                }
            })).ConfigureAwait(false);

            _logger.LogInformation(
                "TVHeadend recordings: analysed {Count} of {Total} in {ElapsedMilliseconds} ms",
                pending.Count,
                recordings.Count,
                stopwatch.ElapsedMilliseconds);

            return described;
        }

        private static long? Runtime(MyRecordingInfo recording)
            => recording.EndDate > recording.StartDate ? (recording.EndDate - recording.StartDate).Ticks : null;

        private ChannelItemInfo ConvertToChannelItem(MyRecordingInfo item, MediaSourceInfo source)
        {
            _logger.LogDebug("[TVHclient] ConvertToChannelItem - Creating ChannelItemInfo");

            var channelItem = new ChannelItemInfo
            {
                Name = string.IsNullOrEmpty(item.EpisodeTitle) ? item.Name : item.EpisodeTitle,
                SeriesName = !string.IsNullOrEmpty(item.EpisodeTitle) ? item.Name : null,
                OfficialRating = item.OfficialRating,
                CommunityRating = item.CommunityRating,
                ContentType = item.IsMovie ? ChannelMediaContentType.Movie : (item.IsSeries ? ChannelMediaContentType.Episode : ChannelMediaContentType.Clip),
                Genres = [.. item.Genres],
                ImageUrl = item.ImageUrl,
                Id = item.Id,
                MediaType = item.ChannelType == MediaBrowser.Model.LiveTv.ChannelType.TV ? ChannelMediaType.Video : ChannelMediaType.Audio,
                IsLiveStream = false,
                MediaSources = [source],
                // ParentIndexNumber = item.ParentIndexNumber,
                PremiereDate = item.StartDate,
                DateCreated = item.StartDate,
                StartDate = item.StartDate,
                EndDate = item.EndDate,
                // ProductionYear = item.ProductionYear,
                // Studios = item.Studios,
                Type = ChannelItemType.Media,
                DateModified = item.DateLastUpdated,
                Overview = item.Overview,
                // People = item.People
                Etag = item.Status.ToString()
            };

            return channelItem;
        }

        /// <summary>
        /// Fills in what a recording contains, from a sample of its opening.
        /// </summary>
        /// <remarks>
        /// Reading a sample rather than the recording is what makes describing every listed
        /// recording affordable. TVHeadend answers range requests but does not advertise
        /// Accept-Ranges, so FFmpeg treats a recording as an unseekable stream and reads it from
        /// end to end -- measured at 68.6 seconds for an 8 GB recording against 0.14 for the
        /// sample.
        /// </remarks>
        /// <returns><see langword="true"/> when the sample described the recording.</returns>
        private async Task<bool> DescribeRecording(string id, MediaSourceInfo source, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(source.Path))
            {
                return false;
            }

            var sample = Path.Combine(Path.GetTempPath(), $"tvheadend-analysis-{Guid.NewGuid():N}.ts");
            try
            {
                await FetchAnalysisSample(source.Path, sample, cancellationToken).ConfigureAwait(false);
                return await _sourceDescriber
                    .DescribeFromSample(source, sample, $"recording {id}", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // The placeholder the source was built with stands, so the recording behaves as
                // it did before rather than failing outright.
                _logger.LogError(exception, "TVHeadend recording {RecordingId} could not be analysed", id);
                return false;
            }
            finally
            {
                try
                {
                    File.Delete(sample);
                }
                catch (IOException)
                {
                    // Left behind in the temporary directory; harmless.
                }
            }
        }

        private static MediaSourceInfo BuildPlaceholderStreams(MediaSourceInfo source)
        {
            source.MediaStreams =
            [
                new MediaStream { Type = MediaStreamType.Video, Index = -1, IsInterlaced = true, RealFrameRate = 50.0F },
                new MediaStream { Type = MediaStreamType.Audio, Index = -1 },
            ];
            return source;
        }

        /// <summary>
        /// Copies the opening of the recording to a local file, which is seekable and therefore
        /// analysable in a fraction of the time the recording itself would take.
        /// </summary>
        private async Task FetchAnalysisSample(string url, string destination, CancellationToken cancellationToken)
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, AnalysisSampleLength - 1);
            foreach (var header in _htsConnectionHandler.GetHeaders())
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (target.ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    await body.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private MediaSourceInfo BuildRecordingMediaSource(string id)
        {
            var path = BuildRecordingPath(id);

            // The placeholder stands in only when the analysis fails. It describes nothing
            // truthfully, but it is what this plugin always reported, so a failure is no worse
            // than before rather than an error.
            return BuildPlaceholderStreams(new MediaSourceInfo
            {
                Path = path,
                Protocol = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? MediaProtocol.Http : MediaProtocol.File,

                // The identifier a client sends back as MediaSourceId. Jellyfin looks it up among
                // the sources stored with the item and only falls back to parsing it as a GUID
                // when that lookup fails -- which is what happened while the description was
                // handed over dynamically instead of being stored, and what surfaced as
                // "Unrecognized Guid format" rather than as the missing source it really was.
                Id = id,
                Container = "mpegts",
                AnalyzeDurationMs = 2000,
            });
        }

        private string BuildRecordingPath(string id)
        {
            try
            {
                // Built through the connection handler so the recording URL uses the web root
                // TVHeadend reports, exactly like channel icons and stream URLs do.
                return _htsConnectionHandler.GetAuthenticatedUrl("dvrfile/" + id);
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
