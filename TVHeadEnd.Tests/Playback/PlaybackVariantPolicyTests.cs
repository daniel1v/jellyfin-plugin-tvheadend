using MediaBrowser.Model.Entities;
using TVHeadEnd.Media;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Playback;

public class PlaybackVariantPolicyTests
{
    // The name the measured client actually sends. The registry lists this one and nothing
    // else, so using any other spelling here would test a quirk that never fires.
    private static readonly PlaybackClientContext AffectedClient = new("Jellyfin for Android", "2.7.1", "Pixel", "device-1");
    private static readonly PlaybackClientContext UnaffectedClient = new("Jellyfin Web", "10.9.0", "Firefox", "device-2");

    [Fact]
    public void AnUnknownChannelIsOfferedNativeOnly()
    {
        // Nothing may be concluded before the channel has been received, and concluding
        // something would mean opening a tuner during playback negotiation.
        var offers = PlaybackVariantPolicy.SelectVariants(
            null,
            new PlaybackVariantAvailability(true, true),
            AffectedClient);

        Assert.Equal([PlaybackVariant.Native], Variants(offers));
    }

    [Fact]
    public void NativelySafeH264IsOfferedNativeOnly()
    {
        var offers = PlaybackVariantPolicy.SelectVariants(
            Descriptor("h264", 0x1B, H264RandomAccessKind.Idr),
            new PlaybackVariantAvailability(true, true),
            AffectedClient);

        Assert.Equal([PlaybackVariant.Native], Variants(offers));
    }

    [Fact]
    public void Mpeg2IsOfferedNativeFirstThenCompatibility()
    {
        // Both are direct play candidates. The order is what lets a client that can decode
        // MPEG-2 keep the broadcast, and what makes a client that can decode neither transcode
        // from the original rather than from an already re-coded stream.
        var offers = PlaybackVariantPolicy.SelectVariants(
            Descriptor("mpeg2video", 0x02, H264RandomAccessKind.NotApplicable),
            new PlaybackVariantAvailability(true, false),
            UnaffectedClient);

        Assert.Equal([PlaybackVariant.Native, PlaybackVariant.Mpeg2H264Compatibility], Variants(offers));
        Assert.All(offers, offer => Assert.True(offer.SupportsDirectPlay));
    }

    [Fact]
    public void Mpeg2WithoutACompatibilityProfileIsOfferedNativeOnly()
    {
        var offers = PlaybackVariantPolicy.SelectVariants(
            Descriptor("mpeg2video", 0x02, H264RandomAccessKind.NotApplicable),
            PlaybackVariantAvailability.NativeOnly,
            UnaffectedClient);

        Assert.Equal([PlaybackVariant.Native], Variants(offers));
    }

    [Fact]
    public void RecoveryOpenGopReachesAnUnaffectedClientUnchanged()
    {
        // The broadcast is conformant and starts on any decoder that discards the leading
        // pictures of an open GOP. Degrading it for everyone would be the client-specific
        // behaviour this architecture exists to avoid.
        var offers = PlaybackVariantPolicy.SelectVariants(
            Descriptor("h264", 0x1B, H264RandomAccessKind.RecoveryOpenGop),
            new PlaybackVariantAvailability(false, true),
            UnaffectedClient);

        Assert.Equal([PlaybackVariant.Native], Variants(offers));
    }

    [Fact]
    public void RecoveryOpenGopNeverReachesAnAffectedClientAsNative()
    {
        var offers = PlaybackVariantPolicy.SelectVariants(
            Descriptor("h264", 0x1B, H264RandomAccessKind.RecoveryOpenGop),
            new PlaybackVariantAvailability(false, true),
            AffectedClient);

        // Only the normalized form is offered, so the stream that will not start cannot win
        // direct play by being listed first.
        Assert.Equal([PlaybackVariant.H264IdrNormalization], Variants(offers));
        Assert.DoesNotContain(PlaybackVariant.Native, Variants(offers));
    }

    [Fact]
    public void WithoutARequestContextNativeIsUsed()
    {
        var offers = PlaybackVariantPolicy.SelectVariants(
            Descriptor("h264", 0x1B, H264RandomAccessKind.RecoveryOpenGop),
            new PlaybackVariantAvailability(false, true),
            PlaybackClientContext.None);

        Assert.Equal([PlaybackVariant.Native], Variants(offers));
    }

    [Fact]
    public void AnOpenThatFindsRecoveryOpenGopEscalatesOnlyForAnAffectedClient()
    {
        var availability = new PlaybackVariantAvailability(false, true);

        Assert.Equal(
            PlaybackVariant.H264IdrNormalization,
            PlaybackVariantPolicy.ReconcileAfterOpen(
                PlaybackVariant.Native,
                H264RandomAccessKind.RecoveryOpenGop,
                availability,
                AffectedClient));

        Assert.Equal(
            PlaybackVariant.Native,
            PlaybackVariantPolicy.ReconcileAfterOpen(
                PlaybackVariant.Native,
                H264RandomAccessKind.RecoveryOpenGop,
                availability,
                UnaffectedClient));
    }

    [Fact]
    public void AnOpenThatFindsIdrChangesNothing()
    {
        Assert.Equal(
            PlaybackVariant.Native,
            PlaybackVariantPolicy.ReconcileAfterOpen(
                PlaybackVariant.Native,
                H264RandomAccessKind.Idr,
                new PlaybackVariantAvailability(false, true),
                AffectedClient));
    }

    private static PlaybackVariant[] Variants(System.Collections.Generic.IReadOnlyList<VariantOffer> offers)
        => [.. System.Linq.Enumerable.Select(offers, offer => offer.Variant)];

    private static ChannelMediaDescriptor Descriptor(string codec, byte streamType, H264RandomAccessKind randomAccess)
        => new()
        {
            ChannelId = "1",
            Container = "mpegts,ts",
            VideoStreamType = streamType,
            RandomAccess = randomAccess,
            IsTransportStream = true,
            Streams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = codec }],
        };
}
