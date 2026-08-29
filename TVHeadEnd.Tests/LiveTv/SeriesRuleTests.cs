using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.LiveTv;
using TVHeadEnd.Tvheadend.Catalogs;
using Xunit;
using HtspMessage = Tvheadend.Htsp.Protocol.HtspMessage;

namespace TVHeadEnd.Tests.LiveTv;

/// <summary>
/// A TVHeadend autorec entry read into a Jellyfin series timer, edited, and written back.
/// </summary>
/// <remarks>
/// <para>
/// The two models do not line up, and the gaps used to be filled with things the server had not
/// said: the series identifier was the rule's title, "any time" was inferred from the days of the
/// week, and an end date was invented from how long a finished recording is kept.
/// </para>
/// <para>
/// The way an autorec is written matters as much. TVHeadend applies only the fields a request
/// mentions, so a field left out keeps whatever it had -- which is right for what Jellyfin cannot
/// show and wrong for what Jellyfin has just cleared.
/// </para>
/// </remarks>
public class SeriesRuleTests
{
    /// <summary>
    /// The TVHeadend server sits two hours east of UTC; nothing here runs in that zone.
    /// </summary>
    private static readonly TimeSpan ServerOffset = TimeSpan.FromHours(2);

    private static readonly DateTime Today = new(2026, 8, 29, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ASeriesLinkIsWhatTheRuleIsBoundTo()
    {
        // Not the title. Saying the title was the series meant two unrelated rules that happened
        // to share a name were reported as the same series.
        var timer = Read(Rule(seriesLink: "crid://bds.tv/1234"));

        Assert.Equal("crid://bds.tv/1234", timer.SeriesId);
        Assert.Equal("Tatort", timer.Name);
    }

    [Fact]
    public void ARuleWithNoSeriesLinkClaimsNoSeries()
    {
        var timer = Read(Rule());

        Assert.Null(timer.SeriesId);
    }

    [Fact]
    public void ANewRuleForAKnownSeriesIsBoundToIt()
    {
        var request = Create(new SeriesTimerInfo
        {
            Name = "Tatort",
            SeriesId = "crid://bds.tv/1234",
            RecordAnyChannel = true,
            RecordAnyTime = true,
        });

        Assert.Equal("crid://bds.tv/1234", request.GetString("serieslinkUri"));

        // Both title fields travel, and they are not the same field. The name is what a person
        // calls the rule; the title is the pattern the server matches programme titles with, which
        // it consults only when the link does not settle it.
        Assert.Equal("Tatort", request.GetString("name"));
        Assert.Equal("Tatort", request.GetString("title"));
    }

    [Fact]
    public void ANewRuleWithNoSeriesLinkFallsBackToTheTitleAlone()
    {
        var request = Create(new SeriesTimerInfo { Name = "Tagesschau", RecordAnyTime = true });

        Assert.False(request.Contains("serieslinkUri"));
        Assert.Equal("Tagesschau", request.GetString("name"));
        Assert.Equal("Tagesschau", request.GetString("title"));
    }

    [Theory]
    [InlineData("Law & Order: S.V.U.", @"Law & Order: S\.V\.U\.")]
    [InlineData("Wer wird Millionär?", @"Wer wird Millionär\?")]
    [InlineData("(Un)geklärt", @"\(Un\)geklärt")]
    [InlineData("Extra 3 [HD]", @"Extra 3 \[HD\]")]
    [InlineData("2 + 2", @"2 \+ 2")]
    public void ATitleIsMatchedLiterallyRatherThanAsAPattern(string title, string expected)
    {
        // TVHeadend compiles the title as a regular expression. Sent as it stands, a full stop
        // matches any character and a bracket is a syntax error rather than a bracket.
        var request = Create(new SeriesTimerInfo { Name = title, RecordAnyTime = true });

        Assert.Equal(expected, request.GetString("title"));

        // And the name is the title as somebody would read it, not the pattern.
        Assert.Equal(title, request.GetString("name"));
    }

    [Fact]
    public void ARuleIsNamedTheWayAPersonWroteItAndNotTheWayItIsMatched()
    {
        // The two fields read back apart. Reporting the pattern as the name showed "S\.W\.A\.T\."
        // in the library, and invited the next edit to escape it a second time.
        var timer = Read(Rule(name: "S.W.A.T.", titlePattern: @"S\.W\.A\.T\."));

        Assert.Equal("S.W.A.T.", timer.Name);
    }

    [Fact]
    public void EditingARuleTwiceDoesNotEscapeItsPatternTwice()
    {
        // What the single field made inevitable: each edit read the escaped pattern as the name
        // and escaped it again, so the filter grew a backslash per visit until it matched nothing.
        var rule = Rule(name: "S.W.A.T.", titlePattern: @"S\.W\.A\.T\.");

        var first = Read(rule);
        first.PrePaddingSeconds = 300;
        var firstRequest = Update(first, rule);

        Assert.False(firstRequest.Contains("title"));

        var second = Read(rule);
        second.PostPaddingSeconds = 600;
        var secondRequest = Update(second, rule);

        Assert.False(secondRequest.Contains("title"));

        // The rule keeps the pattern it had, and the name is still readable.
        Assert.Equal("S.W.A.T.", secondRequest.GetString("name"));
        Assert.Equal(@"S\.W\.A\.T\.", rule.TitlePattern);
    }

    [Fact]
    public void ARegexSomebodyWroteByHandSurvivesAJellyfinEdit()
    {
        // Jellyfin has no field for it, so an edit carries no opinion about it. Rebuilding it from
        // the name would replace a pattern its author meant as a pattern.
        var rule = Rule(name: "Sport", titlePattern: "^(Sport|Fußball).*");
        var timer = Read(rule);
        timer.Priority = 3;

        var request = Update(timer, rule);

        Assert.False(request.Contains("title"));
        Assert.Equal("Sport", request.GetString("name"));
    }

    [Fact]
    public void ARuleThatNeverHadAPatternIsGivenOne()
    {
        // A rule made by hand with only a series link, then renamed from Jellyfin. There is
        // nothing to preserve, so the name becomes the pattern -- escaped, as on creation.
        var rule = Rule(name: null, titlePattern: null, seriesLink: "crid://bds.tv/1234");
        var timer = Read(rule);
        timer.Name = "S.W.A.T.";

        var request = Update(timer, rule);

        Assert.Equal(@"S\.W\.A\.T\.", request.GetString("title"));
    }

    [Fact]
    public void ARuleWithNoNameIsShownByItsPattern()
    {
        // Rules made before this plugin knew the difference, and rules made by hand in the
        // server's own interface, have no name. The pattern is all there is to show, and it is
        // shown as it stands rather than unescaped -- a pattern somebody wrote as a pattern is not
        // a title with backslashes in it.
        var timer = Read(Rule(name: null, titlePattern: "^Tatort"));

        Assert.Equal("^Tatort", timer.Name);
    }

    [Fact]
    public void AnEscapedTitleNoLongerMatchesWhatItShouldNot()
    {
        // The point of the escaping, stated as the behaviour rather than as the spelling: the
        // pattern that goes to the server matches its own title and not a different programme
        // that only looks like it through a regular expression's eyes.
        var pattern = TvheadendDvr.EscapeForTitleMatch("S.W.A.T.");

        Assert.Matches(pattern, "S.W.A.T.");
        Assert.DoesNotMatch(pattern, "SxWxAxTx");
    }

    [Fact]
    public void ARuleWithASeriesLinkSurvivesAnEditThatNeverSawIt()
    {
        // Jellyfin's series timer DTO has no field for the series identifier, so what comes back
        // from an edit has none -- see LiveTvDtoService.GetSeriesTimerInfo. Read from the server's
        // own copy instead, or every edit would unbind the rule from its series.
        var rule = Rule(seriesLink: "crid://bds.tv/1234");
        var edited = Read(rule);
        edited.SeriesId = null;
        edited.PrePaddingSeconds = 300;

        var request = Update(edited, rule);

        Assert.Equal("crid://bds.tv/1234", request.GetString("serieslinkUri"));
    }

    [Fact]
    public void AnAnyTimeRuleGoesBackAsAnyTime()
    {
        var rule = Rule(start: -1, startWindow: -1);
        var timer = Read(rule);

        Assert.True(timer.RecordAnyTime);

        var request = Update(timer, rule);

        Assert.Equal(-1, request.GetInt32("start"));
        Assert.Equal(-1, request.GetInt32("startWindow"));
    }

    [Fact]
    public void ATimeRestrictedRuleComesBackWithTheVerySameMinutes()
    {
        // 20:15 to 20:45 on the server's clock. Read into Jellyfin, returned unedited, and it has
        // to reach TVHeadend as the same two numbers -- anything else silently moves everybody's
        // recordings.
        var rule = Rule(start: 1215, startWindow: 1245);
        var timer = Read(rule);

        Assert.False(timer.RecordAnyTime);

        var request = Update(timer, rule);

        Assert.Equal(1215, request.GetInt32("start"));
        Assert.Equal(1245, request.GetInt32("startWindow"));
    }

    [Fact]
    public void TheServersWallClockSurvivesAJellyfinInADifferentZone()
    {
        // The bug this replaced read the minutes as UTC. A server in Berlin and a Jellyfin
        // container in UTC then disagreed by two hours, and every edit moved the rule.
        var rule = Rule(start: 1215, startWindow: 1245);
        var timer = Read(rule);

        // What Jellyfin holds is an instant, and it is two hours behind the server's wall clock.
        Assert.Equal(new TimeSpan(18, 15, 0), timer.StartDate.TimeOfDay);

        // Converted back with the server's offset, it is 20:15 again.
        Assert.Equal(1215, SeriesRuleCatalog.ToMinutesFromMidnight(timer.StartDate, ServerOffset));

        // And read with this process's idea of time it would not be.
        Assert.NotEqual(1215, SeriesRuleCatalog.ToMinutesFromMidnight(timer.StartDate, TimeSpan.Zero));
    }

    [Fact]
    public void AWindowAcrossMidnightStaysAWindowAcrossMidnight()
    {
        // 23:30 to 00:10 is forty minutes, not minus 1,400.
        var rule = Rule(start: 1410, startWindow: 10);
        var timer = Read(rule);

        Assert.Equal(TimeSpan.FromMinutes(40), timer.EndDate - timer.StartDate);

        var request = Update(timer, rule);

        Assert.Equal(1410, request.GetInt32("start"));
        Assert.Equal(10, request.GetInt32("startWindow"));
    }

    [Fact]
    public void AWindowAtMidnightItselfSurvives()
    {
        var rule = Rule(start: 0, startWindow: 30);
        var timer = Read(rule);

        var request = Update(timer, rule);

        Assert.Equal(0, request.GetInt32("start"));
        Assert.Equal(30, request.GetInt32("startWindow"));
    }

    [Fact]
    public void RecordingOnEveryDayIsSentAsEveryDay()
    {
        // The field is always sent, including when it means no restriction. Leaving it out left
        // the old filter in place, so a rule somebody had narrowed to Mondays could never be
        // widened again from Jellyfin.
        var request = Create(new SeriesTimerInfo
        {
            Name = "Tatort",
            RecordAnyTime = true,
            Days = [.. Enum.GetValues<DayOfWeek>()],
        });

        Assert.Equal(0x7F, request.GetInt32("daysOfWeek"));
    }

    [Fact]
    public void ARuleWithNoDaysAtAllIsSentAsEveryDay()
    {
        var request = Create(new SeriesTimerInfo { Name = "Tatort", RecordAnyTime = true, Days = [] });

        Assert.Equal(0x7F, request.GetInt32("daysOfWeek"));
    }

    [Fact]
    public void SomeOfTheDaysComeBackAsTheSameDays()
    {
        var rule = Rule(daysOfWeek: 0x01 | 0x04 | 0x40);
        var timer = Read(rule);

        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Sunday], timer.Days);

