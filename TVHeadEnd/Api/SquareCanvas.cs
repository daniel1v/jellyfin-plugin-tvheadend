using System;
using SkiaSharp;

namespace TVHeadEnd.Api
{
    /// <summary>
    /// Puts a logo in the middle of a square with a wide margin all round it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jellyfin does not draw an item's picture at one shape. The same logo appears in a tall
    /// poster frame in one view and a wide thumbnail frame in another, and it fills whichever
    /// frame it is given -- so a logo handed over as it stands is enlarged until it is the size of
    /// the tile, and cropped differently in every view.
    /// </para>
    /// <para>
    /// A square is what survives both. Cropping a square to a 2:3 poster keeps the middle
    /// two-thirds of its width; cropping it to a 16:9 thumbnail keeps the middle nine-sixteenths
    /// of its height. Anything inside both survives either, so the logo is drawn well within them
    /// and everything else is margin. The result is a small logo in a large frame, which is what a
    /// logo standing in for artwork should look like.
    /// </para>
    /// <para>
    /// Nothing is ever enlarged. The square is sized around the logo at its own size, so it is the
    /// margin that grows, and Jellyfin scales the whole down for whatever it is drawing.
    /// </para>
    /// </remarks>
    internal static class SquareCanvas
    {
        /// <summary>
        /// How much of the square's width the picture may use.
        /// </summary>
        /// <remarks>
        /// A 2:3 crop keeps the middle 0.667 of the width. Staying well inside that is what leaves
        /// a margin rather than reaching the edge of the crop.
        /// </remarks>
        private const double WidthShare = 0.55;

        /// <summary>
        /// How much of the square's height the picture may use.
        /// </summary>
        /// <remarks>
        /// A 16:9 crop keeps the middle 0.5625 of the height, so this is the tighter of the two
        /// and the one that decides for a tall picture.
        /// </remarks>
        private const double HeightShare = 0.45;

        /// <summary>
        /// The side of the square that holds a picture of this size with its margin.
        /// </summary>
        /// <remarks>
        /// Whichever share the picture comes closer to exhausting decides the side, so the picture
        /// is never scaled and never reaches its share on both axes at once.
        /// </remarks>
        /// <param name="width">The source width.</param>
        /// <param name="height">The source height.</param>
        /// <returns>The length of one side, or zero where the source has none.</returns>
        internal static int Fit(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return 0;
            }

            return (int)Math.Round(Math.Max(width / WidthShare, height / HeightShare));
        }

        /// <summary>
        /// Redraws a picture centred on a square, with margin on every side.
        /// </summary>
        /// <remarks>
        /// The margin is the picture's own top-left pixel, so it continues whatever the logo sits
        /// on and the join does not show. Where that pixel is transparent the square stays
        /// transparent and the card behind it shows through, which is what a logo drawn for
        /// transparency expects.
        /// </remarks>
        /// <param name="source">The encoded picture.</param>
        /// <returns>
        /// A PNG of the padded picture, or <see langword="null"/> when the source could not be
        /// read, in which case the caller serves what it already had.
        /// </returns>
        internal static byte[]? Pad(byte[] source)
        {
            ArgumentNullException.ThrowIfNull(source);

            // Decode throws rather than answering for something it cannot read, and what it throws
            // is an ArgumentNullException naming an internal codec: no use to anyone, and not
            // about an argument the caller passed. Turned into the "no" this method promises.
            SKBitmap? decoded;
            try
            {
                decoded = SKBitmap.Decode(source);
            }
            catch (ArgumentException)
            {
                return null;
            }

            if (decoded is null)
            {
                return null;
            }

            using var picture = decoded;

            var side = Fit(picture.Width, picture.Height);
            if (side <= 0)
            {
                return null;
            }

            var corner = picture.GetPixel(0, 0);
            var background = corner.Alpha == 0 ? SKColors.Transparent : corner;

            using var surface = SKSurface.Create(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(background);
            surface.Canvas.DrawBitmap(
                picture,
                (side - picture.Width) / 2f,
                (side - picture.Height) / 2f);
            surface.Canvas.Flush();

            using var image = surface.Snapshot();
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

            return encoded?.ToArray();
        }
    }
}
