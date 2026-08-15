using System.IO;
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
