using System;
using System.Collections.Generic;
using System.Linq;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

public class LiveTransportStreamConditionerTests
{
    private const int PacketLength = 188;
    private const int PmtPid = 0x13ec;
    private const int VideoPid = 0x13ed;
    private const int AudioPid = 0x13ee;

    [Fact]
    public void ConditionWithholdsEverythingBeforeTheFirstRandomAccessPoint()
    {
        // A tuner starts mid-GOP, so the leading video packets reference a picture
        // parameter set the decoder never received.
        var conditioner = new LiveTransportStreamConditioner(LiveTransportStreamConditioner.EventInformationTablePid);
        var source = Concat(
            ProgramAssociationTable(),
            ProgramMapTable(),
            VideoPacket(startsUnit: true, randomAccess: false),
            AudioPacket(),
            VideoPacket(startsUnit: false, randomAccess: false));

        var written = Condition(conditioner, source, out _);

        Assert.Equal(0, written);
        Assert.False(conditioner.HasStarted);
    }

    [Fact]
    public void ConditionEmitsTheTablesAheadOfTheFirstRandomAccessPoint()
    {
        var conditioner = new LiveTransportStreamConditioner(LiveTransportStreamConditioner.EventInformationTablePid);
        var source = Concat(
            ProgramAssociationTable(),
            ProgramMapTable(),
            VideoPacket(startsUnit: true, randomAccess: false),
            VideoPacket(startsUnit: true, randomAccess: true),
            AudioPacket());

        Condition(conditioner, source, out var output);

        Assert.True(conditioner.HasStarted);

        // The player cannot map the elementary streams without the tables, and they were
        // withheld along with everything else ahead of the keyframe.
        Assert.Equal([0x00, PmtPid, VideoPid, AudioPid], PidsOf(output));
    }

    [Fact]
    public void ConditionPassesEverythingThroughOnceStarted()
    {
        var conditioner = new LiveTransportStreamConditioner(LiveTransportStreamConditioner.EventInformationTablePid);
        Condition(
            conditioner,
            Concat(ProgramAssociationTable(), ProgramMapTable(), VideoPacket(startsUnit: true, randomAccess: true)),
            out _);

        Condition(
            conditioner,
            Concat(AudioPacket(), VideoPacket(startsUnit: false, randomAccess: false), AudioPacket()),
            out var output);

        Assert.Equal([AudioPid, VideoPid, AudioPid], PidsOf(output));
    }

    [Fact]
    public void ConditionRemovesTheEventInformationTablePidAfterStarting()
    {
        var conditioner = new LiveTransportStreamConditioner(LiveTransportStreamConditioner.EventInformationTablePid);
        var source = Concat(
            ProgramAssociationTable(),
            ProgramMapTable(),
            VideoPacket(startsUnit: true, randomAccess: true),
            Packet(LiveTransportStreamConditioner.EventInformationTablePid),
            AudioPacket());

        Condition(conditioner, source, out var output);

        Assert.DoesNotContain(LiveTransportStreamConditioner.EventInformationTablePid, PidsOf(output));
        Assert.Equal([0x00, PmtPid, VideoPid, AudioPid], PidsOf(output));
    }

    [Fact]
    public void ConditionReassemblesPacketsSplitAcrossChunks()
    {
        // The upstream HTTP body arrives in chunks that do not respect packet boundaries.
        var conditioner = new LiveTransportStreamConditioner(LiveTransportStreamConditioner.EventInformationTablePid);
        var stream = Concat(
            ProgramAssociationTable(),
            ProgramMapTable(),
            VideoPacket(startsUnit: true, randomAccess: true),
            Packet(LiveTransportStreamConditioner.EventInformationTablePid),
            AudioPacket());
        var destination = new byte[LiveTransportStreamConditioner.GetMaximumConditionedLength(77)];
        var kept = new List<byte>();

        foreach (var chunk in Chunk(stream, 77))
        {
            var written = conditioner.Condition(chunk, destination);
            kept.AddRange(destination.AsSpan(0, written).ToArray());
        }

        Assert.Equal([0x00, PmtPid, VideoPid, AudioPid], PidsOf(kept.ToArray()));
    }

    [Fact]
    public void ConditionWaitsForTheTablesBeforeStarting()
    {
        // Without the PMT the video PID is unknown, so a keyframe cannot be recognised.
        var conditioner = new LiveTransportStreamConditioner(LiveTransportStreamConditioner.EventInformationTablePid);

        var written = Condition(conditioner, VideoPacket(startsUnit: true, randomAccess: true), out _);

        Assert.Equal(0, written);
        Assert.False(conditioner.HasStarted);
    }

    [Fact]
    public void ConditionResynchronisesAfterLeadingGarbage()
    {
        var conditioner = new LiveTransportStreamConditioner(LiveTransportStreamConditioner.EventInformationTablePid);
        var source = Concat(
            [0x00, 0x01, 0x02],
            ProgramAssociationTable(),
            ProgramMapTable(),
            VideoPacket(startsUnit: true, randomAccess: true));

        Condition(conditioner, source, out var output);

        Assert.Equal([0x00, PmtPid, VideoPid], PidsOf(output));
    }

    [Fact]
    public void ProgramLayoutNamesEveryElementaryStreamInPmtOrder()
    {
        // The fingerprint a cached probe is validated against: reusing a probe is only safe
        // while the broadcast announces the same streams in the same order, because Jellyfin
        // addresses the track to copy by its position in the list.
        var conditioner = new LiveTransportStreamConditioner(LiveTransportStreamConditioner.EventInformationTablePid);

        Condition(conditioner, Concat(ProgramAssociationTable(), ProgramMapTable()), out _);

        Assert.Equal($"1b:{VideoPid:x4},03:{AudioPid:x4}", conditioner.ProgramLayout);
    }

