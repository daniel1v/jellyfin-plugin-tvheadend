using System;
using System.Collections.Generic;
using MediaBrowser.Model.LiveTv;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.Core.Broadcast;
using TVHeadEnd.Core.Dvr;
using TVHeadEnd.LiveTv;
using TVHeadEnd.Tvheadend.Mapping;
using Xunit;

namespace TVHeadEnd.Tests.Core;

/// <summary>
/// How long a recording is, and when it last changed.
/// </summary>
/// <remarks>
/// <para>
/// Both used to come from the times the recording was <em>scheduled</em> for, which is not what is
/// on disk. A recording stopped by hand was published at its planned length -- so a client could
/// seek into minutes nobody ever wrote -- and dated by a stop that never happened, which put its
/// modification time in the future.
/// </para>
/// <para>
/// TVHeadend lists what it actually wrote in <c>files</c>, and serves <c>/dvrfile/&lt;id&gt;</c>
/// from the last of them. That file is the one described here.
/// </para>
/// </remarks>
public class RecordingRuntimeTests
{
    private static readonly DateTime PlannedStart = new(2026, 8, 29, 20, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime PlannedStop = new(2026, 8, 29, 21, 45, 0, DateTimeKind.Utc);

    [Fact]
    public void AFinishedRecordingRunsForAsLongAsItsFileDoes()
    {
        var entry = Entry("completed", File(PlannedStart, PlannedStart.AddMinutes(90)));

        Assert.Equal(TimeSpan.FromMinutes(90), entry.RecordedDuration);
        Assert.Equal(TimeSpan.FromMinutes(90).Ticks, JellyfinDvrMapper.ToRecording(entry).RunTimeTicks);
    }

    [Fact]
    public void ARecordingStoppedEarlyIsAsLongAsItGotAndNotAsLongAsItWasMeantTo()
    {
        // Somebody pressed stop after twenty minutes of a ninety-minute booking. Publishing the
        // ninety told every client the file reached an end it was never written to.
        var entry = Entry("completed", File(PlannedStart, PlannedStart.AddMinutes(20)));

        var recording = JellyfinDvrMapper.ToRecording(entry);

        Assert.Equal(TimeSpan.FromMinutes(20).Ticks, recording.RunTimeTicks);
        Assert.NotEqual((PlannedStop - PlannedStart).Ticks, recording.RunTimeTicks);
    }

    [Fact]
    public void TheFileIsMeasuredFromWhenItReallyBeganNotFromWhenItWasBooked()
    {
        // Padding, a late tune, a server that started the file early -- the file's own start is the
        // only one the bytes were written against.
        var fileStart = PlannedStart.AddMinutes(-3);
        var entry = Entry("completed", File(fileStart, fileStart.AddMinutes(96)));

        Assert.Equal(TimeSpan.FromMinutes(96), entry.RecordedDuration);
        Assert.Equal(TimeSpan.FromMinutes(96).Ticks, JellyfinDvrMapper.ToRecording(entry).RunTimeTicks);
    }

    [Fact]
    public void ARecordingStillBeingWrittenHasNoLengthYet()
    {
        // A file that is still growing has no finished duration, and the scheduled one is not a
        // stand-in: stating it would tell a client the file already reaches an end that has not
        // been written. Unknown length is what chase playback needs to be told.
        var entry = Entry("recording", File(PlannedStart, stop: null));

        Assert.Null(entry.RecordedDuration);
        Assert.Null(JellyfinDvrMapper.ToRecording(entry).RunTimeTicks);
    }

    [Fact]
    public void ARecordingStillBeingWrittenIsStillPlayable()
    {
        // The unknown length must not cost it its place in the library -- chase playback is exactly
        // watching a recording that has not finished.
        var entry = Entry("recording", File(PlannedStart, stop: null));

        Assert.True(JellyfinDvrMapper.IsRecording(entry));
        Assert.Equal(RecordingStatus.InProgress, JellyfinDvrMapper.ToRecording(entry).Status);
    }

    [Fact]
    public void AnEntryOfSeveralFilesIsDescribedAsTheOneTvheadendServes()
    {
        // TVHeadend hands over the last file for /dvrfile/<id>, so that is the one whose length is
        // published. Adding the parts together would describe something no request can produce.
        var first = File(PlannedStart, PlannedStart.AddMinutes(50));
        var last = File(PlannedStart.AddMinutes(52), PlannedStart.AddMinutes(90));

        var entry = Entry("completed", first, last);

        Assert.Equal(TimeSpan.FromMinutes(38), entry.RecordedDuration);
        Assert.Equal(TimeSpan.FromMinutes(38).Ticks, JellyfinDvrMapper.ToRecording(entry).RunTimeTicks);

        // Not the first, and not the two joined together.
        Assert.NotEqual(TimeSpan.FromMinutes(50), entry.RecordedDuration);
        Assert.NotEqual(TimeSpan.FromMinutes(88), entry.RecordedDuration);
    }

    [Fact]
    public void AFinishedRecordingWithNoFileTimesFallsBackToWhatWasPlanned()
    {
        // A server too old to send the list, or one that sent it without usable times. The plan is
        // then the only account of the recording there is, and it is better than nothing for
        // something that is over.
        var withoutFiles = Entry("completed");
        var withUselessFile = Entry("completed", File(start: null, stop: null));

        Assert.Null(withoutFiles.RecordedDuration);
        Assert.Equal(
            (PlannedStop - PlannedStart).Ticks,
            JellyfinDvrMapper.ToRecording(withoutFiles).RunTimeTicks);

        Assert.Equal(
            (PlannedStop - PlannedStart).Ticks,
            JellyfinDvrMapper.ToRecording(withUselessFile).RunTimeTicks);
    }

    [Fact]
    public void ARunningRecordingWithNoFileTimesFallsBackToNothingAtAll()
    {
        // The fallback is for recordings that are over. Applying it to one still running would put
        // back the very number this replaced.
        var entry = Entry("recording");

        Assert.Null(JellyfinDvrMapper.ToRecording(entry).RunTimeTicks);
    }

    [Fact]
    public void AFileClosedBeforeItOpenedIsNoAnswer()
    {
        // Nonsense times are not a duration. Negative or zero means the server said nothing usable.
        var entry = Entry("completed", File(PlannedStart, PlannedStart.AddMinutes(-5)));

        Assert.Null(entry.RecordedDuration);
    }

    [Fact]
    public void ARecordingStoppedEarlyIsNotDatedInTheFutureItNeverReached()
    {
        // The scheduled stop is a time that never happened. Dating the item by it claimed the
        // recording had been modified after the last thing that actually touched it.
        var stoppedAt = PlannedStart.AddMinutes(20);
        var entry = Entry("completed", File(PlannedStart, stoppedAt));

        var recording = JellyfinDvrMapper.ToRecording(entry);

        Assert.Equal(stoppedAt, recording.DateLastUpdated);
        Assert.True(recording.DateLastUpdated < PlannedStop);
    }

    [Fact]
    public void TheRealActivityTimeRisesAsTheRecordingProgresses()
    {
        // The file opened, then the file closed. Both have happened by the time they are reported,
        // which is what makes this the truthful half of what used to be one overloaded value.
        var running = Entry("recording", File(PlannedStart, stop: null));
        var finished = Entry("completed", File(PlannedStart, PlannedStart.AddMinutes(20)));

        Assert.Equal(PlannedStart, running.RecordedActivityUtc);
        Assert.Equal(PlannedStart.AddMinutes(20), finished.RecordedActivityUtc);
        Assert.True(finished.RecordedActivityUtc > running.RecordedActivityUtc);
    }

    [Fact]
    public void AnEntryWithNoFileHasDoneNothingAndSaysSo()
    {
        // Not the scheduled start, and not the scheduled stop. Nothing has happened, so the
        // truthful answer is that there is no time to give -- the version marker Jellyfin compares
        // is built separately and has the scheduled times to fall back on.
        Assert.Null(Entry("completed").RecordedActivityUtc);
        Assert.Null(Entry("scheduled").RecordedActivityUtc);
    }

    [Fact]
    public void APrePaddedRecordingDoesNotClaimItsScheduledStartHasArrived()
    {
        // Pre-padding opens the file before the booking begins, so while it runs the scheduled
        // start is still in the future. Reporting that as the last thing that happened was the
        // failure of using one value for both jobs.
        var openedEarly = PlannedStart.AddMinutes(-5);
        var entry = Entry("recording", File(openedEarly, stop: null));

        Assert.Equal(openedEarly, entry.RecordedActivityUtc);
        Assert.True(entry.RecordedActivityUtc < entry.StartUtc);
    }

    [Fact]
    public void TheListingAndTheMediaSourceAreToldTheSameLength()
    {
        // Two independent answers to how long a recording is are two answers that can disagree, and
        // the client is handed both -- one on the listed item, one on the source it plays.
        // RecordingsChannel.Runtime is what fills in each of them, and it recomputes nothing: it
        // reads the value the projection worked out from the file.
        var recording = JellyfinDvrMapper.ToRecording(
            Entry("completed", File(PlannedStart, PlannedStart.AddMinutes(20))));

        Assert.Equal(TimeSpan.FromMinutes(20).Ticks, RecordingsChannel.Runtime(recording));
        Assert.Equal(recording.RunTimeTicks, RecordingsChannel.Runtime(recording));
    }

    [Fact]
    public void TheChannelDoesNotWorkOutALengthOfItsOwnFromTheScheduledTimes()
    {
        // It used to be EndDate - StartDate, computed in the channel from the planned times, which
        // is how a recording could be listed at one length and played at another.
        var recording = JellyfinDvrMapper.ToRecording(
            Entry("completed", File(PlannedStart, PlannedStart.AddMinutes(20))));

        Assert.Equal(PlannedStart, recording.StartDate);
        Assert.Equal(PlannedStop, recording.EndDate);
        Assert.NotEqual((recording.EndDate - recording.StartDate).Ticks, RecordingsChannel.Runtime(recording));
    }

    [Fact]
    public void TheFilesAreReadFromWhatTvheadendAnnounced()
    {
        var entry = Entry("completed", File(PlannedStart, PlannedStart.AddMinutes(20), size: 1234567));

        var file = Assert.Single(entry.Files);

        Assert.Equal(PlannedStart, file.StartUtc);
        Assert.Equal(PlannedStart.AddMinutes(20), file.StopUtc);
        Assert.Equal(1234567, file.Size);
    }

    [Fact]
    public void AnUpdateAboutSomethingElseDoesNotForgetTheFiles()
    {
        // TVHeadend sends only what changed. Replacing the list on every update would empty it
        // whenever the state alone moved on -- and the move to completed is the update that
        // settles the stop this all rests on.
        var running = Entry("recording", File(PlannedStart, stop: null));

        var update = new HtspMessage();
        update.Set("id", 1L);
        update.Set("error", "None");

        var merged = DvrEntryMapper.Merge(running, DvrEntryMapper.FromMessage(update)!, update);

        Assert.Single(merged.Files);
        Assert.Equal(PlannedStart, merged.PlayableFile!.StartUtc);
    }

    [Fact]
    public void TheUpdateThatFinishesARecordingBringsItsRealLength()
    {
        // The whole point of the merge keeping the list: state and files arrive in one message when
        // a recording ends, and the entry has to come out of it with both.
        var running = Entry("recording", File(PlannedStart, stop: null));

        var update = Message(1, "completed", File(PlannedStart, PlannedStart.AddMinutes(20)));

        var merged = DvrEntryMapper.Merge(running, DvrEntryMapper.FromMessage(update)!, update);

        Assert.Equal(DvrState.Completed, merged.State);
        Assert.Equal(TimeSpan.FromMinutes(20), merged.RecordedDuration);
    }

    private static DvrEntry Entry(string state, params HtspMessage[] files)
        => DvrEntryMapper.FromMessage(Message(1, state, files))!;

    private static HtspMessage Message(int id, string state, params HtspMessage[] files)
    {
        var message = new HtspMessage();
        message.Set("id", (long)id);
        message.Set("state", state);
        message.Set("start", ToUnixTime(PlannedStart));
        message.Set("stop", ToUnixTime(PlannedStop));

        if (files.Length > 0)
        {
            message.Set("files", (IEnumerable<HtspMessage>)files);
        }

        return message;
    }

    private static HtspMessage File(DateTime? start, DateTime? stop, long? size = null)
    {
        var file = new HtspMessage();
        file.Set("filename", "/recordings/tatort.ts");

        if (start is { } begin)
        {
            file.Set("start", ToUnixTime(begin));
        }

        if (stop is { } end)
        {
            file.Set("stop", ToUnixTime(end));
        }

        if (size is { } bytes)
        {
            file.Set("size", bytes);
        }

        return file;
    }

    private static long ToUnixTime(DateTime value)
        => ((DateTimeOffset)DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
