using System.Collections.Generic;
using System.Linq;
using TVHeadEnd.Core.Media;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// Reading one H.264 access unit far enough to say whether it holds an IDR picture.
/// </summary>
/// <remarks>
/// The whole of the plugin's bitstream analysis, and the thing that decides whether a client whose
/// decoder needs an IDR is given this entry point or sent past it. Both of its answers matter, and
/// neither may depend on a picture that arrives after the one being judged.
/// </remarks>
public class H264AccessUnitScannerTests
{
    [Fact]
    public void AnIdrInTheAccessUnitIsFound()
    {
        var scanner = new H264AccessUnitScanner();

        scanner.Scan(AccessUnit(idr: true));

        Assert.True(scanner.CarriesIdr);
    }

    [Fact]
    public void APictureWithoutOneIsNotCredited()
    {
        // An access unit delimiter, parameter sets, a recovery point message and a non-IDR slice:
        // a valid DVB access point, and the shape Das Erste sends at every one of them.
        var scanner = new H264AccessUnitScanner();

        scanner.Scan(AccessUnit(idr: false));

        Assert.False(scanner.CarriesIdr);
    }

    [Fact]
    public void AnIdrInALaterAccessUnitOfTheSamePesDoesNotQualifyTheFirst()
    {
        // The reason the boundary is read from the syntax rather than from the payload unit start.
        // A PES may carry several access units, and a decoder told to begin at the first is not
        // helped by an IDR in the second.
        var scanner = new H264AccessUnitScanner();

        scanner.Scan([.. AccessUnit(idr: false), .. AccessUnit(idr: true)]);

        Assert.True(scanner.Completed);
        Assert.False(scanner.CarriesIdr);
    }

    [Fact]
    public void TheAccessUnitEndsAtTheDelimiterThatStartsTheNext()
    {
        var scanner = new H264AccessUnitScanner();

        scanner.Scan(AccessUnit(idr: true));
        Assert.False(scanner.Completed);

        scanner.Scan(AccessUnit(idr: false));
        Assert.True(scanner.Completed);
        Assert.True(scanner.CarriesIdr);
    }

    [Fact]
    public void AStreamWithoutDelimitersEndsTheUnitAtTheNextPicture()
    {
        // Where no access unit delimiter is sent, a slice whose first macroblock is zero begins a
        // picture. That is the first Exp-Golomb value of the slice header, and it is zero exactly
        // when the top bit of the byte after the NAL header is set.
        var scanner = new H264AccessUnitScanner();

        scanner.Scan([.. Slice(idr: false), .. Slice(idr: true)]);

        Assert.True(scanner.Completed);
        Assert.False(scanner.CarriesIdr);
    }

    [Fact]
    public void ASecondSliceOfTheSamePictureDoesNotEndIt()
    {
        // first_mb_in_slice is not zero, so this continues the picture already open.
        var scanner = new H264AccessUnitScanner();

        scanner.Scan([.. Slice(idr: true), 0x00, 0x00, 0x01, 0x61, 0x20]);

        Assert.False(scanner.Completed);
        Assert.True(scanner.CarriesIdr);
    }

    [Fact]
    public void AStartCodeSplitAcrossTwoReadsIsStillFound()
    {
        // A packet boundary falls wherever it falls, and an IDR missed because of one would send a
        // decoder past a perfectly good entry point.
        var scanner = new H264AccessUnitScanner();

        scanner.Scan([0x41, 0x00, 0x00]);
        scanner.Scan([0x01, 0x65, 0x88]);

        Assert.True(scanner.CarriesIdr);
    }

    [Fact]
    public void AResetForgetsTheFindingAndTheBytesBeforeIt()
    {
        var scanner = new H264AccessUnitScanner();
        scanner.Scan(AccessUnit(idr: true));

        scanner.Reset();

        Assert.False(scanner.CarriesIdr);
        Assert.False(scanner.Completed);
        scanner.Scan([0x01, 0x65]);
        Assert.False(scanner.CarriesIdr);
    }

    /// <summary>
    /// One access unit in the shape a DVB broadcast sends it: delimiter, parameter sets, a
    /// supplemental message, then the picture.
    /// </summary>
    private static byte[] AccessUnit(bool idr) =>
    [
        0x00, 0x00, 0x01, 0x09, 0x10,
        0x00, 0x00, 0x01, 0x67, 0x42,
        0x00, 0x00, 0x01, 0x68, 0xCE,
        0x00, 0x00, 0x01, 0x06, 0x06,
        .. Slice(idr),
    ];

    /// <summary>
    /// A coded slice that starts a picture, so its first byte carries first_mb_in_slice = 0.
    /// </summary>
    private static byte[] Slice(bool idr) => [0x00, 0x00, 0x01, idr ? (byte)0x65 : (byte)0x61, 0x88];
}
