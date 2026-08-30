using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Core.Media;
using TVHeadEnd.Streaming;
using HtspException = Tvheadend.Htsp.HtspException;

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
    /// the obvious way to get that wrong, so both callers share whichever reading is already under
    /// way.
    /// </para>
    /// <para>
    /// What is remembered is the analysis and nothing else. A finished recording never changes,
    /// so its analysis is worth keeping; a recording still being written does not, and its
    /// analysis is kept only long enough for the burst of requests around a single playback to
    /// share one reading of it.
    /// </para>
    /// <para>
    /// Getting the bytes is somebody else's job -- see <see cref="IRecordingSampleSource"/>. What
    /// is left here is a question about time and callers: when a reading is worth starting, who
    /// may share it, how long it may take, and how long it is worth remembering.
    /// </para>
    /// </remarks>
    public sealed class RecordingAnalysisService : IRecordingAnalyser
    {
        /// <summary>
        /// How many recordings are remembered at once.
        /// </summary>
        /// <remarks>
        /// A finished recording's analysis stays true for ever, which is a reason to keep it and
        /// not a reason to keep every recording this process has ever been asked about. Sized for
        /// a television server rather than for the worst case: a few hundred recordings is a large
        /// library, each entry describes a handful of streams, and what falls out is read again in
        /// a fraction of a second the next time somebody plays it.
        /// </remarks>
        private const int RememberedRecordings = 256;

        /// <summary>
        /// How long the analysis of a recording that may still be growing is kept.
        /// </summary>
        /// <remarks>
        /// Long enough for one playback's requests to share a reading, short enough that a
        /// recording which gains a track while it runs is not described by its first minute for
        /// the rest of the server's life.
        /// </remarks>
        private static readonly TimeSpan BriefRetention = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long one reading of a recording may take before it is abandoned.
        /// </summary>
        /// <remarks>
        /// The reading is shared, which is exactly what makes an unbounded one dangerous: a fetch
        /// against a server that accepts the connection and then says nothing, or an FFprobe that
        /// never returns, would sit here for the life of the process with every later caller
        /// queued up behind it. Generous against a slow disk or a distant server, and finite.
        /// </remarks>
        private static readonly TimeSpan AnalysisTimeLimit = TimeSpan.FromMinutes(2);

        private readonly Dictionary<string, Analysis> _analyses = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        private readonly IRecordingSampleSource _samples;
        private readonly RecordingInspector _inspector;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly TimeProvider _clock;
        private readonly TimeSpan _timeLimit;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingAnalysisService"/> class.
        /// </summary>
        /// <param name="samples">Where the opening of a recording is fetched from.</param>
        /// <param name="mediaEncoder">The Jellyfin media encoder.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="lifetime">The host's lifetime, which bounds every reading this starts.</param>
        public RecordingAnalysisService(
            IRecordingSampleSource samples,
            IMediaEncoder mediaEncoder,
            ILoggerFactory loggerFactory,
            IHostApplicationLifetime lifetime)
            : this(samples, mediaEncoder, loggerFactory, lifetime, TimeProvider.System, AnalysisTimeLimit)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingAnalysisService"/> class with a
        /// clock and a time limit of the caller's choosing, so that what is kept, for how long,
        /// and what happens when a reading does not finish can all be tested.
        /// </summary>
        /// <param name="samples">Where the opening of a recording is fetched from.</param>
        /// <param name="mediaEncoder">The Jellyfin media encoder.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="lifetime">The host's lifetime, which bounds every reading this starts.</param>
        /// <param name="clock">The clock retention is measured against.</param>
        /// <param name="timeLimit">How long one reading may take.</param>
        public RecordingAnalysisService(
            IRecordingSampleSource samples,
            IMediaEncoder mediaEncoder,
            ILoggerFactory loggerFactory,
            IHostApplicationLifetime lifetime,
            TimeProvider clock,
            TimeSpan timeLimit)
        {
            ArgumentNullException.ThrowIfNull(samples);
            ArgumentNullException.ThrowIfNull(loggerFactory);
            ArgumentNullException.ThrowIfNull(lifetime);
            ArgumentNullException.ThrowIfNull(clock);

            _samples = samples;
            _lifetime = lifetime;
            _clock = clock;
            _timeLimit = timeLimit;
            _logger = loggerFactory.CreateLogger<RecordingAnalysisService>();
            _inspector = new RecordingInspector(mediaEncoder, _logger);
        }

        /// <summary>
        /// Gets how many recordings are remembered at this moment.
        /// </summary>
        public int Remembered
        {
            get
            {
                lock (_gate)
                {
                    return _analyses.Count;
                }
            }
        }

        /// <summary>
        /// What a sample of one recording contains.
        /// </summary>
        /// <remarks>
        /// Callers asking about the same recording wait on one reading of it, and a caller giving
        /// up does not take that reading away from the others. An operational failure -- a
        /// recording that cannot be reached, or read, or probed -- comes back as an analysis that
        /// describes nothing, so every caller carries on with whatever it had.
        /// </remarks>
        /// <param name="recordingId">The TVHeadend recording identifier.</param>
        /// <param name="recordingHasFinished">
        /// Whether the recording is complete, and its analysis therefore true for good. A caller
        /// that does not know says <see langword="false"/>, which costs at most one further read.
        /// </param>
        /// <param name="cancellationToken">
        /// This caller's token. It bounds this caller's wait and nothing else.
        /// </param>
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
                var now = _clock.GetUtcNow();

                if (!_analyses.TryGetValue(recordingId, out var existing) || HasLapsed(existing, now))
                {
                    existing = new Analysis(now);

                    // Started without the caller's token, and with a lifetime of its own. The
                    // reading is shared, so one caller walking away must not cancel what the
                    // others are waiting for -- and nothing shared may run for ever.
                    existing.Work = Analyse(recordingId);
                    _analyses[recordingId] = existing;
                }

                existing.Keep |= recordingHasFinished;
                existing.LastUsed = now;
                analysis = existing;

                Forget();
            }

            return analysis.Work.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Whether a remembered analysis may no longer be handed out.
        /// </summary>
        private bool HasLapsed(Analysis analysis, DateTimeOffset now)
        {
            // Still being read. Sharing it is the whole point, and a reading is never replaced
            // merely because time passed while it ran.
            if (!analysis.Work.IsCompleted)
            {
                return false;
            }

            var expired = now - analysis.Started > BriefRetention;

            // A reading that failed, or that found nothing, is worth trying again shortly: the
            // server may have been busy, or the file may not have been there yet. Asking a task
            // for its result is only safe once it is known to have one.
            if (!analysis.Work.IsCompletedSuccessfully || !analysis.Work.Result.DescribesTheRecording)
            {
                return expired;
            }

            return !analysis.Keep && expired;
        }

        /// <summary>
        /// Drops the least recently wanted recordings once too many are remembered.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A finished recording's analysis never goes out of date, which is why it is kept -- but
        /// keeping every recording a long-running server was ever asked about is a slow leak
        /// rather than a cache. What falls out is simply read again next time.
        /// </para>
        /// <para>
        /// A reading still under way is never dropped, whatever its age. Callers are waiting on
        /// it, and forgetting it here would let the next caller start a second reading of the same
        /// recording while the first was still running -- the one thing this class exists to
        /// prevent. That can leave more entries than the limit for as long as they are running,
        /// which is bounded by how many recordings are being opened at once.
        /// </para>
        /// </remarks>
        private void Forget()
        {
            if (_analyses.Count <= RememberedRecordings)
            {
                return;
            }

            var forgettable = _analyses
                .Where(entry => entry.Value.Work.IsCompleted)
                .OrderBy(entry => entry.Value.LastUsed)
                .Select(entry => entry.Key)
                .Take(_analyses.Count - RememberedRecordings)
                .ToList();

            foreach (var key in forgettable)
            {
                _analyses.Remove(key);
            }
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
            // The shared reading's own lifetime: bounded by a time limit, and by the server going
            // away. No caller's token reaches this.
            using var deadline = new CancellationTokenSource(_timeLimit, _clock);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token,
                _lifetime.ApplicationStopping);

            try
            {
                using var sample = await _samples.FetchAsync(recordingId, lifetime.Token).ConfigureAwait(false);

                var media = await _inspector
                    .Inspect(sample.Path, $"recording {recordingId}", SourceContainer.TransportStream, lifetime.Token)
                    .ConfigureAwait(false);

                var broadcast = ReadBroadcastFacts(sample.Path);

                _logger.LogDebug(
                    "TVHeadend recording {RecordingId}: entry point evidence {Evidence}",
                    recordingId,
                    broadcast.Evidence);

                return new RecordingAnalysis(media, broadcast.ProgramMap, broadcast.Evidence);
            }
            catch (OperationCanceledException) when (_lifetime.ApplicationStopping.IsCancellationRequested)
            {
                // The server is shutting down. Nothing went wrong and nobody needs the answer.
                return RecordingAnalysis.Nothing;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "TVHeadend recording {RecordingId} was not analysed within {TimeLimit} and was given up on",
                    recordingId,
                    _timeLimit);
                return RecordingAnalysis.Nothing;
            }
            catch (Exception exception) when (IsAnOperationalFailure(exception))
            {
                // Every caller has something to fall back on, so a recording that cannot be
                // analysed behaves as it did before rather than failing outright.
                _logger.LogError(exception, "TVHeadend recording {RecordingId} could not be analysed", recordingId);
                return RecordingAnalysis.Nothing;
            }
        }

        /// <summary>
        /// Whether an exception is one of the ways analysing a recording is expected to fail.
        /// </summary>
        /// <remarks>
        /// The list is deliberately a list. Catching everything meant a defect in this plugin -- a
        /// null reference, a bad argument, an assumption that quietly stopped holding -- looked
        /// exactly like a recording that could not be reached: no analysis, one log line, and
        /// playback carrying on with less than it should have had. What is absorbed here is
        /// somebody else's server, disk or file being unavailable, which is worth absorbing
        /// because a recording that cannot be analysed must still be playable.
        /// </remarks>
        private static bool IsAnOperationalFailure(Exception exception) => exception switch
        {
            // The recording could not be fetched: TVHeadend refused it, went away, or was never
            // reachable to begin with.
            HttpRequestException => true,
            SocketException => true,
            HtspException => true,
            TimeoutException => true,

            // The sample could not be written, or could not be read back.
            IOException => true,
            UnauthorizedAccessException => true,

            // FFprobe could not make sense of it, or could not be run at all.
            FfmpegException => true,

            _ => false,
        };

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
            /// Gets or sets when this was last wanted, which is the order things are forgotten in.
            /// </summary>
            public DateTimeOffset LastUsed { get; set; } = started;

            /// <summary>
            /// Gets or sets the reading itself, shared by everyone who asked for it.
            /// </summary>
            public Task<RecordingAnalysis> Work { get; set; } = Task.FromResult(RecordingAnalysis.Nothing);

            /// <summary>
            /// Gets or sets a value indicating whether the recording has finished, and this is
            /// therefore worth keeping for as long as there is room for it.
            /// </summary>
            public bool Keep { get; set; }
        }
    }
}
