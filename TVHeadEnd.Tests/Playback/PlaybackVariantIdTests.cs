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
}
