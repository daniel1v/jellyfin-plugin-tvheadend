using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;
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

        var description = LiveStreamDescription.FromProgramMap(map);

        Assert.NotNull(description);
        Assert.True(description!.IsUsable);
        Assert.Equal([0, 1, 2, 3], description.Streams.Select(stream => stream.Index));
        Assert.Equal(
            [MediaStreamType.Video, MediaStreamType.Audio, MediaStreamType.Audio, MediaStreamType.Subtitle],
            description.Streams.Select(stream => stream.Type));
        // The MPEG audio tracks carry no codec: stream type 0x03 does not say which layer, and
        // the plugin does not guess. Everything the table does state is stated.
        Assert.Equal(["h264", null, null, "dvb_subtitle"], description.Streams.Select(stream => stream.Codec));
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

        var description = LiveStreamDescription.FromProgramMap(map)!;

        Assert.Equal(["mpeg2video", null, "ac3"], description.Streams.Select(stream => stream.Codec));
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

        var description = LiveStreamDescription.FromProgramMap(map)!;

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

        var description = LiveStreamDescription.FromProgramMap(map)!;

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

        var description = LiveStreamDescription.FromProgramMap(map)!;

        Assert.Equal(3, description.Streams.Count);
        Assert.Equal([0, 1, 2], description.Streams.Select(stream => stream.Index));
        Assert.Equal(MediaStreamType.Data, description.Streams[1].Type);
        Assert.Null(description.Streams[1].Codec);
        Assert.Equal(MediaStreamType.Audio, description.Streams[2].Type);
        Assert.Equal(2, description.Streams[2].Index);
    }

    [Fact]
    public void ADescriptionWithoutVideoIsNotOfferedAsComplete()
    {
        // Jellyfin dereferences the video stream while preparing playback. A table this plugin
        // could not find video in has not been understood, and saying so is what lets Jellyfin
        // inspect the stream instead.
        var map = Pmt(Entry(0x03, GermanAudioPid, Language("deu")));

        var description = LiveStreamDescription.FromProgramMap(map)!;

        Assert.False(description.IsUsable);
    }

    [Fact]
    public void AnUnknownVideoCodecDoesNotByItselfMakeTheDescriptionUnusable()
    {
        // Optional metadata this plugin does not establish -- resolution, frame rate, profile --
        // is absent by design. Absent is not the same as wrong, and must not trigger a fallback
        // of its own; Jellyfin treats an unset optional value as unknown and carries on.
        var map = Pmt(Entry(0x1B, VideoPid), Entry(0x03, GermanAudioPid, Language("deu")));

        var description = LiveStreamDescription.FromProgramMap(map)!;
        var video = description.Streams[0];

        Assert.True(description.IsUsable);
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
        Assert.Null(LiveStreamDescription.FromProgramMap(Pmt()));
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
