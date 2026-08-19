using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// The one client-dependent decision the live path makes: whether a broadcast has to be re-encoded
/// before a decoder that needs IDR pictures will start on it.
/// </summary>
/// <remarks>
/// The condition is narrow on purpose, and these tests are the fence around it. Firing when it
/// should not costs a processor core per viewer for nothing; not firing when it should leaves a
/// channel that spins for ever on Android and reports no error anywhere.
/// </remarks>
public class AndroidIdrTests
{
    private const int PacketLength = 188;
    private const int PmtPid = 0x13ec;
    private const int VideoPid = 0x13ed;
    private const int AudioPid = 0x13ee;

    private const byte H264 = 0x1B;
    private const byte Mpeg2Video = 0x02;

    [Theory]
    [InlineData("Jellyfin Android", true)]
    [InlineData("AndroidTV", true)]
    [InlineData("Jellyfin Web", false)]
    [InlineData("Jellyfin Media Player", false)]
    [InlineData("", false)]
    public void TheClientIsReadFromTheSessionJellyfinAlreadyAuthenticated(string claim, bool expected)
    {
        // The authentication header, not the user agent: every client Jellyfin serves states its
        // name there, and that is the only statement about the caller that cannot be a
        // coincidence.
        var context = new DefaultHttpContext();
        if (claim.Length > 0)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-Client", claim)]));
        }

        var client = new PlaybackClient(new HttpContextAccessor { HttpContext = context });

