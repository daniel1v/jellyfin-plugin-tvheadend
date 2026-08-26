using System;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace TVHeadEnd.Tests.Recordings;

/// <summary>
/// What a bounded sample of a recording is allowed to conclude, and what the endpoint serving it
/// is allowed to differ on between HEAD and GET.
/// </summary>
/// <remarks>
/// <para>
/// Both of these were the same mistake in different places. The plugin read the first few
/// megabytes of a recording, found no H.264 IDR frame, and treated that as proof the recording
/// had none -- then withheld direct play for the whole file and served a re-encode. A finite
/// probe cannot establish an absence: a broadcast that opens on a recovery point and carries an
/// IDR a minute later looks exactly the same from the front.
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
        // accident again.
        var members = typeof(RecordingsChannel)
            .GetMembers()
            .Select(member => member.Name)
            .ToList();

        Assert.DoesNotContain("RequiresReencode", members);
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
        var source = RecordingsChannel.BuildRecordingSource("867835561", "http://host:8096/TVHeadend/Recordings/t/stream");

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
        var source = RecordingsChannel.BuildRecordingSource("867835561", "http://host:8096/x");

        Assert.DoesNotContain("://", source.Path, StringComparison.Ordinal);
        Assert.False(System.IO.Path.IsPathRooted(source.Path));
    }

    [Fact]
    public void ARecordingStartsOutNamedTheWayALiveChannelIs()
    {
        // The starting assumption only; DescribeFromSample replaces it with whatever the sample
        // turned out to be. What matters is that the two paths spell the one container alike.
        var source = RecordingsChannel.BuildRecordingSource("867835561", "http://host:8096/x");

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
    public void NothingReEncodesARecordingOnTheWayToTheClient()
    {
        // The encoder that served the re-encode, and the scanner that decided when to, are both
        // gone. A recording is proxied as TVHeadend stored it.
        var assembly = typeof(RecordingsChannel).Assembly;
        var names = assembly.GetTypes().Select(type => type.FullName ?? type.Name).ToList();

        Assert.DoesNotContain(names, name => name.Contains("LegacyH264Encoder", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("H264RandomAccess", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("VideoRandomAccessProbe", StringComparison.Ordinal));
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
    public void StoredRecordingsAreRewrittenOnceWhenTheDescriptionShapeChanges()
    {
        // ChannelManager rewrites a stored item only when DateModified is later than the date it
        // stored, and it compares no part of MediaSources. So an upgrade that changes how a
        // recording is described reaches existing recordings only through this date -- raising it
        // once makes every stored item stale exactly once.
        var revision = SchemaRevision();

        // A recording TVHeadend last touched before the change: the revision carries it.
        var oldRecording = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(revision, Later(oldRecording, revision));

        // One touched since: its own date is later and still wins, so a genuine change is not
        // masked by the revision.
        var recentRecording = revision.AddDays(1);
        Assert.Equal(recentRecording, Later(recentRecording, revision));
    }

    [Fact]
    public void TheSchemaRevisionIsAConstantRatherThanSomethingDerivedFromTheClock()
    {
        // Anything derived from the current time would be later than the stored date on every
        // listing, so every recording would be rewritten for ever. It has to sit after every
        // date already stored -- otherwise the recordings written last, by the very version being
        // replaced, are the ones the migration misses -- and then never move again.
        var first = SchemaRevision();
        System.Threading.Thread.Sleep(20);
        var second = SchemaRevision();

        Assert.Equal(first, second);
        Assert.NotEqual(default, first);
        Assert.Equal(DateTimeKind.Utc, first.Kind);

        // Written by hand rather than read off a clock. Comparing against the current time would
        // itself be a clock-dependent test, and would fail for a minute whenever the two happened
        // to coincide.
        Assert.Equal(0, first.Ticks % TimeSpan.TicksPerMinute);
    }

    private static DateTime SchemaRevision()
    {
        var field = typeof(RecordingsChannel).GetField(
            "MediaSourceSchemaRevisionUtc",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(field);
        return (DateTime)field!.GetValue(null)!;
    }

    private static DateTime Later(DateTime recordingChanged, DateTime revision)
        => recordingChanged > revision ? recordingChanged : revision;

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
