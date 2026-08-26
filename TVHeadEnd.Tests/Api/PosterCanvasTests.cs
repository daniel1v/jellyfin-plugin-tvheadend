using System;
using SkiaSharp;
using Xunit;

namespace TVHeadEnd.Tests.Api;

/// <summary>
/// Making a wide picture fit the shape Jellyfin draws it in.
/// </summary>
/// <remarks>
/// A TVHeadend channel logo is 400x240 and a recording's primary image is a 2:3 poster. Published
/// as it stood, every recording tile was a small landscape picture blown up into a portrait frame.
/// There is no way to tell Jellyfin to letterbox it, so it is letterboxed here.
/// </remarks>
public class PosterCanvasTests
{
    [Fact]
    public void AWideLogoGainsHeightRatherThanBeingStretched()
    {
        // The real case: 400x240 becomes 400x600, which is 2:3. The logo keeps its own size and
        // sits in the middle -- nothing is scaled, because enlarging a 400 pixel logo to fill a
        // poster was half of what looked wrong.
        Assert.Equal((400, 600), TVHeadEnd.Api.PosterCanvas.Fit(400, 240));
    }

    [Fact]
    public void ATallPictureGainsWidthInstead()
    {
        // Whichever side is already too long decides, so the source always fits untouched.
        Assert.Equal((400, 600), TVHeadEnd.Api.PosterCanvas.Fit(200, 600));
    }

    [Theory]
    [InlineData(400, 600)]
    [InlineData(1000, 1500)]
    [InlineData(400, 590)]
    public void APictureThatIsAlreadyAPosterIsLeftAlone(int width, int height)
    {
        // Padding a poster would add a border nobody asked for and cost a re-encode for nothing.
        Assert.Equal((width, height), TVHeadEnd.Api.PosterCanvas.Fit(width, height));
    }

    [Fact]
    public void APaddedLogoReallyComesBackAsAPoster()
    {
        // End to end through Skia rather than arithmetic only: the bytes have to decode again, at
        // the size the geometry promised.
        var padded = TVHeadEnd.Api.PosterCanvas.Pad(Logo(400, 240, SKColors.CornflowerBlue));

        Assert.NotNull(padded);

        using var result = SKBitmap.Decode(padded);
        Assert.Equal(400, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Fact]
    public void ThePaddingContinuesWhateverTheLogoSitsOn()
    {
        // Taken from the logo's own corner, so the join does not show. A neutral grey would be a
        // visible band above and below anything whose background is not grey.
        var padded = TVHeadEnd.Api.PosterCanvas.Pad(Logo(400, 240, SKColors.CornflowerBlue));

        using var result = SKBitmap.Decode(padded);

        var top = result.GetPixel(10, 10);
        Assert.Equal(SKColors.CornflowerBlue.Red, top.Red);
        Assert.Equal(SKColors.CornflowerBlue.Green, top.Green);
        Assert.Equal(SKColors.CornflowerBlue.Blue, top.Blue);
    }

    [Fact]
    public void ALogoDrawnForTransparencyStaysTransparent()
    {
        // Filling it in would put a box behind a logo that was made to sit on whatever is there.
        var padded = TVHeadEnd.Api.PosterCanvas.Pad(Logo(400, 240, SKColors.Transparent));

        using var result = SKBitmap.Decode(padded);

        Assert.Equal(0, result.GetPixel(10, 10).Alpha);
    }

    [Fact]
    public void SomethingThatIsNotAPictureIsNotPadded()
    {
        // The caller serves the original when this answers null, so a source Skia cannot read
        // costs the picture nothing.
        Assert.Null(TVHeadEnd.Api.PosterCanvas.Pad([0x00, 0x01, 0x02, 0x03]));
    }

    [Fact]
    public void APosterIsNotReEncodedForNothing()
    {
        // Null here means "serve what you already had", which is both cheaper and lossless.
        Assert.Null(TVHeadEnd.Api.PosterCanvas.Pad(Logo(400, 600, SKColors.CornflowerBlue)));
    }

    private static byte[] Logo(int width, int height, SKColor background)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(background);

        using var paint = new SKPaint { Color = SKColors.White };
        surface.Canvas.DrawRect(width / 4f, height / 4f, width / 2f, height / 2f, paint);

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        return encoded.ToArray();
    }
}
