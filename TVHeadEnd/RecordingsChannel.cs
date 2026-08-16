using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    public class RecordingsChannel : IChannel, ISupportsDelete, ISupportsLatestMedia, IHasFolderAttributes, IRequiresMediaInfoCallback
    {
        private const int ScanChunkSize = 65536;

        private readonly TimeSpan _timeout = TimeSpan.FromMinutes(5);
        private readonly ILogger<LiveTvService> _logger;
        private readonly HTSConnectionHandler _htsConnectionHandler;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IHttpClientFactory _httpClientFactory;

        public RecordingsChannel(
            ILoggerFactory loggerFactory,
            HTSConnectionHandler htsConnectionHandler,
            IMediaEncoder mediaEncoder,
            IHttpClientFactory httpClientFactory)
        {
            _htsConnectionHandler = htsConnectionHandler;
            _mediaEncoder = mediaEncoder;
            _httpClientFactory = httpClientFactory;
            _logger = loggerFactory.CreateLogger<LiveTvService>();
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
        /// Gets the version of this channel's contents. Jellyfin keeps a channel's item list on
        /// disk for three hours and only asks the plugin again when this string differs, so a
        /// constant -- which this used to be -- meant a recording made on the TVHeadend server
        /// could take hours to appear, and a change to how items are described never reached an
        /// existing library at all. Following the recordings themselves makes both immediate.
        /// </summary>
        public string DataVersion =>
            "2-" + _htsConnectionHandler.GetRecordingsChangeStamp().ToString(CultureInfo.InvariantCulture);

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

            var result = new ChannelItemResult
            {
                Items = allRecordings.Where(filter).Select(info => ConvertToChannelItem(info)).ToList()
            };

            return result;
        }

        private ChannelItemInfo ConvertToChannelItem(MyRecordingInfo item)
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
                // Deliberately empty. Jellyfin appends the dynamic sources from
                // GetChannelItemMediaInfo to whatever the item carries without comparing them,
                // so carrying one here as well would offer every recording twice. Stating the
                // empty list rather than leaving it unset is what clears the source a previous
                // version of this plugin stored on the item.
                MediaSources = [],
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
        /// Describes a recording as it really is. Jellyfin asks for this when playback is
        /// negotiated, not while the list is browsed, so the analysis costs nothing until a
        /// recording is actually played.
        /// </summary>
        /// <remarks>
        /// Without it the placeholder streams a previous version reported -- index -1, no codec
        /// -- were all the StreamBuilder had to go on, and it cannot match a codec it has not
        /// been told about. Every recording was therefore transcoded, however well the device
        /// could have played it untouched. Jellyfin keeps the answer for five minutes, which
        /// covers opening a recording and pressing play, so nothing is cached here.
        /// </remarks>
        /// <param name="id">The TVHeadend recording identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The media sources for the recording.</returns>
        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);

            var source = BuildRecordingMediaSource(id);
            if (string.IsNullOrEmpty(source.Path))
            {
                return [source];
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var info = await _mediaEncoder.GetMediaInfo(
                    new MediaInfoRequest
                    {
                        MediaType = MediaBrowser.Model.Dlna.DlnaProfileType.Video,
                        MediaSource = source,
                        ExtractChapters = false,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (info is null)
                {
                    _logger.LogWarning(
                        "TVHeadend recording {RecordingId}: the analysis returned nothing after {ElapsedMilliseconds} ms; it will be transcoded",
                        id,
                        stopwatch.ElapsedMilliseconds);
                    return [source];
                }

                source.MediaStreams = info.MediaStreams ?? [];
                source.RunTimeTicks = info.RunTimeTicks;
                source.Bitrate = info.Bitrate;
                source.Size = info.Size;
                if (!string.IsNullOrEmpty(info.Container))
                {
                    // TVHeadend records to Matroska as readily as to MPEG-TS, so what the
                    // analysis found is what counts. Only the transport stream gets both of its
                    // spellings, because FFprobe calls it "ts" while device profiles ask for
                    // "mpegts" and Jellyfin compares the two as plain strings.
                    source.Container = info.Container.Equals("mpegts", StringComparison.OrdinalIgnoreCase)
                        || info.Container.Equals("ts", StringComparison.OrdinalIgnoreCase)
                        ? Streaming.LiveTvMediaSourceFactory.Container
                        : info.Container;
                }

                // A recording of a broadcast that signals random access with recovery points
                // instead of IDR frames -- the ARD network does -- offers no synchronisation
                // sample, and common device decoders never emit a picture from it. Transcoding
                // produces one, so such a recording must not be offered for direct play. This
                // went unnoticed while every recording was transcoded for want of metadata.
                if (source.MediaStreams.Any(stream => stream.Type == MediaStreamType.Video)
                    && !await CarriesIdrFrames(source, cancellationToken).ConfigureAwait(false))
                {
                    source.SupportsDirectPlay = false;
                    source.SupportsDirectStream = false;
                    _logger.LogInformation(
                        "TVHeadend recording {RecordingId} carries no IDR frame; it is transcoded so device decoders can start it",
                        id);
                }

                _logger.LogInformation(
                    "TVHeadend recording {RecordingId} analysed in {ElapsedMilliseconds} ms: {StreamCount} streams ({Codecs}), direct play {DirectPlay}",
                    id,
                    stopwatch.ElapsedMilliseconds,
                    source.MediaStreams.Count,
                    string.Join(", ", source.MediaStreams.Select(stream => stream.Codec)),
                    source.SupportsDirectPlay);

                return [source];
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A recording that cannot be analysed is still playable by transcoding, which is
                // what an unanalysed source falls back to.
                _logger.LogError(
                    exception,
                    "TVHeadend recording {RecordingId} could not be analysed after {ElapsedMilliseconds} ms",
                    id,
                    stopwatch.ElapsedMilliseconds);
                return [source];
            }
        }

        /// <summary>
        /// Reads the opening of the recording to establish whether its video carries an IDR
        /// frame, the only picture common device decoders will start on.
        /// </summary>
        /// <remarks>
        /// Cheap enough to sit in front of playback: a broadcast that sends IDR frames at all
        /// sends one within the first fraction of a second, so the scan almost always ends after
        /// a few hundred kilobytes. Only a recording that genuinely has none is read to the
        /// scanner's limit.
        /// </remarks>
        private async Task<bool> CarriesIdrFrames(MediaSourceInfo source, CancellationToken cancellationToken)
        {
            var conditioner = new LiveTransportStreamConditioner(LiveTransportStreamConditioner.EventInformationTablePid);
            var buffer = ArrayPool<byte>.Shared.Rent(ScanChunkSize);
            var conditioned = ArrayPool<byte>.Shared.Rent(
                LiveTransportStreamConditioner.GetMaximumConditionedLength(ScanChunkSize));

            try
            {
                using var client = _httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Get, source.Path);
                foreach (var header in _htsConnectionHandler.GetHeaders())
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    while (conditioner.IdrScanBytes < LiveTransportStreamConditioner.IdrScanLimit)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(0, ScanChunkSize), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        conditioner.Condition(buffer.AsSpan(0, read), conditioned);
                        if (conditioner.HasSeenIdrFrame)
                        {
                            return true;
                        }
                    }
                }

                return conditioner.HasSeenIdrFrame;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Unable to tell. Claiming an IDR frame is the lesser risk: the recording plays
                // as it did before, and a device that cannot start it still falls back.
                _logger.LogWarning(exception, "TVHeadend recording: the IDR scan could not read the recording");
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                ArrayPool<byte>.Shared.Return(conditioned);
            }
        }

        private MediaSourceInfo BuildRecordingMediaSource(string id)
        {
            var path = BuildRecordingPath(id);

            return new MediaSourceInfo
            {
                Path = path,
                Protocol = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? MediaProtocol.Http : MediaProtocol.File,
                Id = id,
                Container = "mpegts",
                AnalyzeDurationMs = 2000,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                IsInfiniteStream = false,
                RequiresOpening = false,
                RequiresClosing = false,
                MediaStreams = [],
            };
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
