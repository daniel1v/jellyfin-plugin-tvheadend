using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Playback;

/// <summary>
/// The one step this plugin adds to Jellyfin's request pipeline.
/// </summary>
/// <remarks>
/// It adjusts the streaming requests Jellyfin makes for a live stream this plugin opened, and its
/// whole risk is adjusting anything else. Most of what is below is therefore about the requests it
/// must leave exactly as they arrived.
/// </remarks>
public class LivePlaybackRequestMiddlewareTests
{
    private const string ForcedId = "live-forced";
    private const string PlainId = "live-plain";
    private const string ForeignId = "live-foreign";

    [Fact]
    public async Task ARequestNamingNoLiveStreamIsUntouched()
    {
        var context = Request("/Items/abc/PlaybackInfo?userId=42");

        await Invoke(context);

        Assert.Equal("?userId=42", context.Request.QueryString.Value);
    }

    [Fact]
    public async Task ARequestNamingAStreamJellyfinDoesNotHaveOpenIsUntouched()
    {
        var context = Request("/videos/1/live.m3u8?LiveStreamId=gone&ApiKey=k");

        await Invoke(context);

        Assert.Equal("?LiveStreamId=gone&ApiKey=k", context.Request.QueryString.Value);
    }

    [Fact]
    public async Task ARequestNamingAnotherPluginsStreamIsUntouched()
    {
        var context = Request($"/videos/1/live.m3u8?LiveStreamId={ForeignId}");

        await Invoke(context);

        Assert.Equal($"?LiveStreamId={ForeignId}", context.Request.QueryString.Value);
    }

    [Fact]
    public async Task ARequestForOneOfOurStreamsThatIsNotAPlaylistIsUntouched()
    {
        // The segment counts belong to the playlist Jellyfin holds back. A request for the
        // transport stream itself is not held back by anything and gains nothing here.
        var context = Request($"/videos/1/stream.ts?LiveStreamId={PlainId}&static=true");

        await Invoke(context);

        Assert.Equal($"?LiveStreamId={PlainId}&static=true", context.Request.QueryString.Value);
    }

    [Fact]
    public async Task APlaylistForOneOfOurStreamsThatPlaysAsDeliveredKeepsItsCopy()
    {
        var context = Request($"/videos/1/live.m3u8?LiveStreamId={PlainId}&allowVideoStreamCopy=true");

        await Invoke(context);

        // The stream plays as delivered, so nothing is said about copying it either way.
        Assert.Equal("true", context.Request.Query["allowVideoStreamCopy"]);
    }

    [Theory]
    [InlineData("/videos/1/master.m3u8")]
    [InlineData("/videos/1/main.m3u8")]
    [InlineData("/videos/1/live.m3u8")]
    public async Task APlaylistThatAsksForNothingIsGivenTheShortestWait(string path)
    {
        // Jellyfin holds a playlist back until a minimum number of segments exist, and for a
        // segmented live stream being copied its defaults are three segments of three seconds --
        // nine seconds of broadcast before a viewer sees anything.
        var context = Request($"{path}?LiveStreamId={PlainId}&ApiKey=secret");

        await Invoke(context);

        Assert.Equal("1", context.Request.Query["MinSegments"]);
        Assert.Equal("1", context.Request.Query["SegmentLength"]);
        Assert.Equal("secret", context.Request.Query["ApiKey"]);
        Assert.Equal(PlainId, context.Request.Query["LiveStreamId"]);
    }

    [Fact]
    public async Task APlaylistThatAsksForParticularSegmentsKeepsThem()
    {
        // A client that states these is stating them for a reason -- Jellyfin gives Apple devices
        // six-second segments by its own rules -- and trading its playback for another client's
        // startup is not this plugin's call to make.
        var context = Request($"/videos/1/master.m3u8?LiveStreamId={PlainId}&MinSegments=2&SegmentLength=6");

        await Invoke(context);

        Assert.Equal("2", context.Request.Query["MinSegments"]);
        Assert.Equal("6", context.Request.Query["SegmentLength"]);
        Assert.Single(context.Request.Query["MinSegments"]);
        Assert.Single(context.Request.Query["SegmentLength"]);
    }

    [Fact]
    public async Task OneStatedValueDoesNotSpeakForTheOther()
    {
        var context = Request($"/videos/1/master.m3u8?LiveStreamId={PlainId}&SegmentLength=6");

        await Invoke(context);

        Assert.Equal("6", context.Request.Query["SegmentLength"]);
        Assert.Equal("1", context.Request.Query["MinSegments"]);
    }

