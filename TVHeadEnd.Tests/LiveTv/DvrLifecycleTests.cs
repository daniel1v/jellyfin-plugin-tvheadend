using System;
using System.Globalization;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Tvheadend.Htsp;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.Core.Broadcast;
using TVHeadEnd.Core.Dvr;
using TVHeadEnd.LiveTv;
using TVHeadEnd.Tvheadend;
using TVHeadEnd.Tvheadend.Catalogs;
using TVHeadEnd.Tvheadend.Mapping;
using Xunit;

namespace TVHeadEnd.Tests.LiveTv;

/// <summary>
/// What a single recording does between being asked for and being over: which request creates it,
/// which identifier it is known by afterwards, and which request ends it.
/// </summary>
/// <remarks>
/// TVHeadend has one entry for the whole of that life and two different verbs for ending it, so
/// every one of these decisions is taken against the state the server last announced -- never
/// against what the plugin last asked for.
/// </remarks>
public class DvrLifecycleTests
{
    [Fact]
    public void AScheduledRecordingIsCancelled()
    {
        var catalog = Catalog(("7", "scheduled"));

        Assert.Equal("cancelDvrEntry", TvheadendDvr.ChooseCancelMethod(catalog.Find("7")));
    }

    [Fact]
    public void ARunningRecordingIsStoppedRatherThanCancelled()
    {
        // The one that matters. Cancelling a running entry throws away what has been recorded;
        // stopping it keeps the file and lets the entry finish as a recording, which is what a
        // viewer pressing stop is asking for.
        var catalog = Catalog(("7", "recording"));

        Assert.Equal("stopDvrEntry", TvheadendDvr.ChooseCancelMethod(catalog.Find("7")));
    }

    [Fact]
    public void AnEntryTheServerHasNotAnnouncedIsCancelled()
    {
        // Nothing has said it started, so there is nothing to stop.
        Assert.Equal("cancelDvrEntry", TvheadendDvr.ChooseCancelMethod(null));
        Assert.Equal("cancelDvrEntry", TvheadendDvr.ChooseCancelMethod(Catalog().Find("7")));
    }

    [Fact]
    public void TheCatalogAnswersForOneEntryByItsIdentifier()
    {
        var catalog = Catalog(("7", "scheduled"), ("8", "recording"));

        Assert.Equal(DvrState.Scheduled, catalog.Find("7")!.State);
        Assert.Equal(DvrState.Recording, catalog.Find("8")!.State);
        Assert.Null(catalog.Find("9"));
        Assert.Null(catalog.Find(null));
        Assert.Null(catalog.Find(string.Empty));
    }

