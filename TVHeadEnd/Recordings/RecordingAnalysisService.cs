using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Streaming;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.Recordings
{
    /// <summary>
    /// Establishes what a recording contains, once, for everyone who asks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two quite different callers need the same facts about the same recording within
    /// milliseconds of each other: the channel, filling in the media source Jellyfin publishes,
    /// and the playback compatibility filter, deciding whether this client can be left to play it
    /// directly. Fetching eight megabytes twice and running FFprobe twice for one click would be
    /// the obvious way to get that wrong, so the fetch and the analysis live here and both
    /// callers share whichever reading is already under way.
    /// </para>
    /// <para>
    /// What is remembered is the analysis and nothing else. A finished recording never changes,
    /// so its analysis holds for as long as the server runs; a recording still being written does
    /// not, and its analysis is kept only long enough for the burst of requests around a single
    /// playback to share one reading of it.
    /// </para>
    /// </remarks>
    public sealed class RecordingAnalysisService : IRecordingAnalyser
    {
        /// <summary>
        /// How much of a recording is fetched to analyse it. The program tables and a sample of
        /// every elementary stream sit at the very front; this is generous for that and still a
        /// tenth of a second over a local network.
        /// </summary>
        public const int SampleLength = 8 * 1024 * 1024;

        /// <summary>
        /// How long the analysis of a recording that may still be growing is kept.
        /// </summary>
        /// <remarks>
        /// Long enough for one playback's requests to share a reading, short enough that a
        /// recording which gains a track while it runs is not described by its first minute for
        /// the rest of the server's life.
        /// </remarks>
        private static readonly TimeSpan BriefRetention = TimeSpan.FromSeconds(30);

        private readonly Dictionary<string, Analysis> _analyses = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        private readonly TvheadendConnection _connection;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly RecordingInspector _inspector;
        private readonly TimeProvider _clock;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingAnalysisService"/> class.
        /// </summary>
        /// <param name="connection">The TVHeadend connection.</param>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="mediaEncoder">The Jellyfin media encoder.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public RecordingAnalysisService(
            TvheadendConnection connection,
            IHttpClientFactory httpClientFactory,
            IMediaEncoder mediaEncoder,
            ILoggerFactory loggerFactory)
            : this(connection, httpClientFactory, mediaEncoder, loggerFactory, TimeProvider.System)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingAnalysisService"/> class with a
        /// clock of the caller's choosing, so what is kept and for how long can be tested.
        /// </summary>
        /// <param name="connection">The TVHeadend connection.</param>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="mediaEncoder">The Jellyfin media encoder.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="clock">The clock retention is measured against.</param>
        public RecordingAnalysisService(
            TvheadendConnection connection,
            IHttpClientFactory httpClientFactory,
            IMediaEncoder mediaEncoder,
            ILoggerFactory loggerFactory,
            TimeProvider clock)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);

            _connection = connection;
            _httpClientFactory = httpClientFactory;
            _clock = clock;
            _logger = loggerFactory.CreateLogger<RecordingAnalysisService>();
            _inspector = new RecordingInspector(mediaEncoder, _logger);
        }

        /// <summary>
        /// What a sample of one recording contains.
        /// </summary>
        /// <remarks>
        /// Never reports failure as an exception: a recording that cannot be reached or read
        /// yields an analysis that describes nothing, and every caller carries on with whatever
        /// it had. Callers asking about the same recording wait on one reading of it, and a
        /// caller giving up does not take that reading away from the others.
        /// </remarks>
        /// <param name="recordingId">The TVHeadend recording identifier.</param>
        /// <param name="recordingHasFinished">
        /// Whether the recording is complete, and its analysis therefore true for good. A caller
        /// that does not know says <see langword="false"/>, which costs at most one further read.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The analysis.</returns>
        public Task<RecordingAnalysis> AnalyseAsync(
            string recordingId,
            bool recordingHasFinished,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(recordingId);

            Analysis analysis;
            lock (_gate)
            {
                if (!_analyses.TryGetValue(recordingId, out var existing) || HasLapsed(existing))
                {
                    existing = new Analysis(_clock.GetUtcNow());

                    // Started without the caller's token. The reading is shared, so one caller
                    // walking away must not cancel what the others are waiting for.
                    existing.Work = Analyse(recordingId);
                    _analyses[recordingId] = existing;
                }

                existing.Keep |= recordingHasFinished;
                analysis = existing;
            }

            return analysis.Work.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Copies at most <paramref name="limit"/> bytes, whatever the source offers.
        /// </summary>
        /// <param name="source">The stream to read.</param>
        /// <param name="destination">The stream to write.</param>
        /// <param name="limit">The most that may be copied.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of bytes copied.</returns>
        public static async Task<long> CopyAtMost(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
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
        /// Whether a remembered analysis may still be handed out.
        /// </summary>
        private bool HasLapsed(Analysis analysis)
        {
            // Still being read. Sharing it is the whole point.
            if (!analysis.Work.IsCompleted)
            {
                return false;
            }

            var expired = _clock.GetUtcNow() - analysis.Started > BriefRetention;

            // A reading that found nothing is worth trying again shortly, whatever the recording
            // is: the server may have been busy, or the file may not have been there yet.
            if (!analysis.Work.Result.DescribesTheRecording)
            {
                return expired;
            }

            return !analysis.Keep && expired;
        }

        /// <summary>
        /// Fetches the opening of a recording and reads everything wanted out of it.
        /// </summary>
        /// <remarks>
        /// One fetch, one temporary file, one open of that file. FFprobe answers what the streams
        /// are, the broadcast's own tables answer what it said they were, and the access points
        /// are read from the same bytes -- there is nothing here a second download would add.
        /// </remarks>
        private async Task<RecordingAnalysis> Analyse(string recordingId)
        {
            var sample = Path.Combine(Path.GetTempPath(), $"tvheadend-analysis-{Guid.NewGuid():N}.ts");
            try
            {
                // Straight from TVHeadend, not through the endpoint this plugin serves clients
                // from: that one exists to make FFmpeg's seeking work, and going through it here
                // would only route the request back out through Jellyfin.
                var endpoint = await _connection.GetHttpEndpointAsync(CancellationToken.None).ConfigureAwait(false);
                await FetchSample(endpoint.CreateApiUrl("dvrfile/" + recordingId), sample).ConfigureAwait(false);

                var media = await _inspector
                    .Inspect(sample, $"recording {recordingId}", SourceContainer.TransportStream, CancellationToken.None)
                    .ConfigureAwait(false);

                var broadcast = ReadBroadcastFacts(sample);

                _logger.LogDebug(
                    "TVHeadend recording {RecordingId}: entry point evidence {Evidence}",
                    recordingId,
                    broadcast.Evidence);

                return new RecordingAnalysis(media, broadcast.ProgramMap, broadcast.Evidence);
            }
            catch (Exception exception)
            {
                // Every caller has something to fall back on, so a recording that cannot be
                // analysed behaves as it did before rather than failing outright.
                _logger.LogError(exception, "TVHeadend recording {RecordingId} could not be analysed", recordingId);
                return RecordingAnalysis.Nothing;
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
        /// Reads the broadcast's own tables and its H.264 access points out of the sample.
        /// </summary>
        /// <remarks>
        /// Failure is not an error here. A recording in a container that is not MPEG-TS has no
        /// program map, an opening that never carried a complete pair of tables has none to find,
        /// and either way FFprobe's account of the streams still stands on its own.
        /// </remarks>
        /// <param name="sample">The local sample file.</param>
        /// <returns>The program map, if there was one, and what the access points showed.</returns>
        private (ProgramMapTable? ProgramMap, H264EntryPointEvidence Evidence) ReadBroadcastFacts(string sample)
        {
            try
            {
                using var file = new FileStream(
                    sample,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    TransportStreamPacket.Length,
                    FileOptions.SequentialScan);

                var programMap = RecordedProgramMap.ReadFrom(file);
                file.Seek(0, SeekOrigin.Begin);

                return (programMap, RecordedH264AccessPointProbe.Examine(file, programMap));
            }
            catch (IOException exception)
            {
                _logger.LogDebug(exception, "The recording sample could not be read for its own tables");
            }
            catch (UnauthorizedAccessException exception)
            {
                _logger.LogDebug(exception, "The recording sample could not be read for its own tables");
            }

            return (null, H264EntryPointEvidence.Insufficient);
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
        /// <param name="url">The recording, straight from TVHeadend.</param>
        /// <param name="destination">The local file to write.</param>
        private async Task FetchSample(string url, string destination)
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, SampleLength - 1);
            foreach (var header in _connection.HttpEndpoint.CreateHeaders())
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None)
                .ConfigureAwait(false);

            // A server that cannot satisfy the range says so rather than failing outright; the
            // analysis then has nothing to work from and the caller keeps what it had.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                throw new InvalidOperationException($"TVHeadend rejected the range request for the analysis sample of {url}.");
            }

            response.EnsureSuccessStatusCode();

            var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (target.ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStreamAsync(CancellationToken.None).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    await CopyAtMost(body, target, SampleLength, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// One recording's analysis, and how long it may be handed out for.
        /// </summary>
        /// <param name="started">When the reading began.</param>
        private sealed class Analysis(DateTimeOffset started)
        {
            /// <summary>
            /// Gets when the reading began, which is what retention is measured from.
            /// </summary>
            public DateTimeOffset Started { get; } = started;

            /// <summary>
            /// Gets or sets the reading itself, shared by everyone who asked for it.
            /// </summary>
            public Task<RecordingAnalysis> Work { get; set; } = Task.FromResult(RecordingAnalysis.Nothing);

            /// <summary>
            /// Gets or sets a value indicating whether the recording has finished, and this is
            /// therefore true for as long as the server runs.
            /// </summary>
            public bool Keep { get; set; }
        }
    }
}