        var request = Update(timer, rule);

        Assert.Equal(0x01 | 0x04 | 0x40, request.GetInt32("daysOfWeek"));
    }

    [Fact]
    public void WideningARuleBackToEveryDayReachesTheServer()
    {
        // The failure the always-send rule exists for: a Monday-only rule the user has just set
        // back to every day.
        var rule = Rule(daysOfWeek: 0x01);
        var timer = Read(rule);
        timer.Days = [.. Enum.GetValues<DayOfWeek>()];

        var request = Update(timer, rule);

        Assert.Equal(0x7F, request.GetInt32("daysOfWeek"));
    }

    [Fact]
    public void TheDaysOfTheWeekSayNothingAboutTheTimeOfDay()
    {
        // They used to: a rule that ran on every day was reported as "any time", which is a
        // different question and made a 20:15 rule look unrestricted.
        var rule = Rule(daysOfWeek: 0x7F, start: 1215, startWindow: 1245);

        Assert.False(Read(rule).RecordAnyTime);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void TheBroadcastTypesJellyfinHasAWordForGoBackUnchanged(int broadcastType, bool recordNewOnly)
    {
        var rule = Rule(broadcastType: broadcastType);
        var timer = Read(rule);

        Assert.Equal(recordNewOnly, timer.RecordNewOnly);

        var request = Update(timer, rule);

        Assert.Equal(broadcastType, request.GetInt32("broadcastType"));
    }

    [Fact]
    public void ABroadcastTypeJellyfinCannotShowIsLeftAlone()
    {
        // TVHeadend has more of these than Jellyfin has words for. One it cannot show comes back
        // as RecordNewOnly = false, which is not the user asking for "all" -- writing that would
        // silently reset a rule nobody edited.
        var rule = Rule(broadcastType: 2);
        var timer = Read(rule);
        timer.PrePaddingSeconds = 300;

        var request = Update(timer, rule);

        Assert.False(request.Contains("broadcastType"));
    }

    [Fact]
    public void HowManyRecordingsAreKeptRoundTrips()
    {
        var rule = Rule(maxCount: 5);
        var timer = Read(rule);

        Assert.Equal(5, timer.KeepUpTo);

        var request = Update(timer, rule);

        Assert.Equal(5, request.GetInt32("maxCount"));
    }

    [Fact]
    public void ARuleThatKeepsEverythingSaysSo()
    {
        Assert.Equal(0, Read(Rule(maxCount: 0)).KeepUpTo);
        Assert.Equal(0, Read(Rule()).KeepUpTo);
    }

    [Fact]
    public void APrioritySurvivesAnEditOfSomethingElse()
    {
        // It used to be overwritten with the plugin's configured default on every update, so
        // editing the padding of any rule reset its priority.
        var rule = Rule(priority: 4);
        var timer = Read(rule);

        Assert.Equal(4, timer.Priority);

        timer.PostPaddingSeconds = 600;
        var request = Update(timer, rule);

        Assert.Equal(4, request.GetInt32("priority"));
    }

    [Fact]
    public void TheRetentionIsNotAnEndDate()
    {
        // How long a finished recording is kept is not when the rule stops applying, and reporting
        // it as one gave every series timer an expiry it did not have.
        var rule = Rule(retention: 31, start: -1, startWindow: -1);
        var timer = Read(rule);

        Assert.Equal(timer.StartDate, timer.EndDate);
        Assert.True(timer.EndDate < DateTime.UtcNow.AddDays(1));
    }

    [Fact]
    public void APartialUpdateKeepsEverythingItDidNotMention()
    {
        // TVHeadend sends only what changed. Reading each message on its own would leave a rule
        // with nothing but the field that moved.
        var catalog = new SeriesRuleCatalog(NullLogger<SeriesRuleCatalog>.Instance);
        catalog.AddOrUpdate(Announce(Rule(
            name: "Tatort",
            seriesLink: "crid://bds.tv/1234",
            channelId: 42,
            daysOfWeek: 0x01,
            start: 1215,
            startWindow: 1245,
            broadcastType: 1,
            maxCount: 5,
            priority: 4,
            retention: 31)));

        var update = new HtspMessage();
        update.Set("id", "auto-1");
        update.Set("startExtra", 5L);
        catalog.AddOrUpdate(update);

        var rule = catalog.Find("auto-1")!;

        Assert.Equal("Tatort", rule.Name);
        Assert.Equal("crid://bds.tv/1234", rule.SeriesLink);
        Assert.Equal("42", rule.ChannelId);
        Assert.Equal(0x01, rule.DaysOfWeek);
        Assert.Equal(1215, rule.Start);
        Assert.Equal(1245, rule.StartWindow);
        Assert.Equal(1, rule.BroadcastType);
        Assert.Equal(5, rule.MaxCount);
        Assert.Equal(4, rule.Priority);
        Assert.Equal(31, rule.RetentionDays);
        Assert.Equal(5, rule.PrePaddingMinutes);
    }

    [Fact]
    public void AChannelBoundRuleStaysBoundAndAnUnboundOneStaysUnbound()
    {
        var bound = Read(Rule(channelId: 42));

        Assert.False(bound.RecordAnyChannel);
        Assert.Equal("42", bound.ChannelId);

        Assert.True(Read(Rule()).RecordAnyChannel);
    }

    [Fact]
    public void AnUnboundRuleIsWrittenAsAnyChannel()
    {
        var request = Create(new SeriesTimerInfo { Name = "Tatort", RecordAnyChannel = true, RecordAnyTime = true });

        Assert.Equal(-1, request.GetInt32("channelId"));
    }

    [Fact]
    public void TheNoteOnARuleIsWhatJellyfinShowsAsItsOverview()
    {
        // TVHeadend calls it a comment, and an autorec entry has no "description" at all -- the
        // field this used to read was never announced, so every rule's overview was empty.
        var rule = Rule(comment: "Everything with Til Schweiger in it");

        Assert.Equal("Everything with Til Schweiger in it", Read(rule).Overview);
    }

    [Fact]
    public void AnUnrelatedUpdateKeepsTheNote()
    {
        var catalog = new SeriesRuleCatalog(NullLogger<SeriesRuleCatalog>.Instance);
        catalog.AddOrUpdate(Announce(Rule(comment: "Only the good ones")));

        var update = new HtspMessage();
        update.Set("id", "auto-1");
        update.Set("priority", 3L);
        catalog.AddOrUpdate(update);

        Assert.Equal("Only the good ones", catalog.Find("auto-1")!.Comment);
    }

    [Fact]
    public void ANoteEditedInJellyfinReachesTheServer()
    {
        var rule = Rule(comment: "Only the good ones");
        var timer = Read(rule);
        timer.Overview = "All of them after all";

        Assert.Equal("All of them after all", Update(timer, rule).GetString("comment"));
    }

    [Fact]
    public void ANoteCanBeClearedOnPurpose()
    {
        // An empty overview is an answer, not an absence: it is sent, so the note goes away.
        var rule = Rule(comment: "Only the good ones");
        var timer = Read(rule);
        timer.Overview = null;

        Assert.Equal(string.Empty, Update(timer, rule).GetString("comment"));
    }

    [Fact]
    public void ARuleTheServerHasSetToNoDaysStaysAtNoDays()
    {
        // TVHeadend tells no days from every day: zero matches nothing, 0x7F matches everything.
        // Both read back as an empty list in Jellyfin, so which one an empty list means depends on
        // whether the rule already exists.
        var rule = Rule(daysOfWeek: 0);
        var timer = Read(rule);

        Assert.Empty(timer.Days);

        timer.PrePaddingSeconds = 300;

        Assert.Equal(0, Update(timer, rule).GetInt32("daysOfWeek"));
    }

    [Fact]
    public void ADailyRuleStaysDaily()
    {
        var rule = Rule(daysOfWeek: 0x7F);
        var timer = Read(rule);

        Assert.Equal(7, timer.Days.Count);
        Assert.Equal(0x7F, Update(timer, rule).GetInt32("daysOfWeek"));
    }

    [Fact]
    public void ClearingEveryDayOnAnExistingRuleTurnsItOff()
    {
        // The other side of the same coin: on a rule that exists, an empty list is somebody having
        // cleared the days, and TVHeadend's word for that is zero.
        var rule = Rule(daysOfWeek: 0x7F);
        var timer = Read(rule);
        timer.Days = [];

        Assert.Equal(0, Update(timer, rule).GetInt32("daysOfWeek"));
    }

    [Fact]
    public void ANewRuleWithNoDaysChosenIsTheOrdinaryDailyRule()
    {
        // A timer that was never given any days, which is what Jellyfin's defaults produce.
        Assert.Equal(
            0x7F,
            Create(new SeriesTimerInfo { Name = "Tatort", RecordAnyTime = true, Days = [] }).GetInt32("daysOfWeek"));
    }

    [Fact]
    public void AnUnrelatedEditKeepsTheServersOwnStartWindowEvenIfItsClockHasMoved()
    {
        // The daylight saving case. Jellyfin returns the instants published when the offset was
        // one thing; reading them back with the offset now would move the rule by the difference.
        // The rule still wants a window and Jellyfin cannot say anything else about it, so the
        // server's own numbers go back untouched.
        var rule = Rule(start: 1215, startWindow: 1245);
        var timer = SeriesRuleCatalog.ToSeriesTimer(rule, TimeSpan.FromHours(2), Today);

        timer.PrePaddingSeconds = 300;

        var request = HtspMessage.Create("updateAutorecEntry").Set("id", rule.Id);
        TvheadendDvr.ApplySeriesFields(request, timer, rule, TimeSpan.FromHours(1));

        Assert.Equal(1215, request.GetInt32("start"));
        Assert.Equal(1245, request.GetInt32("startWindow"));
    }

    [Fact]
    public void LeavingAnyTimeForAWindowUsesTheClockAsItIsNow()
    {
        // The one edit that does say something about the times, so this is where they are read --
        // with the offset the server reports at that moment.
        var rule = Rule(start: -1, startWindow: -1);
        var timer = Read(rule);
        timer.RecordAnyTime = false;
        timer.StartDate = new DateTime(2026, 8, 29, 18, 15, 0, DateTimeKind.Utc);
        timer.EndDate = timer.StartDate.AddMinutes(30);

        var request = Update(timer, rule);

        // 18:15 UTC is 20:15 on a server two hours east.
        Assert.Equal(1215, request.GetInt32("start"));
        Assert.Equal(1245, request.GetInt32("startWindow"));
    }

    [Fact]
    public void GivingUpAWindowWritesAnyTime()
    {
        var rule = Rule(start: 1215, startWindow: 1245);
        var timer = Read(rule);
        timer.RecordAnyTime = true;

        var request = Update(timer, rule);

        Assert.Equal(-1, request.GetInt32("start"));
        Assert.Equal(-1, request.GetInt32("startWindow"));
    }

    [Fact]
    public void AReadAfterTheClocksChangeUsesTheNewOffset()
    {
        // The read is the one place the current offset is the right answer: it is what turns the
        // server's wall clock into an instant, and the server's wall clock is what moved.
        var rule = Rule(start: 1215, startWindow: 1245);

        var inWinter = SeriesRuleCatalog.ToSeriesTimer(rule, TimeSpan.FromHours(1), Today);
        var inSummer = SeriesRuleCatalog.ToSeriesTimer(rule, TimeSpan.FromHours(2), Today);

        Assert.Equal(new TimeSpan(19, 15, 0), inWinter.StartDate.TimeOfDay);
        Assert.Equal(new TimeSpan(18, 15, 0), inSummer.StartDate.TimeOfDay);
    }

    [Fact]
    public void TheOffsetIsReadFromWhatTheServerReported()
    {
        var reply = new HtspMessage();
        reply.Set("gmtoffset", 120L);

        Assert.Equal(TimeSpan.FromHours(2), TVHeadEnd.Tvheadend.TvheadendConnection.ReadServerOffset(reply));

        // West of GMT.
        reply = new HtspMessage();
        reply.Set("gmtoffset", -300L);
        Assert.Equal(TimeSpan.FromHours(-5), TVHeadEnd.Tvheadend.TvheadendConnection.ReadServerOffset(reply));

        // A server that says nothing is taken to be at UTC, which is what every server was assumed
        // to be before this was asked at all.
        Assert.Equal(TimeSpan.Zero, TVHeadEnd.Tvheadend.TvheadendConnection.ReadServerOffset(new HtspMessage()));
    }

    private static SeriesTimerInfo Read(SeriesRule rule)
        => SeriesRuleCatalog.ToSeriesTimer(rule, ServerOffset, Today);

    private static HtspMessage Create(SeriesTimerInfo info)
    {
        var request = HtspMessage.Create("addAutorecEntry");
        TvheadendDvr.ApplySeriesFields(request, info, existing: null, ServerOffset);
        return request;
    }

    private static HtspMessage Update(SeriesTimerInfo info, SeriesRule existing)
    {
        var request = HtspMessage.Create("updateAutorecEntry").Set("id", existing.Id);
        TvheadendDvr.ApplySeriesFields(request, info, existing, ServerOffset);
        return request;
    }

    private static SeriesRule Rule(
        string? name = "Tatort",
        string? titlePattern = "Tatort",
        string? seriesLink = null,
        int? channelId = null,
        int? daysOfWeek = null,
        int? start = null,
        int? startWindow = null,
        int? retention = null,
        int? priority = null,
        int? broadcastType = null,
        int? maxCount = null,
        string? comment = null)
        => new(
            "auto-1",
            name,
            titlePattern,
            seriesLink,
            channelId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            daysOfWeek,
            start,
            startWindow,
            retention,
            0L,
            0L,
            priority,
            broadcastType,
            maxCount,
            comment);

    /// <summary>
    /// The rule as TVHeadend would announce it, so that the catalog's own reading is exercised
    /// rather than a record built by hand.
    /// </summary>
    private static HtspMessage Announce(SeriesRule rule)
    {
        var message = new HtspMessage();
        message.Set("id", rule.Id);

        Add("name", rule.Name);
        Add("title", rule.TitlePattern);
        Add("comment", rule.Comment);
        Add("serieslinkUri", rule.SeriesLink);
        AddNumber("channel", rule.ChannelId is null ? null : int.Parse(rule.ChannelId, System.Globalization.CultureInfo.InvariantCulture));
        AddNumber("daysOfWeek", rule.DaysOfWeek);
        AddNumber("start", rule.Start);
        AddNumber("startWindow", rule.StartWindow);
        AddNumber("retention", rule.RetentionDays);
        AddNumber("priority", rule.Priority);
        AddNumber("broadcastType", rule.BroadcastType);
        AddNumber("maxCount", rule.MaxCount);

        return message;

        void Add(string field, string? value)
        {
            if (value is not null)
            {
                message.Set(field, value);
            }
        }

        void AddNumber(string field, int? value)
        {
            if (value is { } number)
            {
                message.Set(field, (long)number);
            }
        }
    }
}
