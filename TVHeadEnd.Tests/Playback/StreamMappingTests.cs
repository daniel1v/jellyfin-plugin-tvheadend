using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Playback;

/// <summary>
/// Turning the program map of the delivered transport stream into what Jellyfin plays with.
/// </summary>
/// <remarks>
/// The only description the live path produces. Its indices are what every later <c>-map</c>
/// argument means, so the order has to be the table's own order and nothing may be dropped from
/// the middle of it.
/// </remarks>
public class StreamMappingTests
{
    private const int VideoPid = 511;
    private const int GermanAudioPid = 512;
    private const int EnglishAudioPid = 513;
    private const int SubtitlePid = 514;
    private const int TeletextPid = 515;

    [Fact]
    public void ATypicalDvbH264ChannelIsDescribedInTableOrder()
    {
        // H.264 video, two MPEG audio tracks, DVB subtitles: the shape of most DVB-S/T channels.
        var map = Pmt(
            Entry(0x1B, VideoPid),
            Entry(0x03, GermanAudioPid, Language("deu")),
            Entry(0x03, EnglishAudioPid, Language("eng")),
            Entry(0x06, SubtitlePid, Subtitling("deu")));

        var description = LiveStreamDescription.FromProgramMap(map, ChannelType.TV);

        Assert.NotNull(description);
        Assert.Equal([0, 1, 2, 3], description.Streams.Select(stream => stream.Index));
        Assert.Equal(
            [MediaStreamType.Video, MediaStreamType.Audio, MediaStreamType.Audio, MediaStreamType.Subtitle],
            description.Streams.Select(stream => stream.Type));
        // Named "mp2": that is what DVB mandates for these stream types and what FFmpeg reports
        // for them. An unnamed codec reads to Jellyfin as one no profile can match.
        Assert.Equal(["h264", "mp2", "mp2", "dvb_subtitle"], description.Streams.Select(stream => stream.Codec));
        Assert.Equal([null, "deu", "eng", "deu"], description.Streams.Select(stream => stream.Language));
    }

    [Fact]
    public void AnMpeg2ChannelWithAc3IsDescribedFromItsDescriptors()
    {
        // MPEG-2 video and an AC-3 track carried as private data, which is what the descriptor
        // has to resolve: stream type 0x06 says nothing on its own.
        var map = Pmt(
            Entry(0x02, VideoPid),
            Entry(0x03, GermanAudioPid, Language("deu")),
            Entry(0x06, EnglishAudioPid, Concat(Descriptor(0x6A), Language("deu"))));

        var description = LiveStreamDescription.FromProgramMap(map, ChannelType.TV)!;

        Assert.Equal(["mpeg2video", "mp2", "ac3"], description.Streams.Select(stream => stream.Codec));
        Assert.Equal(MediaStreamType.Audio, description.Streams[2].Type);
        Assert.Equal("deu", description.Streams[2].Language);
    }

    [Fact]
    public void TeletextAndSubtitlesAreToldApartByTheirDescriptors()
    {
        // Both arrive as stream type 0x06 and differ only in the descriptor that follows.
        var map = Pmt(
            Entry(0x1B, VideoPid),
            Entry(0x06, TeletextPid, Concat(Descriptor(0x56, (byte)'d', (byte)'e', (byte)'u'))),
            Entry(0x06, SubtitlePid, Subtitling("deu")));

        var description = LiveStreamDescription.FromProgramMap(map, ChannelType.TV)!;

        Assert.Equal(MediaStreamType.Subtitle, description.Streams[1].Type);
        Assert.Equal("dvb_teletext", description.Streams[1].Codec);
        Assert.Equal(MediaStreamType.Subtitle, description.Streams[2].Type);
        Assert.Equal("dvb_subtitle", description.Streams[2].Codec);
    }

    [Fact]
    public void AHearingImpairedTrackIsFlaggedFromTheDescriptorThatSaysSo()
    {
        var map = Pmt(
            Entry(0x1B, VideoPid),
            Entry(0x03, GermanAudioPid, Descriptor(0x0A, (byte)'d', (byte)'e', (byte)'u', 2)),
            Entry(0x06, SubtitlePid, Descriptor(0x59, (byte)'d', (byte)'e', (byte)'u', 0x20, 0, 0, 0, 0)));

        var description = LiveStreamDescription.FromProgramMap(map, ChannelType.TV)!;

        Assert.True(description.Streams[1].IsHearingImpaired);
        Assert.True(description.Streams[2].IsHearingImpaired);
    }

    [Fact]
    public void AnEntryThatCannotBeClassifiedStillOccupiesItsIndex()
    {
        // Dropping it would shift every index after it, which is the same failure as counting the
        // EIT. A private stream with no descriptor naming it is data, and says so.
        var map = Pmt(
            Entry(0x1B, VideoPid),
            Entry(0x06, 600),
            Entry(0x03, GermanAudioPid, Language("deu")));

        var description = LiveStreamDescription.FromProgramMap(map, ChannelType.TV)!;

        Assert.Equal(3, description.Streams.Count);
        Assert.Equal([0, 1, 2], description.Streams.Select(stream => stream.Index));
        Assert.Equal(MediaStreamType.Data, description.Streams[1].Type);
        Assert.Null(description.Streams[1].Codec);
        Assert.Equal(MediaStreamType.Audio, description.Streams[2].Type);
        Assert.Equal(2, description.Streams[2].Index);
    }

