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
/// It exists to say <c>allowVideoStreamCopy=false</c> for one stream, and its whole risk is saying
/// it for anything else. Most of what is below is therefore about the requests it must leave
/// exactly as they arrived.
/// </remarks>
public class ForcedVideoReencodeMiddlewareTests
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
    public async Task ARequestNamingOneOfOurStreamsThatPlaysAsDeliveredIsUntouched()
    {
        var context = Request($"/videos/1/live.m3u8?LiveStreamId={PlainId}&allowVideoStreamCopy=true");

        await Invoke(context);

        Assert.Equal($"?LiveStreamId={PlainId}&allowVideoStreamCopy=true", context.Request.QueryString.Value);
    }

    [Fact]
    public async Task AStreamThatHasToBeReEncodedGainsTheRefusal()
    {
        var context = Request($"/videos/1/live.m3u8?LiveStreamId={ForcedId}&ApiKey=secret&VideoCodec=h264");

        await Invoke(context);

        Assert.Equal("false", context.Request.Query["allowVideoStreamCopy"]);

        // Everything else survives, the key included, because the request still has to work.
        Assert.Equal(ForcedId, context.Request.Query["LiveStreamId"]);
        Assert.Equal("secret", context.Request.Query["ApiKey"]);
        Assert.Equal("h264", context.Request.Query["VideoCodec"]);
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
        var middleware = new ForcedVideoReencodeMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            NullLogger<ForcedVideoReencodeMiddleware>.Instance);

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
