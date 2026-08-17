using System;
using System.Collections.Generic;
using System.Linq;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Media;

/// <summary>
/// The defect this architecture exists to prevent: the H.264 IDR scanner matches
/// <c>00 00 01 X</c> with <c>X &amp; 0x1F == 5</c>, and the MPEG-2 slice start code for picture
/// row 5 satisfies that by coincidence. Measured on an RTL sample: 205 matches, the first at
/// 0.26 seconds, in a stream with no NAL units at all.
/// </summary>
public class Mpeg2IsNeverScannedForIdrTests
{
    private const int PacketLength = 188;
    private const int PmtPid = 0x002c;
    private const int VideoPid = 0x00a3;

    [Fact]
    public void AnMpeg2SliceStartCodeDoesNotReportAnIdr()
    {
        var probe = new VideoRandomAccessProbe();
        var conditioner = new TransportStreamConditioner(
            TransportStreamConditioner.EventInformationTablePid,
            probe);

        var source = Concat(
            ProgramAssociationTable(),
            ProgramMapTable(videoStreamType: 0x02),
            VideoPacketContaining([0x00, 0x00, 0x01, 0x05]));

        Condition(conditioner, source);

        Assert.Equal(0x02, conditioner.VideoStreamType);
        Assert.Equal(H264RandomAccessKind.NotApplicable, probe.Kind);
    }

    [Fact]
    public void TheSameBytesInH264DoReportAnIdr()
    {
        // The control: identical payload, only the PMT stream type differs.
        var probe = new VideoRandomAccessProbe();
        var conditioner = new TransportStreamConditioner(
            TransportStreamConditioner.EventInformationTablePid,
            probe);

        var source = Concat(
            ProgramAssociationTable(),
            ProgramMapTable(videoStreamType: 0x1B),
            VideoPacketContaining([0x00, 0x00, 0x01, 0x65]));

        Condition(conditioner, source);

        Assert.Equal(0x1B, conditioner.VideoStreamType);
        Assert.Equal(H264RandomAccessKind.Idr, probe.Kind);
    }

    [Fact]
    public void HevcIsNeverScannedEither()
    {
        var probe = new VideoRandomAccessProbe();
        var conditioner = new TransportStreamConditioner(
            TransportStreamConditioner.EventInformationTablePid,
            probe);

        var source = Concat(
            ProgramAssociationTable(),
            ProgramMapTable(videoStreamType: 0x24),
            VideoPacketContaining([0x00, 0x00, 0x01, 0x25]));

        Condition(conditioner, source);

        Assert.Equal(H264RandomAccessKind.NotApplicable, probe.Kind);
    }

    [Fact]
    public void TheStreamTypeSurvivesInThePmt()
    {
        // Losing it is what let the H.264 scan run over MPEG-2 in the first place.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);

        Condition(conditioner, Concat(ProgramAssociationTable(), ProgramMapTable(videoStreamType: 0x02)));

        Assert.Equal(0x02, conditioner.VideoStreamType);
        Assert.Equal(VideoPid, conditioner.VideoPid);
    }

    private static void Condition(TransportStreamConditioner conditioner, byte[] source)
    {
        var destination = new byte[TransportStreamConditioner.GetMaximumConditionedLength(source.Length)];
        conditioner.Condition(source, destination);
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    private static byte[] Packet(int pid, bool startsUnit = false)
    {
        var packet = new byte[PacketLength];
        packet[0] = 0x47;
        packet[1] = (byte)(((pid >> 8) & 0x1F) | (startsUnit ? 0x40 : 0x00));
        packet[2] = (byte)(pid & 0xFF);
        packet[3] = 0x10;
        return packet;
    }

    private static byte[] ProgramAssociationTable()
    {
        var packet = Packet(0x00, startsUnit: true);
        var section = new byte[] { 0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, 0xE0 | (byte)((PmtPid >> 8) & 0x1F), (byte)(PmtPid & 0xFF) };
        packet[4] = 0x00;
        section.CopyTo(packet, 5);
        return packet;
    }

    private static byte[] ProgramMapTable(byte videoStreamType)
    {
        var packet = Packet(PmtPid, startsUnit: true);
        var body = new List<byte>
        {
            0x02, 0xB0, 0x17, 0x00, 0x01, 0xC1, 0x00, 0x00,
            0xE0 | (byte)((VideoPid >> 8) & 0x1F), (byte)(VideoPid & 0xFF),
            0xF0, 0x00,
            videoStreamType, (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), (byte)(VideoPid & 0xFF), 0xF0, 0x00,
        };

        packet[4] = 0x00;
        body.CopyTo(packet, 5);
        return packet;
    }

    private static byte[] VideoPacketContaining(byte[] marker)
    {
        var packet = Packet(VideoPid, startsUnit: true);
        marker.CopyTo(packet, 8);
        return packet;
    }
}
