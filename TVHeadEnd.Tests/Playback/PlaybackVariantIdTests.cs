using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.Streaming;
using TVHeadEnd.Playback;
using Xunit;

namespace TVHeadEnd.Tests.Playback;

public class PlaybackVariantIdTests
{
    [Fact]
    public void EveryVariantOfAChannelHasItsOwnIdentifier()
    {
        // Reuse of an open stream is keyed by this. If two variants shared an identifier, a
        // client that asked for the compatibility rendering could be handed the broadcast.
        var native = PlaybackVariantId.Create("42", PlaybackVariant.Native);
        var mpeg2 = PlaybackVariantId.Create("42", PlaybackVariant.Mpeg2H264Compatibility);
        var idr = PlaybackVariantId.Create("42", PlaybackVariant.H264IdrNormalization);

        Assert.NotEqual(native, mpeg2);
        Assert.NotEqual(native, idr);
        Assert.NotEqual(mpeg2, idr);
    }

    [Fact]
    public void IdentifiersAreStableAcrossCalls()
    {
        // Negotiation and the open that follows are separate requests, and the identifier the
        // client was given has to still resolve.
        Assert.Equal(
            PlaybackVariantId.Create("42", PlaybackVariant.Native),
            PlaybackVariantId.Create("42", PlaybackVariant.Native));
    }

    [Fact]
    public void DifferentChannelsDoNotShareIdentifiers()
    {
        Assert.NotEqual(
            PlaybackVariantId.Create("42", PlaybackVariant.Native),
            PlaybackVariantId.Create("43", PlaybackVariant.Native));
    }

    [Theory]
    [InlineData(PlaybackVariant.Native)]
    [InlineData(PlaybackVariant.Mpeg2H264Compatibility)]
    [InlineData(PlaybackVariant.H264IdrNormalization)]
    public void AnIdentifierResolvesBackToItsVariant(PlaybackVariant variant)
    {
        var id = PlaybackVariantId.Create("42", variant);

        Assert.Equal(variant, PlaybackVariantId.Resolve("42", id));
    }

    [Fact]
    public void AnUnknownIdentifierResolvesToNothing()
    {
        Assert.Null(PlaybackVariantId.Resolve("42", "not-one-of-ours"));
        Assert.Null(PlaybackVariantId.Resolve("42", null));
    }

    [Fact]
    public void ABroadcastIsNeverSharedWithARequestForARenderingOfIt()
    {
        // Reuse keyed by channel alone would hand an affected client the very broadcast the
        // normalized variant exists to keep away from it, and it would look like a cache hit.
        using var native = Stream("159026356", PlaybackVariant.Native);

        Assert.False(LiveTvService.CanBeReusedFor(native, "159026356", PlaybackVariant.H264IdrNormalization));
        Assert.False(LiveTvService.CanBeReusedFor(native, "159026356", PlaybackVariant.Mpeg2H264Compatibility));
    }

    [Fact]
    public void AStreamWithNothingBufferedYetIsNotHandedOut()
    {
        // Sharing a stream that has not produced anything would give the second caller a reader
        // over a buffer that does not exist.
        using var native = Stream("159026356", PlaybackVariant.Native);

        Assert.False(native.HasBuffer);
        Assert.False(LiveTvService.CanBeReusedFor(native, "159026356", PlaybackVariant.Native));
    }

    [Fact]
    public void ARenderingIsNeverSharedAcrossChannels()
    {
        using var normalized = Stream("159026356", PlaybackVariant.H264IdrNormalization);

        Assert.False(LiveTvService.CanBeReusedFor(normalized, "1460599120", PlaybackVariant.H264IdrNormalization));
    }

    [Fact]
    public void AStreamThatHasStoppedSharingIsNotHandedOut()
    {
        using var native = Stream("159026356", PlaybackVariant.Native);
        native.Close().GetAwaiter().GetResult();

        Assert.False(LiveTvService.CanBeReusedFor(native, "159026356", PlaybackVariant.Native));
    }


    [Fact]
    public void TheBroadcastKeepsItsRingBufferAndItsSharing()
    {
        // The native path is the one that is shared, long running and joined mid-flight, so the
        // ring buffer and the entry point hunting stay exactly where they were.
        using var native = Stream("159026356", PlaybackVariant.Native);

        Assert.True(native.EnableStreamSharing);
        Assert.IsType<TvheadendLiveStream>(native);
    }
    private static TvheadendLiveStream Stream(string channelId, PlaybackVariant variant)
        => new(
            channelId,
            variant.ToString(),
            "http://tvheadend.invalid/stream",
            new Dictionary<string, string>(),
            new MediaSourceInfo(),
            Path.Combine(Path.GetTempPath(), "tvheadend-test-" + Guid.NewGuid().ToString("N")),
            1,
            describedAlready: true,
            new NeverUsedHttpClientFactory(),
            NullLogger.Instance);

    private sealed class NeverUsedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
