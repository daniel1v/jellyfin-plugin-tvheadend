using System;
using System.Collections.Generic;
using System.Linq;
using TVHeadEnd.Core.Media;
using Xunit;

namespace TVHeadEnd.Tests.Core;

/// <summary>
/// Reading the program tables of the stream that is arriving, which is the only description a live
/// channel gets.
/// </summary>
/// <remarks>
/// There is no probe behind this and no second opinion. A section that is damaged, announced for
/// later, or truncated has to be refused rather than half-believed, because whatever this decides
/// is what Jellyfin is told the channel contains.
/// </remarks>
public class ProgramSpecificInformationTests
{
    private const int PacketLength = 188;
    private const int PmtPid = 0x13ec;
    private const int MovedPmtPid = 0x13f0;
    private const int VideoPid = 0x13ed;
    private const int AudioPid = 0x13ee;

    [Fact]
    public void AProgramAssociationTableWithABrokenCrcChangesNothing()
    {
        // The transmitted bytes and the check over them disagree, so one of them is wrong and
        // there is no way to tell which. Acting on it would point the reader at whatever PID the
        // damage happened to spell.
        var conditioner = Started();

        Condition(conditioner, Corrupt(Pat(PmtPid)), out _);

        Assert.Equal(VideoPid, conditioner.ProgramMap!.VideoPid);
        Assert.False(conditioner.ProgramLayoutChanged);
        Assert.Equal(2, conditioner.ProgramMap.Entries.Count);
    }

    [Fact]
    public void AProgramAssociationTableAnnouncedForLaterChangesNothing()
    {
        // current_next_indicator clear: the broadcaster is describing a layout that is not on air
        // yet. Following it early points the reader at a program map that is not being sent.
        var conditioner = Started();

        Condition(conditioner, Pat(MovedPmtPid, currentNext: false), out _);
        Condition(conditioner, Pmt(0x1B, pid: MovedPmtPid), out _);

        Assert.Equal(VideoPid, conditioner.ProgramMap!.VideoPid);
        Assert.False(conditioner.ProgramLayoutChanged);
    }

    [Fact]
    public void MovingTheProgramMapNeverLeavesTheNewTableBesideTheOldOne()
    {
        // A pairing that never existed on air: the new PAT names a program map at another PID,
        // and until that map arrives the only one in hand describes a program the PAT no longer
        // points at. Nothing is published in between.
        var conditioner = Started();

        Condition(conditioner, Pat(MovedPmtPid), out _);

        Assert.Null(conditioner.ProgramMap);
        Assert.False(conditioner.HasProgramTables);
        Assert.True(conditioner.ProgramLayoutChanged);

        // The map at the new PID completes the pair, and only then is there a description again.
        Condition(conditioner, Pmt(0x1B, pid: MovedPmtPid), out _);

        Assert.NotNull(conditioner.ProgramMap);
        Assert.True(conditioner.HasProgramTables);
    }

    [Fact]
    public void ASectionSplitAcrossTheEndOfAPacketIsFinishedBeforeTheNextOneStarts()
    {
        // The pointer field says how much of the previous section comes first. Skipping to it --
        // the obvious reading -- throws away the end of a table, which for a program map split
        // across two packets is most of it.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
        var section = PsiSectionBytes.WithCrc(ProgramMapSection(0x1B));
        var split = section.Length - 6;

        Condition(conditioner, Pat(PmtPid), out _);
        Condition(conditioner, SectionStart(PmtPid, section[..split]), out _);

        Assert.Null(conditioner.ProgramMap);

        // A packet that carries the tail of one section and the beginning of the next.
        Condition(conditioner, TailThenStart(PmtPid, section[split..], PsiSectionBytes.WithCrc(ProgramMapSection(0x1B))), out _);

        Assert.NotNull(conditioner.ProgramMap);
        Assert.Equal(VideoPid, conditioner.ProgramMap!.VideoPid);
        Assert.Equal(2, conditioner.ProgramMap.Entries.Count);
    }

