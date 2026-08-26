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
    public class RecordingsChannel : IChannel, ISupportsDelete, ISupportsLatestMedia, IHasFolderAttributes, IRequiresMediaInfoCallback
    {
        /// <summary>
        /// How much of a recording is fetched to analyse it. The program tables and a sample of
        /// every elementary stream sit at the very front; this is generous for that and still a
        /// tenth of a second over a local network.
        /// </summary>
        private const int AnalysisSampleLength = 8 * 1024 * 1024;

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
        /// What reaches them is an offset added to the date, not a date of its own. The published
        /// date is <c>max(DateLastUpdated, floor) + revision</c>, which has the two properties the
        /// job needs and a fixed date has neither of. For an unchanged recording it is greater
        /// than the stored value exactly once per increment, so each upgrade rewrites every item
        /// once and then leaves it alone -- and it stays true however long after the release the
        /// plugin is installed, because it is measured from the recording rather than the calendar.
        /// For a recording TVHeadend really did change it rises with the change, so a later
        /// update still comes through instead of being masked by a fixed future date sitting above
        /// it.
        /// </para>
        /// <para>
        /// Counted in seconds rather than ticks so that the increment survives any rounding
        /// between here and the database. Raise it by one per change to the published shape.
        /// </para>
        /// </remarks>
        private const int MediaSourceSchemaRevision = 2;

        /// <summary>
        /// The floor every recording's modification date is lifted to, unchanged since 13.2.x.
        /// </summary>
        /// <remarks>
        /// It exists so that a recording TVHeadend has not touched in years still carries a date
        /// the schema revision can be counted from. It is not itself the revision and never moves
        /// again; moving it would break the monotonicity of every stored date at once.
        /// </remarks>
        private static readonly DateTime MediaSourceDateFloorUtc = new(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);

        private readonly ILogger<LiveTvService> _logger;
        private readonly TvheadendConnection _connection;
        private readonly LiveTvService _liveTvService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerApplicationHost _applicationHost;
        private readonly RecordingDescriber _describer;

        // A finished recording never changes, so what an analysis found holds for as long as the
        // server runs. Without this every listing of a folder would analyse its contents again.
        private readonly ConcurrentDictionary<string, MediaSourceInfo> _describedRecordings = new(StringComparer.OrdinalIgnoreCase);

        public RecordingsChannel(
            ILoggerFactory loggerFactory,
            TvheadendConnection connection,
            LiveTvService liveTvService,
            IMediaEncoder mediaEncoder,
            IHttpClientFactory httpClientFactory,
            IServerApplicationHost applicationHost)
        {
            _connection = connection;
            _liveTvService = liveTvService;
            _httpClientFactory = httpClientFactory;
            _applicationHost = applicationHost;
            _logger = loggerFactory.CreateLogger<LiveTvService>();
            _describer = new RecordingDescriber(mediaEncoder, _logger);
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
        /// Only what actually changes the listing. This used to mix in the day, the hour and a
        /// five-minute bucket, which discarded the cache on a timer whether or not anything had
        /// happened; the recordings themselves change when TVHeadend says they do, and that is
        /// what the key follows.
        /// </remarks>
        /// <param name="userId">The user the listing is for. Every user sees the same recordings.</param>
        /// <returns>The cache key.</returns>
        public string GetCacheKey(string userId)
            => GetService().RecordingRevision.ToString(CultureInfo.InvariantCulture);

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

        private static long? Runtime(MyRecordingInfo recording)
            => recording.EndDate > recording.StartDate ? (recording.EndDate - recording.StartDate).Ticks : null;

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

                // A placeholder, carrying no streams at all. The listing must not analyse the
                // recordings it lists -- that is one range request and one FFprobe run per
                // recording, on every listing -- and describing them from guesswork instead is
                // worse than saying nothing: Jellyfin maps streams by their position in this
                // list, so invented entries send FFmpeg's "-map" arguments to the wrong tracks.
                // What the recording contains is answered by GetChannelItemMediaInfo when
                // playback is negotiated. The Placeholder type is what tells Jellyfin this is
                // not a description it should act on; GetPlaybackMediaSources checks for exactly
                // that before it would otherwise force a remote probe of its own.
                MediaSources = [BuildPlaceholderSource(item.Id ?? string.Empty)],

                // Stated on the item, because the source deliberately carries nothing. TVHeadend
                // knows how long it scheduled the recording for, and without a duration Jellyfin
                // treats the recording as a stream of unknown length.
                RunTimeTicks = Runtime(item),
                // ParentIndexNumber = item.ParentIndexNumber,
                PremiereDate = item.StartDate,
                DateCreated = item.StartDate,
                // Two reasons the stored item can be out of date: the recording itself changed,
                // and this plugin now describes it differently than the version that wrote the
                // stored copy. The date carries both -- TVHeadend's own, floored, plus one step
                // per description change since. Without the second an upgrade never reaches
                // recordings somebody already has.
                DateModified = PublishedDateFor(item.DateLastUpdated),

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

            if (_describedRecordings.TryGetValue(id, out var cached))
            {
                return [cached];
            }

            var source = BuildRecordingMediaSource(id);
            if (!await DescribeRecording(id, source, cancellationToken).ConfigureAwait(false))
            {
                // Undescribed, and deliberately still without invented streams. Jellyfin falls
                // back to what it can work out itself rather than being told something untrue.
                return [source];
            }

            source.RunTimeTicks = await GetRecordingRuntime(id, cancellationToken).ConfigureAwait(false);

            // Kept only once the recording has finished. A recording still being written grows,
            // and what a sample of its opening said about it is not yet the whole truth -- an
            // audio track the broadcaster adds later would be missing from the description for
            // as long as the server runs.
            if (await IsFinishedAsync(id, cancellationToken).ConfigureAwait(false))
            {
                _describedRecordings[id] = source;
            }

            return [source];
        }

        /// <summary>
        /// Reports whether TVHeadend has finished writing a recording, which is what makes its
        /// description safe to keep.
        /// </summary>
        private async Task<bool> IsFinishedAsync(string id, CancellationToken cancellationToken)
        {
            var recordings = await GetAllRecordingsAsync(cancellationToken).ConfigureAwait(false);
            var recording = recordings.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            return recording is not null && recording.Status != MediaBrowser.Model.LiveTv.RecordingStatus.InProgress;
        }

        /// <summary>
        /// Gets how long the recording runs, from the times TVHeadend scheduled it for. An
        /// analysis cannot supply it, because what is analysed is a sample.
        /// </summary>
        private async Task<long?> GetRecordingRuntime(string id, CancellationToken cancellationToken)
        {
            var recordings = await GetAllRecordingsAsync(cancellationToken).ConfigureAwait(false);
            var recording = recordings.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            return recording is null ? null : Runtime(recording);
        }

        /// <summary>
        /// Fills in what a recording contains, from a sample of its opening.
        /// </summary>
        /// <remarks>
        /// Reading a sample rather than the recording is what makes the analysis affordable.
        /// TVHeadend answers range requests but does not advertise Accept-Ranges, so FFmpeg
        /// treats a recording as an unseekable stream and reads it from end to end -- measured at
        /// 68.6 seconds for an 8 GB recording against 0.14 for the sample.
        /// </remarks>
        /// <returns><see langword="true"/> when the sample described the recording.</returns>
        private async Task<bool> DescribeRecording(string id, MediaSourceInfo source, CancellationToken cancellationToken)
        {
            // The address, not the name. Path is the virtual file the client is told about and
            // is always set; whether the recording can be reached at all is EncoderPath.
            if (string.IsNullOrEmpty(source.EncoderPath))
            {
                return false;
            }

            var sample = Path.Combine(Path.GetTempPath(), $"tvheadend-analysis-{Guid.NewGuid():N}.ts");
            try
            {
                // Straight from TVHeadend, not through the endpoint this plugin serves clients
                // from: that one exists to make FFmpeg's seeking work, and going through it here
                // would only route the request back out through Jellyfin.
                var endpoint = await _connection.GetHttpEndpointAsync(cancellationToken).ConfigureAwait(false);
                var upstream = endpoint.CreateApiUrl("dvrfile/" + id);
                await FetchAnalysisSample(upstream, sample, cancellationToken).ConfigureAwait(false);

                // What the sample says the recording contains, and nothing beyond that. An
                // earlier version also scanned it for H.264 IDR frames and, finding none in the
                // first few megabytes, withheld direct play and re-encoded the whole recording.
                // A bounded sample cannot establish the absence of something: a recording that
                // opens on a recovery point and carries IDR frames a minute later looks exactly
                // the same. That inference is gone, along with the re-encode it forced.
                return await _describer
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

        /// <summary>
        /// Copies the opening of the recording to a local file, which is seekable and therefore
        /// analysable in a fraction of the time the recording itself would take.
        /// </summary>
        /// <remarks>
        /// The range request states how much is wanted, but a server is free to ignore it: a
        /// TVHeadend without range support, or a proxy in between, answers 200 with the whole
        /// recording. Copying that to the end would pull gigabytes across for an analysis that
        /// needs megabytes, so the limit is enforced while reading rather than assumed from the
        /// response. A short answer is equally fine -- whatever arrived is what gets analysed.
        /// </remarks>
        private async Task FetchAnalysisSample(string url, string destination, CancellationToken cancellationToken)
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, AnalysisSampleLength - 1);
            foreach (var header in _connection.HttpEndpoint.CreateHeaders())
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            // A server that cannot satisfy the range says so rather than failing outright; the
            // analysis then has nothing to work from and the caller keeps its placeholder.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                throw new InvalidOperationException($"TVHeadend rejected the range request for the analysis sample of {url}.");
            }

            response.EnsureSuccessStatusCode();

            var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (target.ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    await CopyAtMost(body, target, AnalysisSampleLength, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Copies at most <paramref name="limit"/> bytes, whatever the source offers.
        /// </summary>
        /// <param name="source">The stream to read.</param>
        /// <param name="destination">The stream to write.</param>
        /// <param name="limit">The most that may be copied.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of bytes copied.</returns>
        internal static async Task<long> CopyAtMost(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long copied = 0;
                while (copied < limit)
                {
                    var wanted = (int)Math.Min(buffer.Length, limit - copied);
                    var read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                }

                return copied;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// The identifier a client sends back as MediaSourceId, derived from the recording so it
        /// is the same on every call and after a restart.
        /// </summary>
        /// <remarks>
        /// It has to be readable as a GUID. Two places downstream parse it as one --
        /// <c>DynamicHlsHelper.GetMasterPlaylistInternal</c> unconditionally, and
        /// <c>StreamingHelpers.GetStreamingState</c> when its lookup by identifier finds nothing
        /// -- and a TVHeadend recording number is not a GUID, so the request fails with
        /// "Unrecognized Guid format" before playback starts. Deriving it keeps it stable, which
        /// the stored media source and every later request depend on.
        /// </remarks>
        /// <param name="recordingId">The TVHeadend recording identifier.</param>
        /// <returns>The media source identifier.</returns>
        internal static string RecordingMediaSourceId(string recordingId)
        {
            ArgumentException.ThrowIfNullOrEmpty(recordingId);

            return ("TVHeadEnd_Recording_" + recordingId).GetMD5().ToString("N", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The modification date a recording is published with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>max(DateLastUpdated, floor) + revision seconds</c>. Monotone in TVHeadend's own date,
        /// so a recording that really changed still comes through, and greater than the previously
        /// published value exactly once per revision, so an upgrade rewrites each stored item once.
        /// </para>
        /// <para>
        /// Nothing here reads the clock. A value derived from the current time would be later than
        /// the stored date on every listing and rewrite every recording for ever.
        /// </para>
        /// </remarks>
        /// <param name="recordingChanged">When TVHeadend last touched the recording.</param>
        /// <returns>The date to publish.</returns>
        internal static DateTime PublishedDateFor(DateTime recordingChanged)
        {
            var floored = recordingChanged > MediaSourceDateFloorUtc ? recordingChanged : MediaSourceDateFloorUtc;

            return floored.AddSeconds(MediaSourceSchemaRevision);
        }

        /// <summary>
        /// The source a listing reports: a placeholder, standing for a recording nobody has asked
        /// to play yet.
        /// </summary>
        /// <remarks>
        /// It carries no streams, because the listing does not analyse and must not guess. Its
        /// identifier is deliberately the TVHeadend one rather than a GUID: the identifier a
        /// client comes back with has to be the described source, and keeping the two textually
        /// distinct means a placeholder can never be mistaken for a description.
        /// </remarks>
        /// <param name="id">The TVHeadend recording identifier.</param>
        /// <returns>The placeholder source.</returns>
        internal static MediaSourceInfo BuildPlaceholderSource(string id)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);

            return new MediaSourceInfo
            {
                Id = "tvheadend-recording-" + id,
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
        private MediaSourceInfo BuildRecordingMediaSource(string id)
            => BuildRecordingSource(id, BuildRecordingUrl(id));

        /// <inheritdoc cref="BuildRecordingMediaSource"/>
        /// <param name="id">The TVHeadend recording identifier.</param>
        /// <param name="url">The address this plugin serves the recording from.</param>
        /// <returns>The source.</returns>
        internal static MediaSourceInfo BuildRecordingSource(string id, string url)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);

            return new MediaSourceInfo
            {
                Path = VirtualRecordingPath(id),
                Protocol = MediaProtocol.File,
                EncoderPath = url,
                EncoderProtocol = MediaProtocol.Http,
                Id = RecordingMediaSourceId(id),

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
