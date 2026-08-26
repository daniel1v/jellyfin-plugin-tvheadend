using System;
using SkiaSharp;
using Xunit;

namespace TVHeadEnd.Tests.Api;

/// <summary>
/// Making a channel logo look like a logo standing in for artwork, rather than like artwork.
/// </summary>
/// <remarks>
/// Jellyfin draws an item's picture at whatever shape the view wants -- a tall poster here, a wide
/// thumbnail there -- and fills the frame with it. A 400x240 logo handed over as it stands is
/// enlarged to the size of the tile and cropped differently in every view. A square with a wide
/// margin survives both crops and keeps the logo small.
/// </remarks>
public class SquareCanvasTests
{
    /// <summary>
    /// What a 2:3 poster crop keeps of a square: the middle two-thirds of its width.
    /// </summary>
    private const double PosterCropKeepsWidth = 2.0 / 3.0;

    /// <summary>
    /// What a 16:9 thumbnail crop keeps of a square: the middle nine-sixteenths of its height.
    /// </summary>
    private const double ThumbnailCropKeepsHeight = 9.0 / 16.0;

    [Theory]
    [InlineData(400, 240)]
    [InlineData(240, 400)]
    [InlineData(512, 512)]
    [InlineData(1000, 120)]
    public void TheLogoSurvivesBeingCroppedToEitherShape(int width, int height)
    {
        // The whole point of the square. Whichever way Jellyfin crops it, the logo is still inside
        // what is left -- with room to spare, so it does not sit against the edge of the crop.
        var side = TVHeadEnd.Api.SquareCanvas.Fit(width, height);

        Assert.True(width <= side * PosterCropKeepsWidth, "the poster crop would cut the sides off");
        Assert.True(height <= side * ThumbnailCropKeepsHeight, "the thumbnail crop would cut the top off");
    }

    [Theory]
    [InlineData(400, 240)]
    [InlineData(240, 400)]
    [InlineData(512, 512)]
    public void TheLogoIsSmallInTheFrameRatherThanFillingIt(int width, int height)
    {
        // "Padded" means visibly padded: the picture takes up well under half the square's area,
        // so it reads as a logo on a card and not as a picture that happens to be the wrong shape.
        var side = TVHeadEnd.Api.SquareCanvas.Fit(width, height);

        Assert.True((double)(width * height) / (side * side) < 0.35);
    }

    [Fact]
    public void TheLogoIsNeverEnlargedToFillTheSquare()
    {
        // Blowing a 400 pixel logo up to poster size was half of what looked wrong. The square
        // grows around it instead, and Jellyfin scales the whole thing down.
        var padded = TVHeadEnd.Api.SquareCanvas.Pad(Logo(400, 240, SKColors.CornflowerBlue));

        Assert.NotNull(padded);

        using var result = SKBitmap.Decode(padded);
        Assert.True(result.Width >= 400);
        Assert.True(result.Height >= 240);
    }

    [Fact]
    public void WhatComesBackIsSquare()
    {
        var padded = TVHeadEnd.Api.SquareCanvas.Pad(Logo(400, 240, SKColors.CornflowerBlue));

        using var result = SKBitmap.Decode(padded);
        Assert.Equal(result.Width, result.Height);
    }

    [Fact]
    public void ThereIsMarginOnEverySideAndNotOnlyAboveAndBelow()
    {
        // The first attempt grew the canvas downwards only, so the logo still touched both edges
        // and looked no smaller. Every side is checked, in the picture that actually comes back.
        var padded = TVHeadEnd.Api.SquareCanvas.Pad(Logo(400, 240, SKColors.CornflowerBlue));

        using var result = SKBitmap.Decode(padded);

        var horizontal = (result.Width - 400) / 2;
        var vertical = (result.Height - 240) / 2;

        Assert.True(horizontal > 40, "no room to the left and right");
        Assert.True(vertical > 40, "no room above and below");
    }

    [Fact]
    public void TheMarginContinuesWhateverTheLogoSitsOn()
    {
        // Taken from the logo's own corner, so the join does not show. A neutral grey would be a
        // visible border around anything whose background is not grey.
        var padded = TVHeadEnd.Api.SquareCanvas.Pad(Logo(400, 240, SKColors.CornflowerBlue));

        using var result = SKBitmap.Decode(padded);
        var corner = result.GetPixel(5, 5);

        Assert.Equal(SKColors.CornflowerBlue.Red, corner.Red);
        Assert.Equal(SKColors.CornflowerBlue.Green, corner.Green);
        Assert.Equal(SKColors.CornflowerBlue.Blue, corner.Blue);
    }

    [Fact]
    public void ALogoDrawnForTransparencyStaysTransparent()
    {
        // Filling it in would put a box behind a logo that was made to sit on whatever is there.
        var padded = TVHeadEnd.Api.SquareCanvas.Pad(Logo(400, 240, SKColors.Transparent));

        using var result = SKBitmap.Decode(padded);
        Assert.Equal(0, result.GetPixel(5, 5).Alpha);
    }

    [Fact]
    public void SomethingThatIsNotAPictureIsNotPadded()
    {
        // The caller serves the original when this answers null, so a source Skia cannot read
        // costs the picture nothing.
        Assert.Null(TVHeadEnd.Api.SquareCanvas.Pad([0x00, 0x01, 0x02, 0x03]));
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