    [Fact]
    public void ATimerMadeFromTheGuideNamesTheEventItWasMadeFrom()
    {
        // TvheadendGuide reports TVHeadend's own eventId as the program identifier, so this is the
        // server's number coming back. Binding the entry to the event is what lets TVHeadend
        // follow a broadcast that moves.
        var request = TvheadendDvr.BuildCreateTimerRequest(Timer(programId: "12345"), Settings);

        Assert.Equal(12345, request.GetInt32("eventId"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("a1b2c3d4-0000-0000-0000-000000000000")]
    public void ATimerWithNoUsableEventIsStillAnOrdinaryManualTimer(string? programId)
    {
        // A manual timer has no event at all, and a program identifier in some other shape is not
        // one either. Sending it as an eventId would bind the recording to whatever event happened
        // to carry that number.
        var request = TvheadendDvr.BuildCreateTimerRequest(Timer(programId), Settings);

        Assert.False(request.Contains("eventId"));

        // And everything the server needs to record it without one is there.
        Assert.Equal(42, request.GetInt32("channelId"));
        Assert.Equal("Tatort", request.GetString("title"));
        Assert.NotNull(request.GetInt64("start"));
        Assert.NotNull(request.GetInt64("stop"));
    }

    [Fact]
    public void TheManualFieldsTravelEvenWhenTheEventIsKnown()
    {
        // The event is preferred, not relied upon: a server that has aged the event out, or one
        // that ignores the field, still has the times, the channel and the title to record from.
        var timer = Timer("12345");
        var request = TvheadendDvr.BuildCreateTimerRequest(timer, Settings);

        Assert.Equal(12345, request.GetInt32("eventId"));
        Assert.Equal(42, request.GetInt32("channelId"));
        Assert.Equal("Tatort", request.GetString("title"));
        Assert.Equal("A body in Muenster.", request.GetString("description"));
        Assert.Equal(ToUnixTime(timer.StartDate), request.GetInt64("start"));
        Assert.Equal(ToUnixTime(timer.EndDate), request.GetInt64("stop"));

        // Padding is stated in minutes, and the profile and priority are the ones configured.
        Assert.Equal(2, request.GetInt32("startExtra"));
        Assert.Equal(5, request.GetInt32("stopExtra"));
        Assert.Equal(Settings.Priority, request.GetInt32("priority"));
        Assert.Equal(Settings.DvrProfile, request.GetString("configName"));
    }

    [Fact]
    public void TheIdentifierTvheadendGaveTheNewEntryIsWhatComesBack()
    {
        // Jellyfin keeps this as the timer's external identifier and asks for the timer by it
        // afterwards. Left to invent one, every later update and cancel names an entry the server
        // has never heard of.
        var reply = new HtspMessage();
        reply.Set("success", 1);
        reply.Set("id", 4711);

        Assert.Equal("4711", TvheadendDvr.ReadNewEntryId(reply));
    }

    [Fact]
    public void ASeriesRuleIsNamedByAUuidRatherThanANumber()
    {
        var reply = new HtspMessage();
        reply.Set("success", 1);
        reply.Set("id", "a1b2c3d4e5f6");

        Assert.Equal("a1b2c3d4e5f6", TvheadendDvr.ReadNewEntryId(reply));
    }

    [Fact]
    public void AReplyThatNamesNothingAnswersNothing()
    {
        var reply = new HtspMessage();
        reply.Set("success", 1);

        Assert.Null(TvheadendDvr.ReadNewEntryId(reply));
    }

    [Fact]
    public void AnAcceptedCreateThatNamesNoEntryIsAFailure()
    {
        // Jellyfin keeps what comes back as the timer's own identifier, so an empty one is not a
        // smaller answer than a real one: it is a timer recorded under nothing, which cannot be
        // found, updated or cancelled again. Handing back string.Empty made that look like success.
        var reply = new HtspMessage();
        reply.Set("success", 1);

        var broken = Assert.Throws<HtspException>(
            () => TvheadendDvr.RequireNewEntryId(reply, "a recording"));

        Assert.Contains("a recording", broken.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAcceptedCreateThatNamesAnEntryHandsItOverUnchanged()
    {
        var reply = new HtspMessage();
        reply.Set("success", 1);
        reply.Set("id", 4711);

        Assert.Equal("4711", TvheadendDvr.RequireNewEntryId(reply, "a recording"));
    }

    [Fact]
    public void AnAcceptedSeriesRuleThatNamesNoEntryIsAFailureToo()
    {
        var reply = new HtspMessage();
        reply.Set("success", 1);

        Assert.Throws<HtspException>(() => TvheadendDvr.RequireNewEntryId(reply, "a series rule"));
    }

    [Fact]
    public void ARefusalIsStillARefusalRatherThanAMissingIdentifier()
    {
        // The two failures are different and must stay so: one is the server saying no, the other
        // is the server saying yes without saying what to. EnsureAccepted runs first, so a refusal
        // never reaches the identifier check and keeps the reason TVHeadend gave.
        var reply = new HtspMessage();
        reply.Set("success", 0);
        reply.Set("error", "Access denied");

        var refused = Assert.Throws<HtspException>(() => TvheadendDvr.EnsureAccepted(reply));

        Assert.Contains("Access denied", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedCreateIsStillAFailureEvenWhereItCarriesAnIdentifier()
    {
        var reply = new HtspMessage();
        reply.Set("success", 0);
        reply.Set("error", "Access denied");
        reply.Set("id", 4711);

        var refused = Assert.Throws<HtspException>(() => TvheadendDvr.EnsureAccepted(reply));
        Assert.Contains("Access denied", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASuccessfulCreateDoesNotPutTheEntryInTheCatalogItself()
    {
        // The catalog is TVHeadend's account of the DVR and nothing else. A reply saying the
        // request was accepted is not the server announcing the entry, and writing one here would
        // give the plugin a second, earlier, differently shaped source for the same fact -- which
        // is how the two come to disagree. It appears when dvrEntryAdd arrives, exactly as a
        // recording made in TVHeadend's own web interface does.
        var catalog = Catalog();
        var revision = catalog.Revision;

        var reply = new HtspMessage();
        reply.Set("success", 1);
        reply.Set("id", 4711);

        Assert.Equal("4711", TvheadendDvr.ReadNewEntryId(reply));
        Assert.Equal(0, catalog.Count);
        Assert.Null(catalog.Find("4711"));
        Assert.Equal(revision, catalog.Revision);

        // Only the announcement puts it there.
        catalog.Add(Announcement("4711", "scheduled"));

        Assert.Equal(DvrState.Scheduled, catalog.Find("4711")!.State);
        Assert.NotEqual(revision, catalog.Revision);
    }

    private static TvheadendSettings Settings => new()
    {
        Host = "tvheadend.local",
        HttpPort = 9981,
        HtspPort = 9982,
        UserName = "jellyfin",
        Password = "secret",
        Priority = 2,
        DvrProfile = "default-profile",
        ChannelTypeForOther = "TV",
        LiveBufferSizeMegabytes = 64,
    };

    private static TimerInfo Timer(string? programId) => new()
    {
        ChannelId = "42",
        ProgramId = programId,
        Name = "Tatort",
        Overview = "A body in Muenster.",
        StartDate = new DateTime(2026, 8, 30, 20, 15, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2026, 8, 30, 21, 45, 0, DateTimeKind.Utc),
        PrePaddingSeconds = 120,
        PostPaddingSeconds = 300,
    };

    private static DvrCatalog Catalog(params (string Id, string State)[] entries)
    {
        var catalog = new DvrCatalog(NullLogger<DvrCatalog>.Instance);
        foreach (var (id, state) in entries)
        {
            catalog.Add(Announcement(id, state));
        }

        return catalog;
    }

    private static HtspMessage Announcement(string id, string state)
    {
        var message = new HtspMessage();
        message.Set("id", long.Parse(id, CultureInfo.InvariantCulture));
        message.Set("state", state);
        return message;
    }

    private static long ToUnixTime(DateTime value)
        => ((DateTimeOffset)DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
