using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Core.Media;
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
    private const string ItemId = "8a1f0e6c4b2d47f0a9c3e5d7b1f2a4c6";

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

    [Fact]
    public async Task AStaticRequestForOneOfOurLiveSourcesIsGivenItsStream()
    {
        // The whole point of publishing the buffer as a file. Jellyfin serves a live stream from
        // this endpoint only when the request names one -- without it there is no provider to ask
        // and it serves the ring file, which ends at whatever had been written.
        var stream = LiveStream(requiresVideoReencode: false);
        stream.MediaSource.LiveStreamId = ForcedId;

        var open = new OpenLiveStreams();
        open.Register("source-1", "pixel-10", stream);

        var context = Request("/Videos/1/stream?static=true&MediaSourceId=source-1&DeviceId=pixel-10&ApiKey=secret");

        await Invoke(context, open, new FakeMediaSourceManager(
            new Dictionary<string, ILiveStream>(StringComparer.OrdinalIgnoreCase) { [ForcedId] = stream }));

        Assert.Equal(ForcedId, context.Request.Query["LiveStreamId"]);
        Assert.Equal("secret", context.Request.Query["ApiKey"]);
        Assert.Equal("source-1", context.Request.Query["MediaSourceId"]);
    }

    [Fact]
    public async Task AStaticRequestThatAlreadyNamesItsStreamIsLeftAlone()
    {
        var stream = LiveStream(requiresVideoReencode: false);
        stream.MediaSource.LiveStreamId = ForcedId;

        var open = new OpenLiveStreams();
        open.Register("source-1", "pixel-10", stream);

        var context = Request($"/Videos/1/stream?static=true&MediaSourceId=source-1&LiveStreamId={PlainId}");

        await Invoke(context, open, new FakeMediaSourceManager(
            new Dictionary<string, ILiveStream>(StringComparer.OrdinalIgnoreCase) { [ForcedId] = stream }));

        Assert.Equal(PlainId, context.Request.Query["LiveStreamId"]);
        Assert.Single(context.Request.Query["LiveStreamId"]);
    }

    [Fact]
    public async Task AStaticRequestForASourceNothingIsOpenForIsLeftAlone()
    {
        var context = Request("/Videos/1/stream?static=true&MediaSourceId=source-1");

        await Invoke(context, new OpenLiveStreams(), Streams());

        Assert.Equal("?static=true&MediaSourceId=source-1", context.Request.QueryString.Value);
    }

    [Fact]
    public async Task AnIdentifierJellyfinNoLongerAgreesWithIsNotSupplied()
    {
        // Jellyfin hands identifiers out and Jellyfin closes streams, so its own register is the
        // only thing that can say an identifier still means this stream. Guessing wrong here
        // would hand a viewer somebody else's channel.
        var ours = LiveStream(requiresVideoReencode: false);
        ours.MediaSource.LiveStreamId = ForcedId;

        var somebodyElses = LiveStream(requiresVideoReencode: false);

        var open = new OpenLiveStreams();
        open.Register("source-1", "pixel-10", ours);

        var context = Request("/Videos/1/stream?static=true&MediaSourceId=source-1");

        await Invoke(context, open, new FakeMediaSourceManager(
            new Dictionary<string, ILiveStream>(StringComparer.OrdinalIgnoreCase) { [ForcedId] = somebodyElses }));

        Assert.Equal("?static=true&MediaSourceId=source-1", context.Request.QueryString.Value);
    }

    [Fact]
    public async Task TwoDevicesWatchingOneChannelEachGetTheirOwnStream()
    {
        // One channel can have several streams open at once -- two viewers whose profiles need
        // different renderings get one each -- and every one of them carries the same media
        // source identifier. The device is what tells them apart.
        var forPixel = LiveStream(requiresVideoReencode: false);
        forPixel.MediaSource.LiveStreamId = ForcedId;

        var forBrowser = LiveStream(requiresVideoReencode: false);
        forBrowser.MediaSource.LiveStreamId = PlainId;

        var open = new OpenLiveStreams();
        open.Register("source-1", "pixel-10", forPixel);
        open.Register("source-1", "firefox", forBrowser);

        var registered = new Dictionary<string, ILiveStream>(StringComparer.OrdinalIgnoreCase)
        {
            [ForcedId] = forPixel,
            [PlainId] = forBrowser,
        };

        var pixel = Request("/Videos/1/stream?static=true&MediaSourceId=source-1&DeviceId=pixel-10");
        await Invoke(pixel, open, new FakeMediaSourceManager(registered));
        Assert.Equal(ForcedId, pixel.Request.Query["LiveStreamId"]);

        var browser = Request("/Videos/1/stream?static=true&MediaSourceId=source-1&DeviceId=firefox");
        await Invoke(browser, open, new FakeMediaSourceManager(registered));
        Assert.Equal(PlainId, browser.Request.Query["LiveStreamId"]);
    }

    [Fact]
    public async Task ARequestThatDoesNotSayWhichDeviceItIsGetsNothing()
    {
        // Two streams answer to this media source, so the media source does not identify one.
        // Handing over either would be a coin toss for whose rendering the viewer receives.
        var open = new OpenLiveStreams();
        var registered = TwoDevicesOnOneChannel(open);

        var context = Request("/Videos/1/stream?static=true&MediaSourceId=source-1");

        await Invoke(context, open, new FakeMediaSourceManager(registered));

        Assert.Equal("?static=true&MediaSourceId=source-1", context.Request.QueryString.Value);
    }

    [Fact]
    public async Task ARequestFromADeviceNothingIsOpenForGetsNothing()
    {
        // The device is known to Jellyfin but not to this registry, and two streams are open, so
        // there is still nothing that singles one out.
        var open = new OpenLiveStreams();
        var registered = TwoDevicesOnOneChannel(open);

        var context = Request("/Videos/1/stream?static=true&MediaSourceId=source-1&DeviceId=roku");

        await Invoke(context, open, new FakeMediaSourceManager(registered));

        Assert.Equal("?static=true&MediaSourceId=source-1&DeviceId=roku", context.Request.QueryString.Value);
    }

    [Fact]
    public async Task ASingleOpenStreamIsStillFoundWithoutADevice()
    {
        // A stream opened outside a request has no device recorded, and an older client may send
        // none. Where the media source leaves no room for doubt that is still an answer.
        var stream = LiveStream(requiresVideoReencode: false);
        stream.MediaSource.LiveStreamId = ForcedId;

        var open = new OpenLiveStreams();
        open.Register("source-1", null, stream);

        var context = Request("/Videos/1/stream?static=true&MediaSourceId=source-1&DeviceId=pixel-10");

        await Invoke(context, open, new FakeMediaSourceManager(
            new Dictionary<string, ILiveStream>(StringComparer.OrdinalIgnoreCase) { [ForcedId] = stream }));

        Assert.Equal(ForcedId, context.Request.Query["LiveStreamId"]);
    }

    private static Dictionary<string, ILiveStream> TwoDevicesOnOneChannel(OpenLiveStreams open)
    {
        var first = LiveStream(requiresVideoReencode: false);
        first.MediaSource.LiveStreamId = ForcedId;

        var second = LiveStream(requiresVideoReencode: false);
        second.MediaSource.LiveStreamId = PlainId;

        open.Register("source-1", "pixel-10", first);
        open.Register("source-1", "firefox", second);

        return new Dictionary<string, ILiveStream>(StringComparer.OrdinalIgnoreCase)
        {
            [ForcedId] = first,
            [PlainId] = second,
        };
    }

    [Fact]
    public async Task APlaylistRequestIsNotGivenAStreamIdentifier()
    {
        // Only the static route is short of one. A playlist request that lacks it lacks it for
        // some other reason, and this is not the place to decide what.
        var stream = LiveStream(requiresVideoReencode: false);
        stream.MediaSource.LiveStreamId = ForcedId;

        var open = new OpenLiveStreams();
        open.Register("source-1", "pixel-10", stream);

        var context = Request("/videos/1/live.m3u8?MediaSourceId=source-1");

        await Invoke(context, open, new FakeMediaSourceManager(
            new Dictionary<string, ILiveStream>(StringComparer.OrdinalIgnoreCase) { [ForcedId] = stream }));

        Assert.Equal("?MediaSourceId=source-1", context.Request.QueryString.Value);
    }

    [Fact]
    public async Task ThePlaybackProfileOfOneOfOurChannelsIsWidened()
    {
        // Two names for one container, and a source can only carry one of them, so the profile is
        // the side that has to say both.
        var body = await PlaybackInfo(Channel(TvheadendItems.ServiceName), "{\"DeviceProfile\":{\"DirectPlayProfiles\":[{\"Container\":\"ts\"}]}}");

        Assert.Contains("mpegts", body, StringComparison.Ordinal);
        Assert.Contains("ts", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    public async Task AnItemTheLibraryDoesNotKnowIsLeftCompletelyAlone(BaseItem? item)
    {
        // The library answering nothing is not an invitation to guess. Everything else in it --
        // a film, an episode, another tuner's channel -- reaches Jellyfin with the profile its
        // client sent, byte for byte.
        const string Sent = "{\"DeviceProfile\":{\"DirectPlayProfiles\":[{\"Container\":\"ts\"}]}}";

        Assert.Equal(Sent, await PlaybackInfo(item, Sent));
    }

    [Fact]
    public async Task AnotherTunersChannelIsLeftCompletelyAlone()
    {
        // It is a live channel, which is the closest thing in the library to one of ours, and it
        // is still not ours. The service that produced it is what decides, and nothing else.
        const string Sent = "{\"DeviceProfile\":{\"DirectPlayProfiles\":[{\"Container\":\"ts\"}]}}";

        Assert.Equal(Sent, await PlaybackInfo(Channel("SomeOtherTuner"), Sent));
    }

    [Fact]
    public async Task AFilmIsLeftCompletelyAlone()
    {
        const string Sent = "{\"DeviceProfile\":{\"ContainerProfiles\":[{\"Container\":\"mpegts\"}]}}";

        Assert.Equal(Sent, await PlaybackInfo(new Movie(), Sent));
    }

    [Fact]
    public async Task ARecordingOfOursIsWidenedTheWayAChannelIs()
    {
        // The gap this closes: a recording is not a LiveTvChannel, so recognising only those left
        // every recording matching against its published container alone. A client that lists
        // MPEG-TS as "mpegts" and a source that says "ts" never met.
        var body = await PlaybackInfo(
            Recording(),
            "{\"DeviceProfile\":{\"DirectPlayProfiles\":[{\"Container\":\"mpegts\",\"Type\":\"Video\"}]}}");

        Assert.Contains("mpegts,ts", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherChannelPluginsItemIsLeftCompletelyAlone()
    {
        // It is a channel item, which is the closest thing in the library to one of our
        // recordings, and it belongs to a different channel. The identifier Jellyfin's own channel
        // manager wrote is what decides, and nothing else.
        const string Sent = "{\"DeviceProfile\":{\"DirectPlayProfiles\":[{\"Container\":\"mpegts\"}]}}";

        var foreign = new Movie
        {
            ChannelId = FakeLibrary.NewItemId("Channel Some Other Plugin", typeof(Channel)),
        };

        Assert.Equal(Sent, await PlaybackInfo(foreign, Sent));
    }

    [Fact]
    public async Task OpeningALiveStreamWidensTheProfileToo()
    {
        // MediaInfoHelper.OpenMediaSource weighs the profile a second time, against the source the
        // open produced. Widening only PlaybackInfo would tell a client it may direct play and
        // then, on opening, send it to a transcode.
        var body = await Open(
            "/LiveStreams/Open",
            "{\"ItemId\":\"" + ItemId + "\",\"DeviceProfile\":{\"DirectPlayProfiles\":[{\"Container\":\"mpegts\"}]}}");

        Assert.Contains("mpegts,ts", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningALiveStreamTakesTheItemFromTheQueryFirst()
    {
        // The order Jellyfin's own controller resolves it in: itemId ?? dto.ItemId ?? Empty.
        var body = await Open(
            $"/LiveStreams/Open?itemId={ItemId}",
            "{\"DeviceProfile\":{\"DirectPlayProfiles\":[{\"Container\":\"ts\"}]}}");

        Assert.Contains("ts,mpegts", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningALiveStreamForSomebodyElsesItemIsLeftAlone()
    {
        const string Sent = "{\"ItemId\":\"" + ItemId + "\",\"DeviceProfile\":{\"DirectPlayProfiles\":[{\"Container\":\"mpegts\"}]}}";

        Assert.Equal(Sent, await Body("/LiveStreams/Open", Sent, new Movie()));
    }

    /// <summary>
    /// Runs an open request for one of our live channels.
    /// </summary>
    private static Task<string> Open(string pathAndQuery, string sent)
        => Body(pathAndQuery, sent, Channel(TvheadendItems.ServiceName));

    /// <summary>
    /// Runs a PlaybackInfo request for whatever the library says the item is, and returns the
    /// body as the rest of the pipeline sees it.
    /// </summary>
    private static Task<string> PlaybackInfo(BaseItem? item, string sent)
        => Body($"/Items/{ItemId}/PlaybackInfo", sent, item);

    private static async Task<string> Body(string pathAndQuery, string sent, BaseItem? item)
    {
        var context = Request(pathAndQuery);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(sent));

        await Invoke(context, new OpenLiveStreams(), Streams(), FakeLibrary.Returning(item));

        context.Request.Body.Position = 0;
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static LiveTvChannel Channel(string serviceName)
        => new() { ServiceName = serviceName };

    /// <summary>
    /// A recording as Jellyfin's channel manager stores it: an ordinary video item carrying the
    /// identifier of the channel that produced it.
    /// </summary>
    private static Movie Recording()
        => new()
        {
            ChannelId = FakeLibrary.NewItemId(
                "Channel " + TvheadendItems.RecordingsChannelName,
                typeof(Channel)),
        };


    private static Task Invoke(HttpContext context)
        => Invoke(context, new OpenLiveStreams(), Streams());

    private static Task Invoke(HttpContext context, OpenLiveStreams open, IMediaSourceManager manager)
        => Invoke(context, open, manager, FakeLibrary.Returning(null));

    private static async Task Invoke(
        HttpContext context,
        OpenLiveStreams open,
        IMediaSourceManager manager,
        ILibraryManager library)
    {

        var called = false;
        var middleware = new LivePlaybackRequestMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            NullLogger<LivePlaybackRequestMiddleware>.Instance,
            open);

        await middleware.Invoke(context, manager, library);

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
    /// Answers the two library questions this plugin asks, and nothing else.
    /// </summary>
    /// <remarks>
    /// ILibraryManager is a hundred members wide and only two of them are reachable from here, so
    /// it is generated rather than written out: every other member answers with its default and
    /// would fail loudly if the middleware ever started calling it.
    /// </remarks>
    private class FakeLibrary : DispatchProxy
    {
        private BaseItem? _item;

        public static ILibraryManager Returning(BaseItem? item)
        {
            var proxy = Create<ILibraryManager, FakeLibrary>();
            ((FakeLibrary)(object)proxy!)._item = item;
            return proxy;
        }

        /// <summary>
        /// Derives an identifier the way LibraryManager does: the type's full name in front of
        /// the key, then MD5. Copied rather than approximated, because the whole point of the
        /// channel check is that the plugin arrives at the very identifier Jellyfin stored.
        /// </summary>
        public static Guid NewItemId(string key, Type type)
            => ((type.FullName ?? string.Empty) + key.ToLowerInvariant()).GetMD5();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemById))
            {
                return _item;
            }

            if (targetMethod?.Name == nameof(ILibraryManager.GetNewItemId))
            {
                return NewItemId((string)args![0]!, (Type)args[1]!);
            }

            return null;
        }
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
