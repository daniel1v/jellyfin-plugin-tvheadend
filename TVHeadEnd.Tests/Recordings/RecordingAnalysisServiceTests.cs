using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Recordings;
using Xunit;

namespace TVHeadEnd.Tests.Recordings;

/// <summary>
/// Who shares one reading of a recording, how long it may take, and how long it is worth keeping.
/// </summary>
/// <remarks>
/// <para>
/// Two callers arrive within milliseconds of each other for every recording anybody plays: the
/// channel describing it and the filter deciding whether this client can play it directly. Getting
/// this wrong is not visible in a log -- it is eight megabytes fetched twice and FFprobe run twice
/// for one click, or worse, a caller pressing stop and taking the other caller's answer with them.
/// </para>
/// <para>
/// None of this could be tested until the fetching moved behind <see cref="IRecordingSampleSource"/>:
/// every reading used to begin by opening a real connection to a real server.
/// </para>
/// </remarks>
public class RecordingAnalysisServiceTests
{
    [Fact]
    public async Task TwoCallersAtOnceShareOneReading()
    {
        var samples = new WaitingSampleSource();
        var service = Service(samples, out var inspections);

        var first = service.AnalyseAsync("4711", false, CancellationToken.None);
        var second = service.AnalyseAsync("4711", false, CancellationToken.None);

        await samples.Requested;
        samples.Release();

        Assert.True((await first).DescribesTheRecording);
        Assert.True((await second).DescribesTheRecording);

        Assert.Equal(1, samples.Fetches);
        Assert.Single(inspections);
    }

    [Fact]
    public async Task OneCallerGivingUpDoesNotTakeTheReadingAwayFromTheOther()
    {
        // The failure this guards against is a viewer pressing stop while somebody else is
        // starting the same recording. Passing the caller's token into the shared work would make
        // that one cancellation everybody's.
        var samples = new WaitingSampleSource();
        var service = Service(samples, out var inspections);

        using var impatient = new CancellationTokenSource();
        var abandoned = service.AnalyseAsync("4711", false, impatient.Token);
        var waiting = service.AnalyseAsync("4711", false, CancellationToken.None);

        await samples.Requested;
        await impatient.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.False(waiting.IsCompleted);

        samples.Release();

        Assert.True((await waiting).DescribesTheRecording);
        Assert.Equal(1, samples.Fetches);
        Assert.Single(inspections);
    }

    [Fact]
    public async Task ARecordingStillBeingWrittenIsReadAgainOnceTheBriefRetentionHasPassed()
    {
        // It is still growing, so what its first minute said about it stops being the whole truth.
        var samples = new WaitingSampleSource();
        var clock = new SteppableClock();
        var service = Service(samples, out _, clock);

        await Complete(service, samples, "4711", recordingHasFinished: false);
        Assert.Equal(1, samples.Fetches);

        clock.Advance(TimeSpan.FromSeconds(29));
        await Complete(service, samples, "4711", recordingHasFinished: false);
        Assert.Equal(1, samples.Fetches);

        clock.Advance(TimeSpan.FromSeconds(2));
        await Complete(service, samples, "4711", recordingHasFinished: false);
        Assert.Equal(2, samples.Fetches);
    }

    [Fact]
    public async Task AFinishedRecordingIsReadOnceAndKept()
    {
        // It cannot change, so no amount of time passing is a reason to read it again.
        var samples = new WaitingSampleSource();
        var clock = new SteppableClock();
        var service = Service(samples, out _, clock);

        await Complete(service, samples, "4711", recordingHasFinished: true);

        clock.Advance(TimeSpan.FromDays(3));
        await Complete(service, samples, "4711", recordingHasFinished: true);

        Assert.Equal(1, samples.Fetches);
    }

    [Fact]
    public async Task AReadingThatFailedIsWorthTryingAgainShortly()
    {
        // The server may have been busy, or the file may not have been there yet. A failure is
        // not remembered as though it were an answer -- but nor is it retried on every request.
        var samples = new WaitingSampleSource { Fails = true };
        var clock = new SteppableClock();
        var service = Service(samples, out _, clock);

        await Complete(service, samples, "4711", recordingHasFinished: true);
        await Complete(service, samples, "4711", recordingHasFinished: true);
        Assert.Equal(1, samples.Fetches);

        clock.Advance(TimeSpan.FromSeconds(31));
        samples.Fails = false;

        var analysis = await Complete(service, samples, "4711", recordingHasFinished: true);

        Assert.Equal(2, samples.Fetches);
        Assert.True(analysis.DescribesTheRecording);
    }

