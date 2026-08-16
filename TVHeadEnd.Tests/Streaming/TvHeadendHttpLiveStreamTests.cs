using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

public class TvHeadendHttpLiveStreamTests
{
    [Fact]
    public void AcquireReusableReturnsMatchingSharedStreamAndAddsConsumer()
    {
        var matching = new StubLiveStream("source-id", "media-source-id", true);
        var other = new StubLiveStream("other-id", "other-media-source-id", true);

        var result = TvHeadendHttpLiveStream.AcquireReusable(
            [other, matching],
            "source-id",
            "media-source-id");

        Assert.Same(matching, result);
        Assert.Equal(2, matching.ConsumerCount);
        Assert.Equal(1, other.ConsumerCount);
    }

    [Fact]
    public void AcquireReusableIgnoresStreamsThatAreNoLongerShareable()
    {
        var closing = new StubLiveStream("source-id", "media-source-id", false);

        var result = TvHeadendHttpLiveStream.AcquireReusable(
            [closing],
            "source-id",
            "media-source-id");

        Assert.Null(result);
        Assert.Equal(1, closing.ConsumerCount);
    }

    [Fact]
    public void AcquireReusableMatchesStableMediaSourceWhenClientStreamIdChanges()
    {
        var existing = new StubLiveStream("first-client-attempt", "media-source-id", true);

        var result = TvHeadendHttpLiveStream.AcquireReusable(
            [existing],
            "fallback-client-attempt",
            "media-source-id");

        Assert.Same(existing, result);
        Assert.Equal(2, existing.ConsumerCount);
    }

    [Fact]
    public void ReencodeArgumentsRewriteTheVideoAndCopyEveryAudioTrack()
    {
        var arguments = TvHeadendHttpLiveStream.BuildReencodeArguments(
            "http://tvheadend.invalid/stream/channel/1?ticket=redacted",
            new Dictionary<string, string>(),
            @"C:\buffers\tvheadend-1.ts");

        // The point of the exercise: the source carries no IDR frame, so the video has to be
        // rewritten, while re-encoding the audio would be a pointless loss of quality.
        Assert.Equal("libx264", ValueAfter(arguments, "-c:v"));
        Assert.Equal("copy", ValueAfter(arguments, "-c:a"));
        Assert.Contains("keyint=50:min-keyint=25:scenecut=0", ValueAfter(arguments, "-x264-params"), System.StringComparison.Ordinal);

        Assert.Equal("0:v:0", ValueAfter(arguments, "-map"));
        Assert.Contains("0:a?", arguments);
        Assert.Contains("-dn", arguments);
        Assert.Contains("-sn", arguments);

        Assert.Equal("mpegts", ValueAfter(arguments, "-f"));
        Assert.Equal(@"C:\buffers\tvheadend-1.ts", arguments[^1]);
        Assert.Equal("http://tvheadend.invalid/stream/channel/1?ticket=redacted", ValueAfter(arguments, "-i"));
    }

    [Fact]
    public void ReencodeArgumentsPassUpstreamHeadersWhenTicketlessAuthenticationIsUsed()
    {
        var arguments = TvHeadendHttpLiveStream.BuildReencodeArguments(
            "http://tvheadend.invalid/stream/channel/1",
            new Dictionary<string, string> { ["Authorization"] = "Basic redacted" },
            @"C:\buffers\tvheadend-1.ts");

        Assert.Equal("Authorization: Basic redacted\r\n", ValueAfter(arguments, "-headers"));

        // FFmpeg only applies -headers to the input that follows it.
        Assert.True(arguments.ToList().IndexOf("-headers") < arguments.ToList().IndexOf("-i"));
    }

    [Fact]
    public void ReencodeArgumentsOmitTheHeaderOptionWhenThereAreNoHeaders()
    {
        var arguments = TvHeadendHttpLiveStream.BuildReencodeArguments(
            "http://tvheadend.invalid/stream/channel/1?ticket=redacted",
            new Dictionary<string, string>(),
            @"C:\buffers\tvheadend-1.ts");

        Assert.DoesNotContain("-headers", arguments);
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }

    private sealed class StubLiveStream(
        string originalStreamId,
        string mediaSourceId,
        bool enableStreamSharing) : ILiveStream
    {
        public int ConsumerCount { get; set; } = 1;

        public string OriginalStreamId { get; set; } = originalStreamId;

        public string TunerHostId => string.Empty;

        public bool EnableStreamSharing { get; } = enableStreamSharing;

        public MediaSourceInfo MediaSource { get; set; } = new() { Id = mediaSourceId };

        public string UniqueId { get; } = "stub";

        public Task Open(CancellationToken openCancellationToken) => Task.CompletedTask;

        public Task Close() => Task.CompletedTask;

        public Stream GetStream() => Stream.Null;

        public void Dispose()
        {
        }
    }
}
