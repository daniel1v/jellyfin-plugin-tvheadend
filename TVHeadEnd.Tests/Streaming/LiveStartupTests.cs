using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using TVHeadEnd.Core.Media;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// When the conditioner may begin delivering, and what it refuses to publish.
/// </summary>
/// <remarks>
/// The startup condition used to be "wait for a video packet". A program with no video that this
/// plugin recognised therefore never started at all: the stream was withheld until the twenty
/// second limit and the client was left loading, which is the opposite of the intended fallback
/// -- publishing it undescribed so Jellyfin can inspect it.
/// </remarks>
public class LiveStartupTests
{
    private const int PacketLength = 188;
    private const int PmtPid = 0x13ec;
    private const int VideoPid = 0x13ed;
    private const int AudioPid = 0x13ee;

    [Fact]
    public void ARadioServiceStartsOnceItsTablesAreComplete()
    {
        // Audio only, and nothing to wait for beyond the tables.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);

        var written = Condition(
            conditioner,
            Concat(
                ProgramAssociation(),
                ProgramMap((0x03, AudioPid)),
                Packet(AudioPid, startsUnit: true),
                Packet(AudioPid)),
            out var output);

        Assert.True(conditioner.HasStarted);
        Assert.True(written > 0);
        Assert.Equal(0x00, ReadPid(output, 0));
        Assert.Equal(PmtPid, ReadPid(output, PacketLength));

        // Nothing was invented to start on.
        Assert.False(conditioner.StartedOnRandomAccessPoint);
        Assert.Empty(conditioner.AccessPoints);
    }

    [Fact]
    public void ATelevisionServiceWhoseVideoIsNotRecognisedStartsRatherThanStalling()
    {
        // Stream type 0x06 with no descriptor naming it: something is there, but the table does
        // not say what. Withholding it would leave the client loading for ever; delivering it
        // lets Jellyfin inspect the stream and decide for itself.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);

        var written = Condition(
            conditioner,
            Concat(
                ProgramAssociation(),
                ProgramMap((0x06, VideoPid), (0x03, AudioPid)),
                Packet(VideoPid, startsUnit: true),
                Packet(AudioPid, startsUnit: true)),
            out _);

        Assert.True(conditioner.HasStarted);
        Assert.True(written > 0);
    }

    [Fact]
    public void AnUnrecognisedExtraStreamKeepsItsIndexAndBlocksNothing()
    {
        // A recognised video stream is all a television channel needs. The entry beside it that
        // nothing identifies is described as data at the index the table gave it, so every later
        // -map argument still means what this says, and it is no reason to inspect the stream.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
        Condition(
            conditioner,
            Concat(ProgramAssociation(), ProgramMap((0x1B, VideoPid), (0x06, AudioPid))),
            out _);

        var description = LiveStreamDescription.FromProgramMap(conditioner.ProgramMap!, ChannelType.TV);

        Assert.NotNull(description);
        Assert.Equal(2, description!.Streams.Count);
        Assert.Equal(MediaStreamType.Video, description.Streams[0].Type);
        Assert.Equal(MediaStreamType.Data, description.Streams[1].Type);
        Assert.Equal([0, 1], description.Streams.Select(stream => stream.Index));
    }

    [Fact]
    public void ATelevisionServiceStillWaitsForAPointItsDecoderCanStartAt()
    {
        // The recognised-video case is unchanged: the tables alone are not enough, because
        // starting mid-picture is what no decoder recovers from.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);

        Condition(
            conditioner,
            Concat(
                ProgramAssociation(),
                ProgramMap((0x1B, VideoPid), (0x03, AudioPid)),
                Packet(AudioPid, startsUnit: true)),
            out var beforeVideo);

        Assert.False(conditioner.HasStarted);
        Assert.Empty(beforeVideo);

        Condition(conditioner, Packet(VideoPid, startsUnit: true, randomAccess: true), out var afterVideo);

        Assert.True(conditioner.HasStarted);
        Assert.True(conditioner.StartedOnRandomAccessPoint);
        Assert.NotEmpty(afterVideo);
    }

    [Fact]
    public void ATableAnnouncedForLaterIsNotActedOn()
    {
        // current_next_indicator clear means "this is how the program will be", and applying it
        // now would describe streams that are not there yet.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);

        Condition(
            conditioner,
            Concat(ProgramAssociation(), ProgramMap(currentNext: false, (0x1B, VideoPid))),
            out _);

        Assert.Null(conditioner.ProgramMap);
        Assert.False(conditioner.HasProgramTables);
    }