    [Fact]
    public async Task AReadingUnderWayIsNeverReplacedBecauseTimePassed()
    {
        // Retention is about answers, not about work in progress. Replacing a running reading
        // would start a second fetch of the same recording while the first was still going.
        var samples = new WaitingSampleSource();
        var clock = new SteppableClock();
        var service = Service(samples, out _, clock);

        var first = service.AnalyseAsync("4711", false, CancellationToken.None);
        await samples.Requested;

        clock.Advance(TimeSpan.FromHours(1));
        var second = service.AnalyseAsync("4711", false, CancellationToken.None);

        samples.Release();
        await first;
        await second;

        Assert.Equal(1, samples.Fetches);
    }

    [Fact]
    public async Task AReadingThatNeverFinishesIsGivenUpOn()
    {
        // Without this the entry sits in the cache for the life of the process and every later
        // caller queues up behind a fetch that is never going to answer.
        var samples = new WaitingSampleSource();
        var service = Service(samples, out _, timeLimit: TimeSpan.FromMilliseconds(50));

        var analysis = await service.AnalyseAsync("4711", true, CancellationToken.None);

        Assert.False(analysis.DescribesTheRecording);
        Assert.True(samples.SawCancellation);
    }

    [Fact]
    public async Task ARecordingGivenUpOnIsTriedAgainLater()
    {
        var samples = new WaitingSampleSource();
        var clock = new SteppableClock();
        var service = Service(samples, out _, clock, TimeSpan.FromMilliseconds(50));

        await service.AnalyseAsync("4711", true, CancellationToken.None);
        Assert.Equal(1, samples.Fetches);

        clock.Advance(TimeSpan.FromSeconds(31));
        samples.Release();

        await Complete(service, samples, "4711", recordingHasFinished: true);

        Assert.Equal(2, samples.Fetches);
    }

    [Fact]
    public async Task TheServerShuttingDownEndsTheReadingWithoutComplaint()
    {
        var samples = new WaitingSampleSource();
        var lifetime = new StoppableLifetime();
        var service = Service(samples, out _, lifetime: lifetime);

        var analysis = service.AnalyseAsync("4711", true, CancellationToken.None);
        await samples.Requested;
        lifetime.Stop();

        Assert.False((await analysis).DescribesTheRecording);
    }

    [Fact]
    public async Task OnlySoManyRecordingsAreRememberedAtOnce()
    {
        // A finished recording's analysis stays true for ever, which is a reason to keep it and
        // not a reason to keep every recording this process was ever asked about.
        var samples = new WaitingSampleSource();
        var service = Service(samples, out _);

        for (var index = 0; index < 300; index++)
        {
            await Complete(service, samples, $"recording-{index}", recordingHasFinished: true);
        }

        Assert.Equal(300, samples.Fetches);
        Assert.True(service.Remembered <= 256, $"remembered {service.Remembered}");
    }

    [Fact]
    public async Task ARecordingThatFellOutOfMemoryIsSimplyReadAgain()
    {
        var samples = new WaitingSampleSource();
        var service = Service(samples, out _);

        await Complete(service, samples, "first", recordingHasFinished: true);

        for (var index = 0; index < 300; index++)
        {
            await Complete(service, samples, $"recording-{index}", recordingHasFinished: true);
        }

        var before = samples.Fetches;
        var analysis = await Complete(service, samples, "first", recordingHasFinished: true);

        Assert.Equal(before + 1, samples.Fetches);
        Assert.True(analysis.DescribesTheRecording);
    }

    [Fact]
    public async Task ForgettingNeverTouchesAReadingThatIsStillRunning()
    {
        // Callers are waiting on it, and dropping it would let the next caller start a second
        // reading of the same recording while the first was still going.
        var slow = new WaitingSampleSource();
        var service = Service(slow, out _);

        var running = service.AnalyseAsync("in-flight", false, CancellationToken.None);
        await slow.Requested;

        for (var index = 0; index < 300; index++)
        {
            slow.ReleaseNext();
            await service.AnalyseAsync($"recording-{index}", true, CancellationToken.None);
        }

        Assert.False(running.IsCompleted);

        slow.Release();
        Assert.True((await running).DescribesTheRecording);

        // One fetch for the one that was running, and one each for the rest -- never a second for
        // the recording that was in flight the whole time.
        Assert.Equal(301, slow.Fetches);
    }

