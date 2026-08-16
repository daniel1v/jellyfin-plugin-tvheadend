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
        var arguments = TvHeadendHttpLiveStream.BuildReencodeArguments();

        // The point of the exercise: the source carries no IDR frame, so the video has to be
        // rewritten, while re-encoding the audio would be a pointless loss of quality.
        Assert.Equal("libx264", ValueAfter(arguments, "-c:v"));
        Assert.Equal("copy", ValueAfter(arguments, "-c:a"));
        Assert.Contains("keyint=50:min-keyint=25:scenecut=0", ValueAfter(arguments, "-x264-params"), System.StringComparison.Ordinal);

        Assert.Equal("0:v:0", ValueAfter(arguments, "-map"));
        Assert.Contains("0:a?", arguments);
        Assert.Contains("-dn", arguments);
        Assert.Contains("-sn", arguments);
    }

    [Fact]
    public void ReencodeArgumentsWriteToAPipeBecauseTheBufferIsCircular()
    {
        // FFmpeg cannot address a ring, so its output is carried into the buffer by the plugin
        // rather than written to a file. Nothing in the arguments may name a path.
        var arguments = TvHeadendHttpLiveStream.BuildReencodeArguments();

        Assert.Equal("pipe:1", arguments[^1]);
        Assert.DoesNotContain(arguments, argument => argument.Contains(".ts", System.StringComparison.Ordinal));
        Assert.DoesNotContain("-y", arguments);
    }

    [Fact]
    public void ReencodeArgumentsReadFromAPipeSoNoSecondSubscriptionIsOpened()
    {
        // The channel is already being received when the encoder starts; pointing FFmpeg at
        // the tuner again would cost another connection and occupy a second tuner.
        var arguments = TvHeadendHttpLiveStream.BuildReencodeArguments();

        Assert.Equal("pipe:0", ValueAfter(arguments, "-i"));
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("http", System.StringComparison.OrdinalIgnoreCase));

        // The input format has to be stated, because a pipe cannot be probed by extension.
        var list = arguments.ToList();
        Assert.Equal("mpegts", arguments[list.IndexOf("-i") - 1]);
    }

    [Fact]
    public void ReencodeArgumentsBoundTheInputAnalysisBeforeTheInput()
    {
        // Left at its default, FFmpeg spends seconds deciding what a transport stream holds,
        // which lands directly on the channel change of an affected channel.
        var arguments = TvHeadendHttpLiveStream.BuildReencodeArguments();

        Assert.Equal("1000000", ValueAfter(arguments, "-analyzeduration"));
        Assert.Equal("4000000", ValueAfter(arguments, "-probesize"));

        var list = arguments.ToList();
        Assert.True(list.IndexOf("-analyzeduration") < list.IndexOf("-i"));
        Assert.True(list.IndexOf("-probesize") < list.IndexOf("-i"));
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
