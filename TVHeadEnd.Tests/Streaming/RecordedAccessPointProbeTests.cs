using System.Collections.Generic;
using System.IO;
using System.Linq;
using TVHeadEnd.Core.Media;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// What a sample of a recording is allowed to establish about where a decoder may join it.
/// </summary>
/// <remarks>
/// The recording half of the live IDR question, and it carries the same risk in both directions.
/// Saying "recovery only" too readily re-encodes recordings that never needed it; missing it
/// leaves a recording that opens on a black screen for the one client that cannot start without
/// an IDR picture. Everything here is about the bytes fetched, and nothing here is about a client.
/// </remarks>
public class RecordedAccessPointProbeTests
{
    private const int VideoPid = 0x13ed;
    private const byte H264 = 0x1B;
    private const byte Mpeg2Video = 0x02;

    [Fact]
    public void ARecordingWhoseAccessPointsAllOpenOnRecoveryPointsIsSeenAsSuch()
    {
        // The Das Erste case: every access point signalled, none of them an IDR.
        var sample = Stream(RecoveryAccessPoint(), RecoveryAccessPoint(), RecoveryAccessPoint());

        Assert.Equal(
            H264EntryPointEvidence.RecoveryOnlyObserved,
            RecordedH264AccessPointProbe.Examine(sample, Map(H264)));
    }