    [Fact]
    public void TheStoredPacketsAreTheOnesTheParsedSectionArrivedIn()
    {
        // The two used to be counted separately: the parser followed the pointer field, and the
        // packet capture cleared itself at every payload unit start. For a section whose tail
        // shares a packet with the start of the next, that kept the packet beginning the next
        // section and dropped every packet the finished one came in -- so a joining reader was
        // handed bytes that did not contain the table the plugin had just parsed.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
        var section = PsiSectionBytes.WithCrc(ProgramMapSection(0x1B));
        var split = section.Length - 6;

        Condition(conditioner, Pat(PmtPid), out _);
        Condition(conditioner, SectionStart(PmtPid, section[..split]), out _);
        Condition(conditioner, TailThenStart(PmtPid, section[split..], PsiSectionBytes.WithCrc(ProgramMapSection(0x1B))), out _);

        Assert.NotNull(conditioner.ProgramMap);

        // Whatever the conditioner hands a reader has to parse back to the same table.
        var stored = conditioner.TakeProgramTables();
        var rebuilt = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
        foreach (var packet in stored.ProgramAssociationPackets.Concat(stored.ProgramMapPackets))
        {
            Condition(rebuilt, packet, out _);
        }

        Assert.NotNull(rebuilt.ProgramMap);
        Assert.Equal(conditioner.ProgramMap!.Describe(), rebuilt.ProgramMap!.Describe());
    }

    [Fact]
    public void TwoWholeSectionsInOnePacketAreBothRead()
    {
        // A PSI packet may carry more than one complete section. Reading only as far as the first
        // -- which is what taking the section length once and stopping amounts to -- silently
        // drops the rest, and for a program map that is the description of the channel.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
        Condition(conditioner, Pat(PmtPid), out _);

        // The first names one audio track, the second names it with its language. Both are
        // complete and both are in one packet; the second is the one in force.
        var first = PsiSectionBytes.WithCrc(ProgramMapSection(0x1B));
        var second = PsiSectionBytes.WithCrc(GermanAudioProgramMapSection());

        Condition(conditioner, TwoSections(PmtPid, first, second), out _);

        Assert.NotNull(conditioner.ProgramMap);
        Assert.Equal("deu", conditioner.ProgramMap!.Entries[1].Language);

        // And what is stored for a joining reader is the section that was acted on.
        var stored = conditioner.TakeProgramTables();
        var rebuilt = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
        foreach (var packet in stored.ProgramAssociationPackets.Concat(stored.ProgramMapPackets))
        {
            Condition(rebuilt, packet, out _);
        }

        Assert.Equal("deu", rebuilt.ProgramMap!.Entries[1].Language);
    }

    [Theory]
    [InlineData(0x30)]
    [InlineData(0x0A)]
    public void AProgramMapWhoseDescriptorRangeRunsPastTheSectionIsRefusedWhole(byte overrun)
    {
        // Half a table is not a smaller table. Truncating the range silently would publish a
        // description missing a language, a subtitle page, or the descriptor that turns a private
        // stream into AC-3 -- with nothing anywhere saying so.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
        Condition(conditioner, Pat(PmtPid), out _);

        var section = ProgramMapSection(0x1B);

        // The elementary stream loop starts at offset 12, and bytes 15 and 16 carry the first
        // entry's ES_info_length. Nothing follows it that is long enough.
        section[16] = overrun;

        Condition(conditioner, SectionPacket(PmtPid, PsiSectionBytes.WithCrc(section)), out _);

        Assert.Null(conditioner.ProgramMap);
        Assert.False(conditioner.HasProgramTables);
    }

