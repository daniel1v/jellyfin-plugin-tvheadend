using System;
using MediaBrowser.Model.LiveTv;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.Domain;
using TVHeadEnd.Tvheadend.Catalogs;
using Xunit;

namespace TVHeadEnd.Tests.Domain;

public class DvrEntryTests
{
    [Fact]
    public void AnEntryIsReadFromWhatTvheadendAnnounced()
    {
        var entry = DvrEntry.FromMessage(Message(
            id: 4711,
            state: "completed",
            title: "Tatort",
            startExtraMinutes: 2,
            stopExtraMinutes: 5));

        Assert.NotNull(entry);
        Assert.Equal("4711", entry!.Id);
        Assert.Equal(DvrState.Completed, entry.State);
        Assert.Equal("Tatort", entry.Title);

        // TVHeadend states padding in minutes.
        Assert.Equal(TimeSpan.FromMinutes(2), entry.PrePadding);
        Assert.Equal(TimeSpan.FromMinutes(5), entry.PostPadding);
    }

    [Fact]
    public void AStateTheServerDoesNotSendIsNotInvented()
    {
        // TVHeadend has no cancelled or failed state, so an unfamiliar one is admitted as
        // unknown rather than mapped onto something that looks plausible.
        var entry = DvrEntry.FromMessage(Message(id: 1, state: "something-new"));

        Assert.Equal(DvrState.Unknown, entry!.State);
    }

    [Theory]
    [InlineData("scheduled", true, false)]
    [InlineData("recording", false, true)]
    [InlineData("completed", false, true)]
    [InlineData("missed", false, true)]
    public void TheTwoJellyfinViewsSplitTheSameEntry(string state, bool isTimer, bool isRecording)
    {
        var entry = DvrEntry.FromMessage(Message(id: 1, state: state))!;

        Assert.Equal(isTimer, JellyfinDvrMapper.IsTimer(entry));
        Assert.Equal(isRecording, JellyfinDvrMapper.IsRecording(entry));
    }

    [Fact]
    public void ARecordingWhoseFileIsGoneIsNotOffered()
    {
        // TVHeadend keeps the entry of a deleted recording, still marked completed. Only the
        // error tells them apart, and listing one would offer something unplayable.
        var message = Message(id: 1, state: "completed");
        message.Set("error", "File missing");

        var entry = DvrEntry.FromMessage(message)!;

        Assert.True(entry.FileIsMissing);
        Assert.False(JellyfinDvrMapper.IsRecording(entry));
    }

    [Fact]
    public void AnUpdateChangesOnlyWhatItMentions()
    {
        // TVHeadend sends just the fields that changed. Replacing the entry with the update --
        // which is what parsing each message on its own amounts to -- would leave a recording
        // that has just started with no title, no channel and no times.
        var store = new DvrCatalog(new NullLogger<DvrCatalog>());
        store.Add(Message(id: 7, state: "scheduled", title: "Tagesschau"));

        var update = new HtspMessage();
        update.Set("id", 7);
        update.Set("state", "recording");
        store.Update(update);

        var entry = Assert.Single(store.GetEntries());
        Assert.Equal(DvrState.Recording, entry.State);
        Assert.Equal("Tagesschau", entry.Title);
    }

    [Fact]
    public void ATimerCarriesTheStateJellyfinKnows()
    {
        var entry = DvrEntry.FromMessage(Message(id: 3, state: "scheduled", title: "Tatort"))!;

        var timer = JellyfinDvrMapper.ToTimer(entry);

        Assert.Equal("3", timer.Id);
        Assert.Equal("Tatort", timer.Name);
        Assert.Equal(RecordingStatus.New, timer.Status);
    }

    [Fact]
    public void ARecordingKeepsNoPathBecauseTheFileIsOnTheOtherServer()
    {
        var message = Message(id: 3, state: "completed", title: "Tatort");
        message.Set("path", "/recordings/tatort.ts");

        var recording = JellyfinDvrMapper.ToRecording(DvrEntry.FromMessage(message)!);

        Assert.Equal(string.Empty, recording.Path);
        Assert.Equal(RecordingStatus.Completed, recording.Status);
    }

    [Fact]
    public void ASubtitleMarksTheRecordingAsPartOfASeries()
    {
        var message = Message(id: 3, state: "completed", title: "Tatort");
        message.Set("subtitle", "Der Fall Mustermann");

        var recording = JellyfinDvrMapper.ToRecording(DvrEntry.FromMessage(message)!);

        Assert.True(recording.IsSeries);
        Assert.Equal("Der Fall Mustermann", recording.EpisodeTitle);
    }

    [Fact]
    public void AMessageWithoutAnIdentifierIsRefused()
    {
        Assert.Null(DvrEntry.FromMessage(new HtspMessage()));
    }


    [Fact]
    public void ARecordingCarriesAModificationDateSoJellyfinCanRefreshIt()
    {
        // Jellyfin re-saves a channel item, and with it the description of what the item
        // contains, only when the item is new or something it compares has changed. The
        // modification date is the only one of those a plugin controls, so leaving it unset
        // freezes the description of every existing recording forever.
        var entry = DvrEntry.FromMessage(Message(id: 3, state: "completed", title: "Tatort"))!;

        var recording = JellyfinDvrMapper.ToRecording(entry);

        Assert.NotEqual(default, recording.DateLastUpdated);
        Assert.Equal(entry.StopUtc, recording.DateLastUpdated);
    }
    private static HtspMessage Message(
        int id,
        string state,
        string? title = null,
        long startExtraMinutes = 0,
        long stopExtraMinutes = 0)
    {
        var message = new HtspMessage();
        message.Set("id", id);
        message.Set("state", state);
        message.Set("channel", 1234);
        message.Set("start", 1786889738L);
        message.Set("stop", 1786889978L);
        message.Set("startExtra", startExtraMinutes);
        message.Set("stopExtra", stopExtraMinutes);
        if (title is not null)
        {
            message.Set("title", title);
        }

        return message;
    }

    private sealed class NullLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
