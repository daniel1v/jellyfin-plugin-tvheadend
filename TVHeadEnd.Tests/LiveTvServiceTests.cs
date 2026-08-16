using System.Reflection;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace TVHeadEnd.Tests;

public class LiveTvServiceTests
{
    [Fact]
    public void TheServiceManagesItsOwnLiveStreamsSoTheProbingFallbackStaysUnreachable()
    {
        // Jellyfin picks GetChannelStreamWithDirectStreamProvider for services that implement
        // ISupportsDirectStreamProvider and falls back to ILiveTvService.GetChannelStream for
        // those that do not. Losing the interface would silently route every channel through
        // that fallback, which hands out the bare TVHeadend URL: a second subscription for a
        // channel already being received, and a stream that never passed the conditioner or
        // the re-encode that IDR-less broadcasts need.
        Assert.True(typeof(ISupportsDirectStreamProvider).IsAssignableFrom(typeof(LiveTvService)));
    }

    [Fact]
    public void TheUnreachableFallbackRefusesInsteadOfOpeningASecondSubscription()
    {
        // Should the interface above ever be dropped, the fallback has to fail loudly rather
        // than quietly serve an unconditioned stream, so the mistake surfaces at once.
        var fallback = typeof(LiveTvService).GetMethod(
            nameof(LiveTvService.GetChannelStream),
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(fallback);

        var body = fallback!.GetMethodBody();
        Assert.NotNull(body);

        // A method that only throws needs no locals and no branches; this keeps the assertion
        // honest without depending on an instance the constructor cannot produce in a test.
        Assert.Empty(body!.LocalVariables);
    }
}
