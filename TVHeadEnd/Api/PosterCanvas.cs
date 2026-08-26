using System;
using SkiaSharp;

namespace TVHeadEnd.Api
{
    /// <summary>
    /// Puts a wide picture on a poster-shaped canvas instead of letting a client stretch it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A TVHeadend channel logo is 400x240 -- landscape, 1.67:1 -- and Jellyfin renders a
    /// recording's primary image as a 2:3 poster. Handing the logo over as it stands produced a
    /// small landscape picture blown up into a portrait frame, which is the one thing worse than
    /// no picture at all. There is no way to tell Jellyfin "this is a logo, letterbox it", so the
    /// letterboxing is done here and it receives an image that is already the right shape.
    /// </para>
    /// <para>
    /// Nothing is scaled. The canvas grows around the picture at its native size, so a 400x240
    /// logo becomes 400x600 with the logo centred, and Jellyfin scales that down for whatever the
    /// client asked for. Enlarging a 400 pixel logo to fill a poster was half of what looked bad.
    /// </para>
    /// </remarks>
    internal static class PosterCanvas
    {
        /// <summary>
        /// The shape Jellyfin draws a primary image in: two wide by three high.
        /// </summary>
        private const double PosterAspect = 2.0 / 3.0;

        /// <summary>
        /// How far from that shape a picture may already be before it is left alone.
        /// </summary>
        /// <remarks>
        /// A picture that is close enough to a poster is a poster. Padding it would add a border
        /// nobody asked for and cost a re-encode for nothing.
        /// </remarks>
        private const double Tolerance = 0.05;

        /// <summary>
        /// The canvas that holds a picture of this size at its native size, in poster shape.
        /// </summary>
        /// <remarks>
        /// Whichever side is already too long decides: a wide picture keeps its width and gains
        /// height, a tall one keeps its height and gains width. Either way the source fits without
        /// being touched.
        /// </remarks>
        /// <param name="width">The source width.</param>
        /// <param name="height">The source height.</param>
        /// <returns>The canvas size, or the source size where it is poster-shaped already.</returns>
        internal static (int Width, int Height) Fit(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return (width, height);
            }

            var aspect = (double)width / height;
            if (Math.Abs(aspect - PosterAspect) <= Tolerance)
            {
                return (width, height);
            }

            return aspect > PosterAspect
                ? (width, (int)Math.Round(width / PosterAspect))
                : ((int)Math.Round(height * PosterAspect), height);
        }

        /// <summary>
        /// Redraws a picture centred on a poster-shaped canvas.
        /// </summary>
        /// <remarks>
        /// The background is the picture's own top-left pixel, so the padding continues whatever
        /// the logo sits on and the join does not show. Where that pixel is transparent the canvas
        /// stays transparent and the card behind it shows through, which is what a logo drawn for
        /// transparency expects.
        /// </remarks>
        /// <param name="source">The encoded picture.</param>
        /// <returns>
        /// A PNG of the padded picture, or <see langword="null"/> when the source could not be
        /// read or needed no padding -- in both cases the caller serves what it already had.
        /// </returns>
        internal static byte[]? Pad(byte[] source)
        {
            ArgumentNullException.ThrowIfNull(source);

            // Decode throws rather than answering for something it cannot read -- an
            // ArgumentNullException naming an internal codec, which says nothing useful and is not
            // an argument the caller passed. Turned into the "no" this method promises.
            SKBitmap? picture;
            try
            {
                picture = SKBitmap.Decode(source);
            }
            catch (ArgumentException)
            {
                return null;
            }

            if (picture is null)
            {
                return null;
            }

            using var decoded = picture;

            var (width, height) = Fit(decoded.Width, decoded.Height);
            if (width == decoded.Width && height == decoded.Height)
            {
                return null;
            }

            var corner = decoded.GetPixel(0, 0);
            var background = corner.Alpha == 0 ? SKColors.Transparent : corner;

            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(background);
            surface.Canvas.DrawBitmap(
                decoded,
                (width - decoded.Width) / 2f,
                (height - decoded.Height) / 2f);
            surface.Canvas.Flush();

            using var image = surface.Snapshot();
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

            return encoded?.ToArray();
        }
    }
}