    [Fact]
    public void OneIdrAnywhereInTheSampleIsEnoughToSettleIt()
    {
        // The whole sample is read, not just its first few access points. A recording that opens
        // on recovery points and carries an IDR a minute in is a recording the client can start.
        var sample = Stream(RecoveryAccessPoint(), RecoveryAccessPoint(), RecoveryAccessPoint(), IdrAccessPoint());

        Assert.Equal(
            H264EntryPointEvidence.IdrObserved,
            RecordedH264AccessPointProbe.Examine(sample, Map(H264)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TooFewAccessPointsSettleNothing(int accessPoints)
    {
        var sample = Stream(Enumerable.Range(0, accessPoints).SelectMany(_ => RecoveryAccessPoint()).ToArray());

        Assert.Equal(
            H264EntryPointEvidence.Insufficient,
            RecordedH264AccessPointProbe.Examine(sample, Map(H264)));
    }

    [Fact]
    public void ThePictureTheSampleRanOutOnIsNotCounted()
    {
        // Three access points, but the third was never read to its end. Calling the recording
        // recovery-only on two whole pictures and one guess is exactly the inference that a
        // bounded sample cannot support.
        var sample = Stream(RecoveryAccessPoint(), RecoveryAccessPoint(), RecoveryPicture());

        Assert.Equal(
            H264EntryPointEvidence.Insufficient,
            RecordedH264AccessPointProbe.Examine(sample, Map(H264)));
    }

    [Fact]
    public void AnMpeg2RecordingIsNeverScannedHoweverItsSliceStartCodesRead()
    {
        // The trap. The MPEG-2 slice start code for picture row five is 00 00 01 05, which
        // satisfies the H.264 IDR pattern by coincidence -- measured at 205 matches in eight
        // megabytes of RTL. Gating on the stream type is what keeps that from being read either
        // as an IDR or, worse, as its absence.
        var sample = Stream(Mpeg2SliceAccessPoint(), Mpeg2SliceAccessPoint(), Mpeg2SliceAccessPoint());

        Assert.Equal(
            H264EntryPointEvidence.Insufficient,
            RecordedH264AccessPointProbe.Examine(sample, Map(Mpeg2Video)));
    }

    [Fact]
    public void ARecordingWithNoProgramMapIsNotGuessedAt()
    {
        var sample = Stream(RecoveryAccessPoint(), RecoveryAccessPoint(), RecoveryAccessPoint());

        Assert.Equal(
            H264EntryPointEvidence.Insufficient,
            RecordedH264AccessPointProbe.Examine(sample, null));
    }

    [Fact]
    public void ASampleThatIsNotATransportStreamIsAbandonedRatherThanRead()
    {
        var sample = new MemoryStream(Enumerable.Repeat((byte)0xFF, 4096).ToArray());

        Assert.Equal(
            H264EntryPointEvidence.Insufficient,
            RecordedH264AccessPointProbe.Examine(sample, Map(H264)));
    }

    [Fact]
    public void ASampleShorterThanAPacketYieldsNothing()
    {
        var sample = new MemoryStream([0x47, 0x00, 0x00]);

        Assert.Equal(
            H264EntryPointEvidence.Insufficient,
            RecordedH264AccessPointProbe.Examine(sample, Map(H264)));
    }

    [Fact]
    public void PacketsOfOtherStreamsAreNotReadAsVideo()
    {
        // An audio packet carrying the same bytes is still an audio packet. Only the PID the
        // program map calls video is followed.
        var sample = Stream(
            RecoveryAccessPoint(),
            Packet(VideoPid + 1, startsUnit: true, randomAccess: true, IdrPicture()),
            RecoveryAccessPoint(),
            RecoveryAccessPoint());

        Assert.Equal(
            H264EntryPointEvidence.RecoveryOnlyObserved,
            RecordedH264AccessPointProbe.Examine(sample, Map(H264)));
    }

    private static ProgramMapTable Map(byte videoStreamType) => new(
        1,
        VideoPid,
        [
            new ProgramMapEntry { StreamType = videoStreamType, Pid = VideoPid, Kind = ElementaryStreamKind.Video },
            new ProgramMapEntry { StreamType = 0x03, Pid = VideoPid + 1, Kind = ElementaryStreamKind.Audio },
        ]);

    private static MemoryStream Stream(params byte[][] parts)
        => new(parts.SelectMany(part => part).ToArray());

    /// <summary>
    /// A signalled access point whose picture opens on a recovery point, followed by the start of
    /// the next picture -- which is what tells a reader the first one is over.
    /// </summary>
    private static byte[] RecoveryAccessPoint() => [.. RecoveryPicture(), .. NextPicture()];

    private static byte[] IdrAccessPoint() => [.. Packet(VideoPid, true, true, IdrPicture()), .. NextPicture()];

    private static byte[] RecoveryPicture()
        => Packet(VideoPid, true, true, [0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00, 0x01, 0x06, 0x06, 0x02, 0x00, 0x00, 0x00, 0x01, 0x61]);

    private static byte[] IdrPicture()
        => [0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x00, 0x00, 0x01, 0x65, 0x88];

    private static byte[] NextPicture()
        => Packet(VideoPid, true, false, [0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00, 0x01, 0x61, 0x88]);

    /// <summary>
    /// An MPEG-2 access point whose payload happens to contain the H.264 IDR byte pattern.
    /// </summary>
    private static byte[] Mpeg2SliceAccessPoint() =>
    [
        .. Packet(VideoPid, true, true, [0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x05, 0x80, 0x00, 0x00, 0x01, 0x05]),
        .. Packet(VideoPid, true, false, [0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x05]),
    ];

    private static byte[] Packet(int pid, bool startsUnit, bool randomAccess, IReadOnlyList<byte> payload)
    {
        var packet = new byte[TransportStreamPacketLength];
        packet[0] = 0x47;
        packet[1] = (byte)(((pid >> 8) & 0x1F) | (startsUnit ? 0x40 : 0x00));
        packet[2] = (byte)(pid & 0xFF);

        int offset;
        if (randomAccess)
        {
            packet[3] = 0x30;
            packet[4] = 1;
            packet[5] = 0x40;
            offset = 6;
        }
        else
        {
            packet[3] = 0x10;
            offset = 4;
        }

        for (var index = 0; index < payload.Count; index++)
        {
            packet[offset + index] = payload[index];
        }

        return packet;
    }

    private const int TransportStreamPacketLength = 188;
}