    [Fact]
    public void ADamagedTableIsNotActedOn()
    {
        // The program map is the only description of the stream there is, so a section that
        // arrived corrupt would be believed exactly as readily as a good one.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);

        var corrupted = ProgramMap((0x1B, VideoPid));
        corrupted[5 + 13] ^= 0xFF; // A byte inside the section body, leaving the CRC stale.

        Condition(conditioner, Concat(ProgramAssociation(), corrupted), out _);

        Assert.Null(conditioner.ProgramMap);
    }

    [Fact]
    public void AChangedProgramLayoutIsReportedOnce()
    {
        // Old join points describe the old layout; a reader sent to one would be given the new
        // tables and the old picture.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);

        Condition(conditioner, Concat(ProgramAssociation(), ProgramMap((0x1B, VideoPid), (0x03, AudioPid))), out _);
        Assert.False(conditioner.ProgramLayoutChanged);

        // The same table again changes nothing.
        Condition(conditioner, ProgramMap((0x1B, VideoPid), (0x03, AudioPid)), out _);
        Assert.False(conditioner.ProgramLayoutChanged);

        // An added track does.
        Condition(conditioner, ProgramMap((0x1B, VideoPid), (0x03, AudioPid), (0x03, 0x13ef)), out _);
        Assert.True(conditioner.ProgramLayoutChanged);

        conditioner.AcknowledgeProgramLayoutChange();
        Assert.False(conditioner.ProgramLayoutChanged);
    }

    [Fact]
    public void AccessPointsFoundBeforeALayoutChangeNeverLeaveTheConditioner()
    {
        // The case no amount of discarding afterwards can reach: the access point and the change
        // that invalidates it are in the same chunk, so the offsets handed on with the new tables
        // must already exclude it.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
        Condition(conditioner, Concat(ProgramAssociation(), ProgramMap((0x1B, VideoPid))), out _);

        var generation = conditioner.ProgramLayoutGeneration;

        Condition(
            conditioner,
            Concat(
                Packet(VideoPid, startsUnit: true, randomAccess: true),
                ProgramMap(true, (0x1B, VideoPid), (0x03, AudioPid))),
            out _);

        Assert.True(conditioner.ProgramLayoutChanged);
        Assert.NotEqual(generation, conditioner.ProgramLayoutGeneration);
        Assert.Empty(conditioner.AccessPoints);
    }

    private static int Condition(TransportStreamConditioner conditioner, byte[] source, out byte[] output)
    {
        var destination = new byte[TransportStreamConditioner.GetMaximumConditionedLength(source.Length)];
        var written = conditioner.Condition(source, destination);
        output = destination.AsSpan(0, written).ToArray();
        return written;
    }

    private static byte[] ProgramAssociation()
    {
        List<byte> section =
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

    private static byte[] ProgramMap(params (byte StreamType, int Pid)[] entries)
        => ProgramMap(true, entries);

    private static byte[] ProgramMap(bool currentNext, params (byte StreamType, int Pid)[] entries)
    {
        var body = new List<byte>();
        foreach (var entry in entries)
        {
            body.Add(entry.StreamType);
            body.Add((byte)(0xE0 | ((entry.Pid >> 8) & 0x1F)));
            body.Add((byte)(entry.Pid & 0xFF));
            body.Add(0xF0);
            body.Add(0x00);
        }

        List<byte> section =
        [
            0x02,
            0, 0,
            0x00, 0x01,
            (byte)(currentNext ? 0xC1 : 0xC0), 0x00, 0x00,
            (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF,
            0xF0, 0x00,
            .. body,
        ];

        var sectionLength = section.Count - 3 + 4;
        section[1] = (byte)(0xB0 | ((sectionLength >> 8) & 0x0F));
        section[2] = (byte)(sectionLength & 0xFF);

        return SectionPacket(PmtPid, PsiSection.WithCrc(section));
    }

    private static byte[] SectionPacket(int pid, byte[] section)
    {
        var packet = Packet(pid, startsUnit: true);
        packet[4] = 0x00;
        section.CopyTo(packet, 5);
        return packet;
    }

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

    private static int ReadPid(byte[] data, int offset)
        => ((data[offset + 1] & 0x1F) << 8) | data[offset + 2];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}