    private static async Task<RecordingAnalysis> Complete(
        RecordingAnalysisService service,
        WaitingSampleSource samples,
        string recordingId,
        bool recordingHasFinished)
    {
        samples.ReleaseNext();
        return await service.AnalyseAsync(recordingId, recordingHasFinished, CancellationToken.None);
    }

    private static RecordingAnalysisService Service(
        WaitingSampleSource samples,
        out List<string> inspections,
        TimeProvider? clock = null,
        TimeSpan? timeLimit = null,
        IHostApplicationLifetime? lifetime = null)
    {
        var recorded = new List<string>();
        inspections = recorded;

        return new RecordingAnalysisService(
            samples,
            RecordingEncoder(recorded),
            NullLoggerFactory.Instance,
            lifetime ?? new StoppableLifetime(),
            clock ?? TimeProvider.System,
            timeLimit ?? TimeSpan.FromMinutes(2));
    }

    private static RecordingAnalysisService Service(
        WaitingSampleSource samples,
        out List<string> inspections,
        TimeProvider clock,
        TimeSpan timeLimit)
        => Service(samples, out inspections, clock, timeLimit, null);

    /// <summary>
    /// A media encoder that answers with one video stream and counts what it was asked about.
    /// </summary>
    private static IMediaEncoder RecordingEncoder(List<string> inspections)
    {
        var proxy = DispatchProxy.Create<IMediaEncoder, EncoderProxy>();
        ((EncoderProxy)(object)proxy).Inspections = inspections;
        return proxy;
    }

    /// <summary>
    /// A sample source that hands out a real, tiny file, but only when let go.
    /// </summary>
    private sealed class WaitingSampleSource : IRecordingSampleSource
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _waiting = [];
        private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _open;

        public int Fetches { get; private set; }

        public bool Fails { get; set; }

        public bool SawCancellation { get; private set; }

        /// <summary>
        /// Gets a task that completes as soon as anything has asked for a sample.
        /// </summary>
        public Task Requested => _requested.Task;

        public async Task<RecordingSample> FetchAsync(string recordingId, CancellationToken cancellationToken)
        {
            TaskCompletionSource gate;
            lock (_gate)
            {
                Fetches++;
                _requested.TrySetResult();

                gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_open)
                {
                    gate.SetResult();
                }
                else
                {
                    _waiting.Add(gate);
                }
            }

            try
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }

            if (Fails)
            {
                throw new HttpRequestException("TVHeadend was not reachable.");
            }

            var path = RecordingSample.CreatePath();
            await File.WriteAllBytesAsync(path, new byte[188], CancellationToken.None);
            return new RecordingSample(path, 188);
        }

        /// <summary>
        /// Lets everything through: whatever is waiting now, and everything asked for afterwards.
        /// </summary>
        public void Release()
        {
            List<TaskCompletionSource> waiting;
            lock (_gate)
            {
                _open = true;
                waiting = [.. _waiting];
                _waiting.Clear();
            }

            foreach (var gate in waiting)
            {
                gate.TrySetResult();
            }
        }

        /// <summary>
        /// Lets everything asked for from now on through, leaving anything already waiting to go
        /// on waiting.
        /// </summary>
        public void ReleaseNext()
        {
            lock (_gate)
            {
                _open = true;
            }
        }
    }

    /// <summary>
    /// A clock that only moves when a test moves it.
    /// </summary>
    /// <remarks>
    /// Only <see cref="GetUtcNow"/> is overridden, so timers -- which the analysis time limit uses
    /// -- still run off the real clock. Retention is what these tests drive, and retention is
    /// nothing but a comparison against this.
    /// </remarks>
    private sealed class SteppableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// The host's lifetime, reduced to the one token this service reads.
    /// </summary>
    private sealed class StoppableLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Stop() => _stopping.Cancel();

        public void StopApplication() => Stop();

        public void Dispose() => _stopping.Dispose();
    }

    /// <summary>
    /// Jellyfin's media encoder, reduced to the one call the inspector makes.
    /// </summary>
    public class EncoderProxy : DispatchProxy
    {
        internal List<string>? Inspections { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IMediaEncoder.GetMediaInfo))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            var request = (MediaInfoRequest)args![0]!;
            Inspections?.Add(request.MediaSource.Path ?? string.Empty);

            return Task.FromResult(new MediaInfo
            {
                Container = "mpegts",
                MediaStreams =
                [
                    new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                ],
            });
        }
    }
}
