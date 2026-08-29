using System;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using TVHeadEnd.Domain;
using TVHeadEnd.Recordings;
using TVHeadEnd.Streaming;
using Xunit;
using HtspMessage = Tvheadend.Htsp.Protocol.HtspMessage;

namespace TVHeadEnd.Tests.Recordings;

/// <summary>
/// What a bounded sample of a recording is allowed to conclude, and what the endpoint serving it
/// is allowed to differ on between HEAD and GET.
/// </summary>
/// <remarks>
/// <para>
/// Both of these were the same mistake in different places. The plugin read the first few
/// megabytes of a recording, found no H.264 IDR frame, and treated that as proof the recording
/// had none -- then withheld direct play for the whole file, for every viewer, and served a
/// re-encode of its own. A finite probe cannot establish an absence: a broadcast that opens on a
/// recovery point and carries an IDR a minute later looks exactly the same from the front.
/// </para>
/// <para>
/// What replaced it keeps the reading and throws away the conclusions. The published source is
/// the same for everyone and offers every route; a single request, from a client whose decoder is
/// known not to start on what was actually found, has three of Jellyfin's own parameters
/// withdrawn and nothing else. These tests are the fence around that line.
/// </para>
/// <para>
/// The re-encode is what made HEAD and GET disagree. HEAD proxied TVHeadend, which advertises a
/// seekable file of known length; GET could answer with an encoder's output, which has neither.
/// A client that asked first and fetched second was told one thing and handed another.
/// </para>
/// </remarks>
public class RecordingDeliveryTests
{
    [Fact]
    public void NothingInTheDescribedSourceWithholdsDirectPlay()
    {
        // The source a described recording is published as. Every route stays open; which one a
        // client takes is Jellyfin's decision against its device profile, not a verdict reached
        // here from a sample.
        var source = DescribedRecording();

        Assert.True(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
    }

    [Fact]
    public void TheChannelOffersNoWayToDeclareARecordingUnplayable()
    {
        // The mechanism is gone rather than merely unused: there is no longer a member that maps
        // a recording identifier to "must be re-encoded", so nothing can reach that state by
        // accident again. Whether one particular request has to be re-encoded is decided in that
        // request and left there -- see RecordingPlaybackCompatibilityFilter.
        var members = typeof(RecordingsChannel)
            .GetMembers()
            .Select(member => member.Name)
            .ToList();

        Assert.DoesNotContain("RequiresReencode", members);
    }

    [Fact]
    public void WhatIsLearnedAboutARecordingCarriesNoDecisionAboutIt()
    {
        // The analysis is shared between viewers and remembered for as long as the server runs.
        // A verdict stored in it would be one client's answer handed to the next, which is the
        // precise shape of the bug this architecture exists to make impossible.
        var members = typeof(RecordingAnalysis)
            .GetMembers()
            .Select(member => member.Name)
            .ToList();

        Assert.DoesNotContain("RequiresReencode", members);
        Assert.DoesNotContain("SupportsDirectPlay", members);
        Assert.DoesNotContain("SupportsDirectStream", members);
        Assert.DoesNotContain("AndroidCompatible", members);
        Assert.DoesNotContain("IsAndroid", members);
        Assert.DoesNotContain("Client", members);
    }

    [Fact]
    public void EvidenceOfNoIdrChangesNothingAboutThePublishedSource()
    {
        // The strongest form of the same rule. Even handed the evidence that does trigger the
        // workaround, describing a recording only describes it: the routes on offer are the same
        // ones, and the request filter is the only thing that ever withdraws any of them.
        var source = DescribedRecording();
        var analysis = new RecordingAnalysis(
            new InspectedMedia(
                "ts",
                [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" }],
                null,
                null,
                null,
                null),
            null,
            H264EntryPointEvidence.RecoveryOnlyObserved);

        Assert.True(RecordingDescriber.Describe(source, analysis));

        Assert.True(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
    }

    [Fact]
    public void EveryRouteToARecordingAnswersWithTheSameMethodForHeadAndGet()
    {
        // Both verbs land on the same method for every route it has, so they cannot describe the
        // resource differently -- which is the whole reason a second implementation was refused
        // for the neutral URL.
        Assert.Equal(GetRoutes(), HeadRoutes());
    }

    [Fact]
    public void ARecordingIsServedFromAnAddressThatClaimsNoContainer()
    {
        // TVHeadend's DVR profile decides what a recording is, and the answer arrives with the
        // analysis -- long after the address is built. The ".ts" on the end asserted MPEG-TS of
        // every recording, including the Matroska a WebTV profile writes.
        var published = TVHeadEnd.Api.TvHeadendRecordingsController.StreamPathFor("token");

        Assert.Equal("/TVHeadend/Recordings/token/stream", published);

        // And it is a route the controller actually serves, prefix included, rather than a
        // string that merely looks like one.
        Assert.Contains("/TVHeadend/Recordings/{token}/stream", GetRoutes());

    }

    [Fact]
    public void TheOldContainerSpecificAddressStillAnswers()
    {
        // It is written into media sources people already have, and a stored recording that
        // stopped playing would be a worse outcome than a name that overstates its container.
        Assert.Contains("/TVHeadend/Recordings/{token}/stream.ts", GetRoutes());
        Assert.Contains("/TVHeadend/Recordings/{token}/stream.ts", HeadRoutes());

    }

    [Fact]
    public void ARecordingIsAFileToTheClientAndAnAddressToJellyfin()
    {
        // The same split live TV uses. EncodingHelper.AttachMediaSourceInfo prefers EncoderPath
        // and EncoderProtocol whenever both are set, so the server fetches over HTTP while the
        // client is told the plainest thing there is: a whole file it may play as it stands.
        var source = RecordingsChannel.BuildRecordingSource("867835561", "1f6cf027e0f2168c8ffaab722d151bb1", "http://host:8096/TVHeadend/Recordings/t/stream");

        Assert.Equal(MediaProtocol.File, source.Protocol);
        Assert.False(string.IsNullOrEmpty(source.Path));

        Assert.Equal(MediaProtocol.Http, source.EncoderProtocol);
        Assert.Equal("http://host:8096/TVHeadend/Recordings/t/stream", source.EncoderPath);
    }

    [Fact]
    public void TheFileARecordingNamesIsNotOneAnybodyCouldOpenByAccident()
    {
        // Nothing on the server reads it -- AttachMediaSourceInfo takes EncoderPath instead --
        // but a client configured for direct file access resolves what it is given against its
        // own filesystem. A path that looks real is the one that could resolve to something else.
        var source = RecordingsChannel.BuildRecordingSource("867835561", "1f6cf027e0f2168c8ffaab722d151bb1", "http://host:8096/x");

        Assert.DoesNotContain("://", source.Path, StringComparison.Ordinal);
        Assert.False(System.IO.Path.IsPathRooted(source.Path));
    }

    [Fact]
    public void ARecordingStartsOutNamedTheWayALiveChannelIs()
    {
        // The starting assumption only; RecordingDescriber.Describe replaces it with whatever the sample
        // turned out to be. What matters is that the two paths spell the one container alike.
        var source = RecordingsChannel.BuildRecordingSource("867835561", "1f6cf027e0f2168c8ffaab722d151bb1", "http://host:8096/x");

        Assert.Equal("ts", source.Container);
        Assert.Equal(TVHeadEnd.Playback.LiveMediaSource.Container, source.Container);
    }

    [Theory]
    [InlineData("video/MP2T", "video/MP2T")]
    [InlineData("application/octet-stream", "application/octet-stream")]
    [InlineData("video/x-matroska", "video/x-matroska")]
    public void WhatTvheadendCallsARecordingIsWhatTheClientIsTold(string upstream, string expected)
    {
        // TVHeadend stored it, so TVHeadend gets to say what it is. Answering "video/mp2t"
        // regardless was a claim about a container this endpoint never inspects.
        Assert.Equal(expected, TVHeadEnd.Api.TvHeadendRecordingsController.DescribeContent(Answer(upstream)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/html")]
    public void ARecordingTvheadendDidNotDescribeIsPassedOnAsBytes(string? upstream)
    {
        // Nothing is invented from nothing, and a server answering a byte range with a document
        // is describing an error page rather than a recording. Jellyfin's
        // GetStaticRemoteStreamResult falls back to exactly this value itself.
        Assert.Equal("application/octet-stream", TVHeadEnd.Api.TvHeadendRecordingsController.DescribeContent(Answer(upstream)));
    }

    private static System.Net.Http.HttpResponseMessage Answer(string? contentType)
    {
        var response = new System.Net.Http.HttpResponseMessage
        {
            Content = new System.Net.Http.ByteArrayContent([0x47]),
        };

        response.Content.Headers.ContentType = contentType is null
            ? null
            : System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

        return response;
    }

    private static string[] GetRoutes() => Templates<Microsoft.AspNetCore.Mvc.HttpGetAttribute>(a => a.Template);

    private static string[] HeadRoutes() => Templates<Microsoft.AspNetCore.Mvc.HttpHeadAttribute>(a => a.Template);

    private static string[] Templates<T>(Func<T, string?> template)
        where T : Attribute
    {
        var method = typeof(TVHeadEnd.Api.TvHeadendRecordingsController)
            .GetMethod(nameof(TVHeadEnd.Api.TvHeadendRecordingsController.GetRecording));

        Assert.NotNull(method);

        // Read off the class rather than written out again here: a test that repeats the prefix
        // would keep passing if the controller moved.
        var prefix = typeof(TVHeadEnd.Api.TvHeadendRecordingsController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .Single()
            .Template;

        return [.. method!.GetCustomAttributes(typeof(T), false)
            .Cast<T>()
            .Select(attribute => "/" + prefix + "/" + (template(attribute) ?? string.Empty))
            .Order(StringComparer.Ordinal)];

    }


    [Fact]
    public void NothingInThisPluginEncodesARecording()
    {
        // The contract rather than a list of names once used. Reading a recording to find out
        // what it contains is fine and is done in several places; what this plugin must never own
        // is an encoder. When a recording has to be re-encoded it is Jellyfin that does it, with
        // Jellyfin's arguments, and the plugin's own endpoint still serves TVHeadend's file.
        var assembly = typeof(RecordingsChannel).Assembly;
        var names = assembly.GetTypes().Select(type => type.FullName ?? type.Name).ToList();

        Assert.DoesNotContain(names, name => name.Contains("Encoder", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("Transcod", StringComparison.Ordinal));
    }

    [Fact]
    public void ThereIsNoSettingLeftThatForcesARecordingToBeReEncoded()
    {
        var settings = typeof(TVHeadEnd.Configuration.PluginConfiguration)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.DoesNotContain("EnableLegacyH264Fallback", settings);
    }


    [Fact]
    public void AStoredRecordingIsRewrittenExactlyOncePerRevision()
    {
        // ChannelManager rewrites a stored item only when DateModified is strictly later than the
        // date it stored, and it compares no part of MediaSources. So an upgrade that changes how
        // a recording is described reaches existing recordings only through this date.
        var recording = Finished(RealStop);

        // What the version before this one stored for it, and what this one publishes. Derived
        // rather than written out, so raising the revision does not need this test edited -- the
        // property under test is the step, not the number.
        var stored = PublishedAtRevision(recording, SchemaRevision() - 1);
        var now = RecordingsChannel.PublishedDateFor(recording);

        // Once...
        Assert.True(now > stored);

        // ...and then never again, because the recording has not changed and neither has the
        // revision. The rewrite stores what the channel published, and the next listing publishes
        // the same value again -- not later than the stored one, so the item is left alone.
        Assert.False(RecordingsChannel.PublishedDateFor(recording) > now);
    }

    [Fact]
    public void ARecordingTvheadendReallyChangedStillComesThrough()
    {
        // The failure a fixed future date has: it sits above every real date until it is reached,
        // so a genuine update to a recording is masked and never reaches the library. Here the
        // published date rises with what the recording did, so it cannot be masked.
        var before = Finished(RealStop);
        var after = Finished(RealStop.AddSeconds(1));

        Assert.True(RecordingsChannel.PublishedDateFor(after) > RecordingsChannel.PublishedDateFor(before));

        // Including a change smaller than the revision step, which is the case a coarser scheme
        // would swallow.
        Assert.True(
            RecordingsChannel.PublishedDateFor(Finished(RealStop.AddMilliseconds(1)))
            > RecordingsChannel.PublishedDateFor(before));
    }

    [Fact]
    public void ALateInstallationMigratesJustTheSame()
    {
        // The fixed future date only worked for somebody who upgraded before it arrived. Measured
        // from the recording rather than the calendar, the migration holds whenever it is run --
        // here for a recording TVHeadend wrote long after the release that introduced it.
        var muchLater = new DateTime(2028, 5, 4, 9, 30, 0, DateTimeKind.Utc);
        var recording = Recording(muchLater, muchLater.AddHours(1), muchLater.AddHours(1), RecordingStatus.Completed);

        Assert.True(
            RecordingsChannel.PublishedDateFor(recording)
            > PublishedAtRevision(recording, SchemaRevision() - 1));
    }

    [Fact]
    public void ARecordingOlderThanTheFloorIsLiftedToIt()
    {
        // A recording TVHeadend has not touched in years still needs a date the revision can be
        // counted from, and the floor is what gives it one.
        var ancient = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recording = Recording(ancient, ancient.AddHours(1), ancient.AddHours(1), RecordingStatus.Completed);

        Assert.Equal(
            DateFloor().AddDays(SchemaRevision()).AddSeconds(2),
            RecordingsChannel.PublishedDateFor(recording));
    }

    [Fact]
    public void NothingAboutThePublishedDateIsReadOffTheClock()
    {
        // A value derived from the current time would be later than the stored date on every
        // listing, so every recording would be rewritten for ever.
        var first = RecordingsChannel.PublishedDateFor(Finished(RealStop));
        System.Threading.Thread.Sleep(20);
        var second = RecordingsChannel.PublishedDateFor(Finished(RealStop));

        Assert.Equal(first, second);
        Assert.Equal(DateTimeKind.Utc, first.Kind);
    }

    [Fact]
    public void TheOffsetAheadOfTheRecordingMasksNothing()
    {
        // The marker does sit ahead of the recording it describes -- a whole revision step, which
        // is what lets it clear what earlier versions published from the scheduled stop. That is
        // safe only because it is a constant added to a rising anchor rather than a fixed date
        // everything is pinned to: a fixed future date sits above every real change until it is
        // reached, and swallows all of them.
        var published = RecordingsChannel.PublishedDateFor(Finished(RealStop));

        Assert.True(published > RealStop);

        // And the very next thing the recording does still rises above it.
        Assert.True(RecordingsChannel.PublishedDateFor(Finished(RealStop.AddSeconds(1))) > published);
    }

    [Fact]
    public void TheSameRecordingPublishesTheSameDateAfterARestart()
    {
        // Every input comes from TVHeadend. Nothing per-process goes in -- that belongs to the
        // cache key, which must change across restarts, and would rewrite every stored item on
        // every start if it leaked in here.
        var beforeRestart = RecordingsChannel.PublishedDateFor(Finished(RealStop));
        var afterRestart = RecordingsChannel.PublishedDateFor(Finished(RealStop));

        Assert.Equal(beforeRestart, afterRestart);
    }

    [Fact]
    public void TheRevisionIsCountedInWholeDaysSoItClearsWhatOlderSchemesPublished()
    {
        // Earlier versions published from the scheduled stop and stepped in seconds. One
        // increment has to clear both that step and however far short of its booking a recording
        // fell, which is bounded by the booking and unbounded by anything smaller than a day.
        var revision = SchemaRevision();

        Assert.True(revision >= 1);
        Assert.Equal(
            TimeSpan.FromDays(revision),
            RecordingsChannel.PublishedDateFor(Scheduled(DateFloor())) - DateFloor());
    }

    [Fact]
    public void RaisingTheRevisionAlsoDiscardsTheCachedListing()
    {
        // Both halves of an upgrade reaching existing recordings, from one number. Jellyfin caches
        // a channel's listing for three hours under a path built from DataVersion, and the cache
        // key this channel supplies follows TVHeadend's recordings rather than the plugin -- so a
        // version that changed how a recording is described used to be invisible until the cache
        // aged out, with nothing to say so. Measured once: a listing cached at 18:34 was still
        // being served at 21:29, two hours after the change was installed.
        var channel = (RecordingsChannel)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(RecordingsChannel));

        Assert.Contains(
            SchemaRevision().ToString(System.Globalization.CultureInfo.InvariantCulture),
            channel.DataVersion,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordingCutShortAfterTheFloorStillReceivesTheCorrectionMadeForIt()
    {
        // The hard case, and the one a floor cannot solve. This recording was made on the day the
        // floor was set, so the floor does not lift it at all; and it was stopped an hour early,
        // so its real times are an hour below the scheduled stop the previous versions published
        // from. The shortfall is however long the recording had left to run, which no fixed date
        // knows in advance -- so the anchor keeps the scheduled stop in view and the revision step
        // clears the seconds the old schemes added.
        var recording = Finished(RealStop);

        // Schema 5 published from the scheduled stop.
        var storedByScheduledStop = PlannedStop.AddSeconds(5);

        // Schema 6 published from the real activity, floored, which is the same day here.
        var storedByRealActivity = Max(RealStop, DateFloor()).AddSeconds(6);

        var published = RecordingsChannel.PublishedDateFor(recording);

        Assert.True(published > storedByScheduledStop, "Schema 5's value would not be superseded.");
        Assert.True(published > storedByRealActivity, "Schema 6's value would not be superseded.");
    }

    [Fact]
    public void FinishingAnEarlyStoppedRecordingStillMovesTheDateOn()
    {
        // Within one schema version, and with an anchor that does not move: a recording stopped
        // early ends below its scheduled stop, so nothing about its times changes when it
        // finishes. That transition is exactly when the real runtime becomes known and has to be
        // stored, so the state itself has to carry the date forward.
        var running = Recording(PlannedStart, PlannedStop, RealStart, RecordingStatus.InProgress);
        var finished = Recording(PlannedStart, PlannedStop, RealStop, RecordingStatus.Completed);

        Assert.True(
            RecordingsChannel.PublishedDateFor(finished) > RecordingsChannel.PublishedDateFor(running),
            "The final runtime would never be stored.");
    }

    [Fact]
    public void APrePaddedRecordingNeverClaimsItsScheduledStartHasHappened()
    {
        // Pre-padding starts the file before the booking. While that is running the scheduled
        // start is still in the future, so a real-activity time taken from it would state a moment
        // that has not come -- which is what the one combined value used to do.
        var entry = DvrEntry.FromMessage(PrePaddedRunningEntry())!;

        Assert.Equal(RealStart.AddMinutes(-5), entry.RecordedActivityUtc);
        Assert.True(entry.RecordedActivityUtc < entry.StartUtc);

        var recording = JellyfinDvrMapper.ToRecording(entry);

        Assert.Equal(entry.RecordedActivityUtc, recording.DateLastUpdated);

        // The version marker may still sit above it: it is not a claim about the recording, and
        // Jellyfin only ever compares it with itself.
        Assert.True(RecordingsChannel.PublishedDateFor(recording) > DateFloor());
    }

    [Fact]
    public void ARecordingWithNoFileHasNoActivityTimeAtAll()
    {
        // Nothing has happened, so there is nothing truthful to report. The marker still works,
        // because it has the scheduled times and the floor to anchor on.
        var recording = Recording(PlannedStart, PlannedStop, activity: null, RecordingStatus.Completed);

        Assert.Null(recording.DateLastUpdated);
        Assert.Equal(PlannedStart.AddDays(SchemaRevision()).AddSeconds(2), RecordingsChannel.PublishedDateFor(recording));
    }

    private static readonly DateTime PlannedStart = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PlannedStop = new(2026, 8, 29, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RealStart = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RealStop = new(2026, 8, 29, 13, 0, 0, DateTimeKind.Utc);

    private static DateTime Max(DateTime left, DateTime right) => left > right ? left : right;

    private static MyRecordingInfo Finished(DateTime realStop)
        => Recording(PlannedStart, PlannedStop, realStop, RecordingStatus.Completed);

    private static MyRecordingInfo Scheduled(DateTime at)
        => Recording(at, at, activity: null, RecordingStatus.New);

    private static MyRecordingInfo Recording(
        DateTime plannedStart,
        DateTime plannedStop,
        DateTime? activity,
        RecordingStatus status)
        => new()
        {
            Id = "1",
            StartDate = plannedStart,
            EndDate = plannedStop,
            DateLastUpdated = activity,
            Status = status,
        };

    private static HtspMessage PrePaddedRunningEntry()
    {
        var file = new HtspMessage();
        file.Set("start", ToUnixTime(RealStart.AddMinutes(-5)));

        var message = new HtspMessage();
        message.Set("id", 1L);
        message.Set("state", "recording");
        message.Set("start", ToUnixTime(PlannedStart));
        message.Set("stop", ToUnixTime(PlannedStop));
        message.Set("files", (System.Collections.Generic.IEnumerable<HtspMessage>)new[] { file });
        return message;
    }

    private static long ToUnixTime(DateTime value)
        => ((DateTimeOffset)DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static int SchemaRevision()
    {
        var field = typeof(RecordingsChannel).GetField(
            "MediaSourceSchemaRevision",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(field);
        return (int)field!.GetValue(null)!;
    }

    private static DateTime DateFloor()
    {
        var field = typeof(RecordingsChannel).GetField(
            "MediaSourceDateFloorUtc",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(field);
        return (DateTime)field!.GetValue(null)!;
    }

    [Fact]
    public void TheFloorOnlyEverMovesForward()
    {
        // Raising it raises every recording below it, which keeps those dates monotone. Lowering
        // it would drop them all at once and freeze every stored item at whatever it says now.
        Assert.True(DateFloor() >= new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>
    /// What the channel would publish for a recording at a given revision number, worked out here
    /// so that a past revision -- which the code no longer contains -- can still be compared
    /// against.
    /// </summary>
    private static DateTime PublishedAtRevision(MyRecordingInfo recording, int revision)
    {
        var anchor = Max(DateFloor(), recording.DateLastUpdated ?? recording.StartDate);

        var progress = recording.Status switch
        {
            RecordingStatus.InProgress => 1,
            RecordingStatus.Completed => 2,
            _ => 0,
        };

        return anchor.AddDays(revision).AddSeconds(progress);
    }



    private static MediaSourceInfo DescribedRecording()
        => new()
        {
            Id = "recording-1",
            Container = "ts",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = false,
            MediaStreams =
            [
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "mp2" },
            ],
        };
}