    [Fact]
    public void ATelevisionChannelWhoseTableNamesNoVideoIsNotDescribedAtAll()
    {
        // Fail fast. There is nothing to fall back to and no reason to want one: a probe would
        // read the same stream to reach the same table. Refusing here turns this into one
        // immediate error instead of a client waiting on a source with no picture in it.
        var map = Pmt(Entry(0x03, GermanAudioPid, Language("deu")));

        Assert.Null(LiveStreamDescription.FromProgramMap(map, ChannelType.TV));
    }

    [Fact]
    public void ARadioChannelIsDescribedFromItsAudioAlone()
    {
        // The very table that is incomplete for television is a complete radio service. Only the
        // channel list can tell the two apart, which is why the kind is passed into the open path
        // rather than guessed from the transport stream.
        var map = Pmt(Entry(0x03, GermanAudioPid, Language("deu")));

        var description = LiveStreamDescription.FromProgramMap(map, ChannelType.Radio);

        Assert.NotNull(description);
        Assert.Equal(MediaStreamType.Audio, description!.Streams[0].Type);
        Assert.Equal(0, description.Streams[0].Index);
    }

    [Fact]
    public void ARadioChannelWhoseTableNamesNoAudioIsNotDescribedAtAll()
    {
        var map = Pmt(Entry(0x06, 600));

        Assert.Null(LiveStreamDescription.FromProgramMap(map, ChannelType.Radio));
    }

    [Fact]
    public void UnstatedVideoPropertiesAreLeftUnsetRatherThanEstablished()
    {
        // Optional metadata this plugin does not establish -- resolution, frame rate, profile --
        // is absent by design. Absent is not the same as unclear: it must not read as a reason to
        // inspect the stream. Jellyfin treats an unset optional value as unknown and carries on.
        var map = Pmt(Entry(0x1B, VideoPid), Entry(0x03, GermanAudioPid, Language("deu")));

        var description = LiveStreamDescription.FromProgramMap(map, ChannelType.TV)!;
        var video = description.Streams[0];

        Assert.Null(video.Width);
        Assert.Null(video.Height);
        Assert.Null(video.RealFrameRate);
        Assert.Null(video.Profile);
        Assert.Null(video.Level);
        Assert.Null(video.BitRate);
    }

    [Fact]
    public void AnEmptyTableDescribesNothing()
    {
        Assert.Null(LiveStreamDescription.FromProgramMap(Pmt(), ChannelType.TV));
    }

    [Fact]
    public void TheProgramMapIsReadFromTheSectionTheBroadcastSent()
    {
        // Parsed rather than constructed: this is the shape that actually arrives, descriptors
        // and all, and the classification has to survive a real section.
        var section = BuildPmtSection(
            (0x1B, VideoPid, System.Array.Empty<byte>()),
            (0x03, GermanAudioPid, Language("deu")),
            (0x06, SubtitlePid, Subtitling("deu")));

        var map = ProgramMapTable.Parse(section);

        Assert.NotNull(map);
        Assert.Equal(3, map!.Entries.Count);
        Assert.Equal(VideoPid, map.VideoPid);
        Assert.Equal(ElementaryStreamKind.Audio, map.Entries[1].Kind);
        Assert.Equal("deu", map.Entries[1].Language);
        Assert.Equal(ElementaryStreamKind.Subtitle, map.Entries[2].Kind);
        Assert.Equal("dvb_subtitle", map.Entries[2].Codec);
    }

    private static ProgramMapTable Pmt(params ProgramMapEntry[] entries)
        => new(1, VideoPid, entries);

    private static ProgramMapEntry Entry(byte streamType, int pid, byte[]? descriptors = null)
    {
        var section = BuildPmtSection((streamType, pid, descriptors ?? []));
        return ProgramMapTable.Parse(section)!.Entries[0];
    }

    private static byte[] Language(string code)
        => Descriptor(0x0A, (byte)code[0], (byte)code[1], (byte)code[2], 0);

    private static byte[] Subtitling(string code)
        => Descriptor(0x59, (byte)code[0], (byte)code[1], (byte)code[2], 0x10, 0, 0, 0, 0);

    private static byte[] Descriptor(byte tag, params byte[] body)
        => [tag, (byte)body.Length, .. body];

    private static byte[] Concat(params byte[][] parts)
        => parts.SelectMany(part => part).ToArray();

    /// <summary>
    /// Builds a PMT section the way a broadcaster would, so the parser is exercised rather than
    /// bypassed.
    /// </summary>
    private static byte[] BuildPmtSection(params (byte StreamType, int Pid, byte[] Descriptors)[] entries)
    {
        var body = new List<byte>();
        foreach (var entry in entries)
        {
            body.Add(entry.StreamType);
            body.Add((byte)(0xE0 | ((entry.Pid >> 8) & 0x1F)));
            body.Add((byte)(entry.Pid & 0xFF));
            body.Add((byte)(0xF0 | ((entry.Descriptors.Length >> 8) & 0x0F)));
            body.Add((byte)(entry.Descriptors.Length & 0xFF));
            body.AddRange(entry.Descriptors);
        }

        var section = new List<byte>
        {
            0x02, // table_id
            0, 0, // section_length, filled in below
            0x00, 0x01, // program_number
            0xC1, // version/current_next
            0x00, 0x00, // section_number, last_section_number
            (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), (byte)(VideoPid & 0xFF), // PCR PID
            0xF0, 0x00, // program_info_length
        };

        section.AddRange(body);

        // The length covers everything after it, including the CRC that is about to be appended.
        var sectionLength = section.Count - 3 + 4;
        section[1] = (byte)(0xB0 | ((sectionLength >> 8) & 0x0F));
        section[2] = (byte)(sectionLength & 0xFF);

        return TVHeadEnd.Tests.Streaming.PsiSection.WithCrc(section);
    }
}