    [Fact]
    public async Task AStreamThatHasToBeReEncodedGainsTheRefusal()
    {
        var context = Request($"/videos/1/stream.ts?LiveStreamId={ForcedId}&ApiKey=secret&VideoCodec=h264");

        await Invoke(context);

        Assert.Equal("false", context.Request.Query["allowVideoStreamCopy"]);

        // Everything else survives, the key included, because the request still has to work.
        Assert.Equal(ForcedId, context.Request.Query["LiveStreamId"]);
        Assert.Equal("secret", context.Request.Query["ApiKey"]);
        Assert.Equal("h264", context.Request.Query["VideoCodec"]);
    }

    [Fact]
    public async Task APlaylistForAStreamThatHasToBeReEncodedGetsBothRules()
    {
        // The two questions are unrelated and a request can need answers to both.
        var context = Request($"/videos/1/live.m3u8?LiveStreamId={ForcedId}&ApiKey=secret");

        await Invoke(context);

        Assert.Equal("false", context.Request.Query["allowVideoStreamCopy"]);
        Assert.Equal("1", context.Request.Query["MinSegments"]);
        Assert.Equal("1", context.Request.Query["SegmentLength"]);
        Assert.Equal("secret", context.Request.Query["ApiKey"]);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task AnExistingValueIsReplacedRatherThanContradicted(string existing)
    {
        // Appending a second value would leave the request saying both things at once, and which
        // of them Jellyfin's model binder believes is not a thing to depend on.
        var context = Request($"/videos/1/live.m3u8?LiveStreamId={ForcedId}&allowVideoStreamCopy={existing}");

        await Invoke(context);

        Assert.Equal("false", context.Request.Query["allowVideoStreamCopy"]);
        Assert.Single(context.Request.Query["allowVideoStreamCopy"]);
        Assert.DoesNotContain("allowVideoStreamCopy=true", context.Request.QueryString.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheParametersAreRecognisedWhateverCaseJellyfinSpelledThemIn()
    {
        var context = Request($"/videos/1/live.m3u8?livestreamid={ForcedId}&AllowVideoStreamCopy=true");

        await Invoke(context);

        Assert.Equal("false", context.Request.Query["allowVideoStreamCopy"]);
        Assert.Single(context.Request.Query["allowVideoStreamCopy"]);
    }

    private static async Task Invoke(HttpContext context)
    {
        var called = false;
        var middleware = new LivePlaybackRequestMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            NullLogger<LivePlaybackRequestMiddleware>.Instance);

        await middleware.Invoke(context, Streams());

        Assert.True(called, "The request must always continue down the pipeline.");
    }

    private static FakeMediaSourceManager Streams()
        => new(new Dictionary<string, ILiveStream>(StringComparer.OrdinalIgnoreCase)
        {
            [ForcedId] = LiveStream(requiresVideoReencode: true),
            [PlainId] = LiveStream(requiresVideoReencode: false),
            [ForeignId] = new ForeignLiveStream(),
        });

    private static TvheadendLiveStream LiveStream(bool requiresVideoReencode)
    {
        var stream = new TvheadendLiveStream(
            "42",
            "Das Erste HD",
            "http://tvheadend/stream",
            new Dictionary<string, string>(),
            new MediaSourceInfo(),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            LiveStreamBuffer.MinimumSizeMegabytes,
            new UnusedClientFactory(),
            NullLogger.Instance);

        stream.RequiresVideoReencode = requiresVideoReencode;
        return stream;
    }

    private static DefaultHttpContext Request(string pathAndQuery)
    {
        var split = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var context = new DefaultHttpContext();
        context.Request.Path = split < 0 ? pathAndQuery : pathAndQuery[..split];
        context.Request.QueryString = split < 0 ? QueryString.Empty : new QueryString(pathAndQuery[split..]);
        return context;
    }

    /// <summary>
    /// Never called: these streams are only ever looked up, never opened.
    /// </summary>
    private sealed class UnusedClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => throw new NotSupportedException();
    }

    /// <summary>
    /// A live stream belonging to some other plugin, which this must not touch however it is asked.
    /// </summary>
    private sealed class ForeignLiveStream : ILiveStream
    {
        public int ConsumerCount { get; set; }

        public string OriginalStreamId { get; set; } = string.Empty;

        public string TunerHostId => string.Empty;

        public bool EnableStreamSharing => true;

        public MediaSourceInfo MediaSource { get; set; } = new();

        public string UniqueId => "foreign";

        public Task Open(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task Close() => Task.CompletedTask;

        public Stream GetStream() => Stream.Null;

        public void Dispose()
        {
        }
    }
}