    [Fact]
    public void AProgramMapWhoseDescriptorOverrunsItsOwnRangeIsRefusedWhole()
    {
        // The range fits inside the section and the descriptor does not fit inside the range. The
        // language descriptor claims thirty-two bytes of a six byte range: reading what is there
        // and calling it a language would invent one.
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
        Condition(conditioner, Pat(PmtPid), out _);

        byte[] section =
        [
            0x02,
            0xB0, 0x1D,
            0x00, 0x01,
            0xC1, 0x00, 0x00,
            (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF,
            0xF0, 0x00,
            0x1B, (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF, 0xF0, 0x00,
            0x03, (byte)(0xE0 | ((AudioPid >> 8) & 0x1F)), AudioPid & 0xFF, 0xF0, 0x06,
            0x0A, 0x20, (byte)'d', (byte)'e', (byte)'u', 0x00,
        ];

        Condition(conditioner, SectionPacket(PmtPid, PsiSectionBytes.WithCrc(section)), out _);

        Assert.Null(conditioner.ProgramMap);
    }

    [Fact]
    public void ATrackChangingItsLanguageCountsAsANewProgramLayout()
    {
        // Not just the PID and the stream type. A broadcaster that swaps a track's language has
        // changed what a viewer is told it is playing, and an entry point found under the old
        // table would hand a joining reader the new description and the old programme.
        var conditioner = Started();

        Condition(conditioner, PmtWithGermanAudio(), out _);

        Assert.True(conditioner.ProgramLayoutChanged);
        Assert.Equal("deu", conditioner.ProgramMap!.Entries[1].Language);
    }

    [Fact]
    public void AnUnchangedProgramMapRepeatedIsNotAChange()
    {
        // PSI repeats several times a second. Treating every repetition as a change would throw
        // the join points away continuously and leave every late viewer at the write head.
        var conditioner = Started();

        Condition(conditioner, Pmt(0x1B), out _);
        Condition(conditioner, Pmt(0x1B), out _);

        Assert.False(conditioner.ProgramLayoutChanged);
    }

    [Fact]
    public void TablesAndTheAccessPointsTheyDescribeArePublishedTogether()
    {
        // The race this replaces: publishing the access points first leaves a window where a
        // reader joins at a position described by tables it has not been given, and publishing
        // the tables first leaves the mirror image. Either way it is handed a picture and a map
        // of a different programme.
        var index = new StreamBootstrapIndex();
        var tables = new ProgramTableSnapshot([Pat(PmtPid)], [Pmt(0x1B)], 0);

        index.Publish(tables, basePosition: 0, accessPoints: [new StreamAccessPoint(376, RandomAccessGuarantee.DvbRandomAccess)]);
        var join = index.CreateJoin(0);

        Assert.True(index.HasProgramTables);
        Assert.Equal(376, join.Position);
        Assert.Equal(2 * PacketLength, join.Tables.Length);
    }

    [Fact]
    public void AnAccessPointIsNeverPublishedBeforeTheTablesThatDescribeIt()
    {
        // A chunk whose tables have not both arrived yet contributes no join point either, so
        // there is no moment at which a reader can be sent somewhere it cannot be told about.
        var index = new StreamBootstrapIndex();

        index.Publish(ProgramTableSnapshot.Empty, basePosition: 0, accessPoints: [new StreamAccessPoint(188, RandomAccessGuarantee.DvbRandomAccess)]);

        Assert.False(index.HasProgramTables);
        Assert.Empty(index.CreateJoin(0).Tables);
    }

    private static TransportStreamConditioner Started()
    {
        var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
        Condition(conditioner, Concat(Pat(PmtPid), Pmt(0x1B)), out _);
        return conditioner;
    }

    private static int Condition(TransportStreamConditioner conditioner, byte[] source, out byte[] output)
    {
        var destination = new byte[TransportStreamConditioner.GetMaximumConditionedLength(source.Length)];
        var written = conditioner.Condition(source, destination);
        output = destination.AsSpan(0, written).ToArray();
        return written;
    }

    private static byte[] Corrupt(byte[] packet)
    {
        var damaged = (byte[])packet.Clone();

        // One bit of the transport stream identifier, which the CRC covers.
        damaged[8] ^= 0x01;
        return damaged;
    }

    private static byte[] Pat(int programMapPid, bool currentNext = true)
    {
        byte[] section =
        [
            0x00,
            0xB0, 0x0D,
            0x00, 0x01,
            currentNext ? (byte)0xC1 : (byte)0xC0,
            0x00, 0x00,
            0x00, 0x01,
            (byte)(0xE0 | ((programMapPid >> 8) & 0x1F)), (byte)(programMapPid & 0xFF),
        ];

        return SectionPacket(0x00, PsiSectionBytes.WithCrc(section));
    }

    private static byte[] Pmt(byte videoStreamType, int pid = PmtPid)
        => SectionPacket(pid, PsiSectionBytes.WithCrc(ProgramMapSection(videoStreamType)));

    private static byte[] ProgramMapSection(byte videoStreamType) =>
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

    private static byte[] PmtWithGermanAudio()
    {
        byte[] section =
        [
            0x02,
            0xB0, 0x1D,
            0x00, 0x01,
            0xC1, 0x00, 0x00,
            (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF,
            0xF0, 0x00,
            0x1B, (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF, 0xF0, 0x00,
            0x03, (byte)(0xE0 | ((AudioPid >> 8) & 0x1F)), AudioPid & 0xFF, 0xF0, 0x06,
            0x0A, 0x04, (byte)'d', (byte)'e', (byte)'u', 0x00,
        ];

        return SectionPacket(PmtPid, PsiSectionBytes.WithCrc(section));
    }

    /// <summary>
    /// One packet carrying two complete sections back to back, which is legal and which a
    /// broadcaster does when both are small.
    /// </summary>
    private static byte[] TwoSections(int pid, IReadOnlyList<byte> first, IReadOnlyList<byte> second)
    {
        var packet = Packet(pid, startsUnit: true);
        packet[4] = 0x00;

        var offset = 5;
        foreach (var value in first)
        {
            packet[offset++] = value;
        }

        foreach (var value in second)
        {
            packet[offset++] = value;
        }

        // Stuffing, as a broadcaster fills the rest.
        for (; offset < packet.Length; offset++)
        {
            packet[offset] = 0xFF;
        }

        return packet;
    }

    private static byte[] GermanAudioProgramMapSection() =>
    [
        0x02,
        0xB0, 0x1D,
        0x00, 0x01,
        0xC1, 0x00, 0x00,
        (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF,
        0xF0, 0x00,
        0x1B, (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF, 0xF0, 0x00,
        0x03, (byte)(0xE0 | ((AudioPid >> 8) & 0x1F)), AudioPid & 0xFF, 0xF0, 0x06,
        0x0A, 0x04, (byte)'d', (byte)'e', (byte)'u', 0x00,
    ];

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

    /// <summary>
    /// A packet that begins a section and leaves it unfinished.
    /// </summary>
    private static byte[] SectionStart(int pid, ReadOnlySpan<byte> beginning)
    {
        var packet = Packet(pid, startsUnit: true);
        packet[4] = 0x00;
        beginning.CopyTo(packet.AsSpan(5));
        return packet;
    }

    /// <summary>
    /// A packet carrying the end of the section in progress followed by the start of the next,
    /// with the pointer field saying where the split is.
    /// </summary>
    private static byte[] TailThenStart(int pid, ReadOnlySpan<byte> tail, ReadOnlySpan<byte> next)
    {
        var packet = Packet(pid, startsUnit: true);
        packet[4] = (byte)tail.Length;
        tail.CopyTo(packet.AsSpan(5));
        next[..Math.Min(next.Length, packet.Length - 5 - tail.Length)].CopyTo(packet.AsSpan(5 + tail.Length));
        return packet;
    }

    private static byte[] Packet(int pid, bool startsUnit = false)
    {
        var packet = new byte[PacketLength];
        packet[0] = 0x47;
        packet[1] = (byte)(((pid >> 8) & 0x1F) | (startsUnit ? 0x40 : 0x00));
        packet[2] = (byte)(pid & 0xFF);
        packet[3] = 0x10;
        return packet;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}