        Assert.Equal(expected, client.IsAndroid);
    }

    [Fact]
    public void WithNoRequestInFlightNoClientIsAssumed()
    {
        // A scheduled task or an internal call. Assuming a defect on no evidence would re-encode
        // every channel; assuming none delivers the broadcast, which is what it is for.
        Assert.False(new PlaybackClient(null).IsAndroid);
        Assert.False(new PlaybackClient(new HttpContextAccessor()).IsAndroid);
    }

    [Fact]
    public void AnH264BroadcastWhoseAccessPointCarriesAnIdrIsLeftAlone()
    {
        var conditioner = Start(H264, withIdr: true);

        Assert.True(conditioner.StartAccessUnitCarriesIdr);
        Assert.False(H264IdrNormalizer.IsNeeded(true, conditioner.VideoStreamType, conditioner.StartAccessUnitCarriesIdr));
        Assert.False(H264IdrNormalizer.IsNeeded(false, conditioner.VideoStreamType, conditioner.StartAccessUnitCarriesIdr));
    }

    [Fact]
    public void AnH264BroadcastWhoseAccessPointCarriesNoIdrIsReEncodedForAndroidAlone()
    {
        // The ARD case, measured: a valid DVB access point -- random access indicator set, a
        // recovery point rather than an IDR -- which FFmpeg starts on and MediaCodec does not.
        var conditioner = Start(H264, withIdr: false);

        Assert.False(conditioner.StartAccessUnitCarriesIdr);
        Assert.True(H264IdrNormalizer.IsNeeded(true, conditioner.VideoStreamType, conditioner.StartAccessUnitCarriesIdr));

        // Every other client receives the broadcast, because the broadcast is conformant.
        Assert.False(H264IdrNormalizer.IsNeeded(false, conditioner.VideoStreamType, conditioner.StartAccessUnitCarriesIdr));
    }

    [Fact]
    public void AnMpeg2BroadcastIsNeverReEncodedHoweverItsSliceStartCodesRead()
    {
        // The trap this used to fall into. The MPEG-2 slice start code for picture row five is
        // 00 00 01 05, which satisfies the H.264 IDR pattern by coincidence -- measured at 205
        // matches in eight megabytes of RTL. Gating on the stream type turns "no IDR found" into
        // "the question does not apply", which is what it always was.
        var conditioner = Start(Mpeg2Video, withIdr: false);

        Assert.Null(conditioner.StartAccessUnitCarriesIdr);
        Assert.False(H264IdrNormalizer.IsNeeded(true, conditioner.VideoStreamType, conditioner.StartAccessUnitCarriesIdr));
    }

    [Fact]
    public void ANormalizedStreamPublishesAJoinPointOnlyWhereAnIdrActuallyIs()
    {
        // The stream this plugin produced itself is held to the standard it exists to meet. A
        // signalled access point with no IDR at it is exactly the thing the re-encode removes, so
        // finding one is a reason to publish nothing rather than to trust the flag.
        var conditioner = new TransportStreamConditioner(
            TransportStreamConditioner.EventInformationTablePid,
            requireIdrAtAccessPoints: true);

        Condition(conditioner, Concat(Pat(), Pmt(H264), VideoAccessPoint(withIdr: true)), out _);
        Assert.True(conditioner.HasStarted);

        // A real one, recorded.
        var written = Condition(conditioner, VideoAccessPoint(withIdr: true), out _);
        Assert.Equal(PacketLength, written);
        Assert.Equal([0], conditioner.RandomAccessOffsets);

        // A flagged one that turns out to hold no IDR, followed by the next picture: not recorded.
        Condition(
            conditioner,
            Concat(VideoAccessPoint(withIdr: false), VideoPacket(startsUnit: true, randomAccess: false)),
            out _);
        Assert.Empty(conditioner.RandomAccessOffsets);
    }

    [Fact]
    public void ABroadcastIsTakenAtItsWordAboutItsOwnAccessPoints()
    {
        // The counterpart. Outside the normalized path the random access indicator is the
        // transmitter saying so, and second-guessing it would mean decoding the picture. An open
        // GOP access point is still where a decoder is meant to join.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);

        Condition(conditioner, Concat(Pat(), Pmt(H264), VideoAccessPoint(withIdr: false)), out _);
        Condition(conditioner, VideoAccessPoint(withIdr: false), out _);

        Assert.Equal([0], conditioner.RandomAccessOffsets);
    }

    private static TransportStreamConditioner Start(byte videoStreamType, bool withIdr)
    {
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);

        // The tables, the access point, and the rest of the picture it points at. The second
        // payload unit start is what settles the question for a picture that held no IDR.
        Condition(
            conditioner,
            Concat(
                Pat(),
                Pmt(videoStreamType),
                VideoAccessPoint(withIdr),
                VideoPacket(startsUnit: false, randomAccess: false),
                VideoPacket(startsUnit: true, randomAccess: false)),
            out _);

        return conditioner;
    }

    private static int Condition(TransportStreamConditioner conditioner, byte[] source, out byte[] output)
    {
        var destination = new byte[TransportStreamConditioner.GetMaximumConditionedLength(source.Length)];
        var written = conditioner.Condition(source, destination);
        output = destination.AsSpan(0, written).ToArray();
        return written;
    }

    /// <summary>
    /// A video packet that starts a picture and says a decoder may begin there, carrying either a
    /// real IDR slice or the recovery point an open GOP broadcast sends instead.
    /// </summary>
    private static byte[] VideoAccessPoint(bool withIdr)
    {
        var packet = VideoPacket(startsUnit: true, randomAccess: true);

        // Payload begins after the header and the two byte adaptation field.
        var payload = 6;

        // Access unit delimiter, then either an IDR slice or a non-IDR one preceded by the
        // recovery point message that makes it a valid DVB access point.
        byte[] nalUnits = withIdr
            ? [0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x00, 0x00, 0x01, 0x65, 0x88]
            : [0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00, 0x01, 0x06, 0x06, 0x02, 0x00, 0x00, 0x00, 0x01, 0x61];

        nalUnits.CopyTo(packet.AsSpan(payload));
        return packet;
    }

    private static byte[] VideoPacket(bool startsUnit, bool randomAccess)
        => Packet(VideoPid, startsUnit, randomAccess);

    private static byte[] Packet(int pid, bool startsUnit = false, bool randomAccess = false)
    {
        var packet = new byte[PacketLength];
        packet[0] = 0x47;
        packet[1] = (byte)(((pid >> 8) & 0x1F) | (startsUnit ? 0x40 : 0x00));
        packet[2] = (byte)(pid & 0xFF);

        if (randomAccess)
        {
            packet[3] = 0x30;
            packet[4] = 1;
            packet[5] = 0x40;
        }
        else
        {
            packet[3] = 0x10;
        }

        return packet;
    }

    private static byte[] Pat()
    {
        byte[] section =
        [
            0x00,
            0xB0, 0x0D,
            0x00, 0x01,
            0xC1, 0x00, 0x00,
            0x00, 0x01,
            (byte)(0xE0 | ((PmtPid >> 8) & 0x1F)), PmtPid & 0xFF,
        ];

        return SectionPacket(0x00, PsiSection.WithCrc(section));
    }

    private static byte[] Pmt(byte videoStreamType)
    {
        byte[] section =
        [
            0x02,
            0xB0, 0x17,
            0x00, 0x01,
            0xC1, 0x00, 0x00,
            (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF,
            0xF0, 0x00,
            videoStreamType, (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF, 0xF0, 0x00,
            0x03, (byte)(0xE0 | ((AudioPid >> 8) & 0x1F)), AudioPid & 0xFF, 0xF0, 0x00,
        ];

        return SectionPacket(PmtPid, PsiSection.WithCrc(section));
    }

    private static byte[] SectionPacket(int pid, IReadOnlyList<byte> section)
    {
        var packet = Packet(pid, startsUnit: true);
        packet[4] = 0x00;
        for (var index = 0; index < section.Count; index++)
        {
            packet[5 + index] = section[index];
        }

        return packet;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}