    [Fact]
    public void ProgramLayoutIsUnknownBeforeThePmtArrives()
    {
        var conditioner = new LiveTransportStreamConditioner(LiveTransportStreamConditioner.EventInformationTablePid);

        Condition(conditioner, ProgramAssociationTable(), out _);

        Assert.Null(conditioner.ProgramLayout);
    }

    [Fact]
    public void ProgramLayoutChangesWhenTheBroadcastAddsATrack()
    {
        var withoutExtraTrack = new LiveTransportStreamConditioner(LiveTransportStreamConditioner.EventInformationTablePid);
        Condition(withoutExtraTrack, Concat(ProgramAssociationTable(), ProgramMapTable()), out _);

        var withExtraTrack = new LiveTransportStreamConditioner(LiveTransportStreamConditioner.EventInformationTablePid);
        Condition(withExtraTrack, Concat(ProgramAssociationTable(), ProgramMapTableWithSecondAudioTrack()), out _);

        Assert.NotEqual(withoutExtraTrack.ProgramLayout, withExtraTrack.ProgramLayout);
    }

    [Fact]
    public void ConstructorRejectsAPidOutsideTheTransportStreamRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiveTransportStreamConditioner(0x2000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiveTransportStreamConditioner(-1));
    }

    private static int Condition(LiveTransportStreamConditioner conditioner, byte[] source, out byte[] output)
    {
        var destination = new byte[LiveTransportStreamConditioner.GetMaximumConditionedLength(source.Length)];
        var written = conditioner.Condition(source, destination);
        output = destination.AsSpan(0, written).ToArray();
        return written;
    }

    private static byte[] Packet(int pid, bool startsUnit = false, bool randomAccess = false)
    {
        var packet = new byte[PacketLength];
        packet[0] = 0x47;
        packet[1] = (byte)(((pid >> 8) & 0x1F) | (startsUnit ? 0x40 : 0x00));
        packet[2] = (byte)(pid & 0xFF);

        if (randomAccess)
        {
            // Adaptation field and payload, with the random access indicator set.
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

    private static byte[] VideoPacket(bool startsUnit, bool randomAccess)
        => Packet(VideoPid, startsUnit, randomAccess);

    private static byte[] AudioPacket() => Packet(AudioPid);

    private static byte[] ProgramAssociationTable()
    {
        var section = new byte[]
        {
            0x00, // table_id
            0xB0, 0x0D, // section_length = 13
            0x00, 0x01, // transport_stream_id
            0xC1, 0x00, 0x00, // version, section numbers
            0x00, 0x01, // program_number 1
            0xE0 | ((PmtPid >> 8) & 0x1F), PmtPid & 0xFF,
            0x00, 0x00, 0x00, 0x00, // CRC
        };

        return SectionPacket(0x00, section);
    }

    private static byte[] ProgramMapTable()
    {
        var section = new byte[]
        {
            0x02, // table_id
            0xB0, 0x17, // section_length = 23
            0x00, 0x01, // program_number
            0xC1, 0x00, 0x00, // version, section numbers
            0xE0 | ((VideoPid >> 8) & 0x1F), VideoPid & 0xFF, // PCR PID
            0xF0, 0x00, // program_info_length = 0
            0x1B, (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF, 0xF0, 0x00, // H.264
            0x03, (byte)(0xE0 | ((AudioPid >> 8) & 0x1F)), AudioPid & 0xFF, 0xF0, 0x00, // MPEG audio
            0x00, 0x00, 0x00, 0x00, // CRC
        };

        return SectionPacket(PmtPid, section);
    }

    private static byte[] ProgramMapTableWithSecondAudioTrack()
    {
        const int SecondAudioPid = 0x13ef;
        var section = new byte[]
        {
            0x02, // table_id
            0xB0, 0x1C, // section_length = 28
            0x00, 0x01, // program_number
            0xC1, 0x00, 0x00, // version, section numbers
            0xE0 | ((VideoPid >> 8) & 0x1F), VideoPid & 0xFF, // PCR PID
            0xF0, 0x00, // program_info_length = 0
            0x1B, (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF, 0xF0, 0x00, // H.264
            0x03, (byte)(0xE0 | ((AudioPid >> 8) & 0x1F)), AudioPid & 0xFF, 0xF0, 0x00, // MPEG audio
            0x03, (byte)(0xE0 | ((SecondAudioPid >> 8) & 0x1F)), SecondAudioPid & 0xFF, 0xF0, 0x00, // second audio
            0x00, 0x00, 0x00, 0x00, // CRC
        };

        return SectionPacket(PmtPid, section);
    }

    private static byte[] SectionPacket(int pid, byte[] section)
    {
        var packet = Packet(pid, startsUnit: true);
        packet[4] = 0x00; // pointer_field
        section.CopyTo(packet, 5);
        return packet;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static IEnumerable<byte[]> Chunk(byte[] source, int size)
    {
        for (var offset = 0; offset < source.Length; offset += size)
        {
            yield return source[offset..Math.Min(offset + size, source.Length)];
        }
    }

    private static int[] PidsOf(ReadOnlySpan<byte> data)
    {
        var pids = new int[data.Length / PacketLength];
        for (var i = 0; i < pids.Length; i++)
        {
            var packet = data.Slice(i * PacketLength, PacketLength);
            pids[i] = ((packet[1] & 0x1F) << 8) | packet[2];
        }

        return pids;
    }
}
