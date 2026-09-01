using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;
using TVHeadEnd.Compatibility.Jellyfin12;
using TVHeadEnd.Core.Media;
using TVHeadEnd.Recordings;
using TVHeadEnd.Tests.Core;
using Xunit;

namespace TVHeadEnd.Tests.Recordings;

/// <summary>
/// Which audio tracks of a recording Jellyfin may choose a default from.
/// </summary>
/// <remarks>
/// <para>
/// A recording made with TVHeadend's <c>pass</c> profile is the broadcast itself, so its audio has
/// the same broadcast semantics a live channel's does. FFprobe cannot see that: it reads a file
/// and knows nothing of DVB descriptors, so every track came back with IsDefault false.
/// </para>
/// <para>
/// That is not a smaller answer than the truth. Jellyfin narrows its audio candidates to the
/// tracks marked default whenever the viewer prefers default tracks -- the setting a new account
/// is created with -- and when that narrowing yields nothing it skips the codec check rather than
/// failing it. Direct play is then granted and labelled with whichever track came first, MP2
/// included, to a client whose profile has no MP2 in it. Measured on Jellyfin for Android 2.7.1:
/// the server served the file, the client stopped after 863 ms, and no error was logged anywhere.
/// </para>
/// </remarks>
public class RecordedAudioDefaultTests
{
    private const byte Mpeg1Audio = 0x03;
    private const byte PrivateData = 0x06;
    private const byte H264Video = 0x1B;

    [Fact]
    public void ABroadcastWithMp2AndAc3OffersBoth()
    {
        // The case that broke. Both are the programme's own sound, so both belong to the set
        // Jellyfin picks from -- and it is Jellyfin, against the device profile, that then picks
        // the one the client can decode. The plugin states no preference between them.
        var streams = Streams(("h264", MediaStreamType.Video), ("mp2", MediaStreamType.Audio), ("ac3", MediaStreamType.Audio));

        Assert.True(BroadcastAudioFacts.Apply(streams, Map(
            (H264Video, 256, []),
            (Mpeg1Audio, 257, Language("deu")),
            (PrivateData, 258, Concat(Ac3(), Language("deu"))))));

        Assert.True(streams[1].IsDefault);
        Assert.True(streams[2].IsDefault);

        // Which is the whole point: the candidate set is not empty, so the codec check happens.
        Assert.Contains(streams, stream => stream.Type == MediaStreamType.Audio && stream.IsDefault);
    }

    [Fact]
    public void TheOrderAndTheIndicesAreLeftExactlyAsTheyWere()
    {
        // Jellyfin addresses streams by position, and an "-map" argument that lands on the wrong
        // track is worse than no description at all.
        var streams = Streams(("h264", MediaStreamType.Video), ("mp2", MediaStreamType.Audio), ("ac3", MediaStreamType.Audio));
        var before = streams.Select(stream => (stream.Index, stream.Type, stream.Codec)).ToList();

        BroadcastAudioFacts.Apply(streams, Map(
            (H264Video, 256, []),
            (Mpeg1Audio, 257, []),
            (PrivateData, 258, Ac3())));

        Assert.Equal(before, streams.Select(stream => (stream.Index, stream.Type, stream.Codec)).ToList());
    }

    [Fact]
    public void AnAdditionToTheProgrammeIsNotADefault()
    {
        // An audio description is an addition to the programme sound, and the tables say so.
        // Editorial classification 0x01 is "audio description for the visually impaired".
        var streams = Streams(("h264", MediaStreamType.Video), ("mp2", MediaStreamType.Audio), ("mp2", MediaStreamType.Audio));

        Assert.True(BroadcastAudioFacts.Apply(streams, Map(
            (H264Video, 256, []),
            (Mpeg1Audio, 257, Language("deu")),
            (Mpeg1Audio, 258, Concat(Language("deu"), SupplementaryAudio(0x01))))));

        Assert.True(streams[1].IsDefault);
        Assert.False(streams[2].IsDefault);
    }

    [Fact]
    public void ATrackTheTablesSayNothingAboutStaysInTheDefaultSet()
    {
        // DVB leaves the field undefined far more often than it fills it in, and reading silence
        // as "supplementary" is exactly what empties the candidate set. Same rule as live TV.
        var streams = Streams(("h264", MediaStreamType.Video), ("mp2", MediaStreamType.Audio));

        Assert.True(BroadcastAudioFacts.Apply(streams, Map(
            (H264Video, 256, []),
            (Mpeg1Audio, 257, []))));

        Assert.True(streams[1].IsDefault);
    }

    [Fact]
    public void SeveralLanguagesAreAllOffered()
    {
        // No language preference and no codec preference here. Every track the broadcast calls its
        // own sound is a candidate; choosing between them is Jellyfin's job and the viewer's.
        var streams = Streams(
            ("h264", MediaStreamType.Video),
            ("mp2", MediaStreamType.Audio),
            ("mp2", MediaStreamType.Audio),
            ("ac3", MediaStreamType.Audio));

        Assert.True(BroadcastAudioFacts.Apply(streams, Map(
            (H264Video, 256, []),
            (Mpeg1Audio, 257, Language("deu")),
            (Mpeg1Audio, 258, Language("eng")),
            (PrivateData, 259, Concat(Ac3(), Language("deu"))))));

        Assert.All(streams.Where(stream => stream.Type == MediaStreamType.Audio), stream => Assert.True(stream.IsDefault));
    }

    [Fact]
    public void ARecordingWithNoProgramMapIsLeftAsTheProbeFoundIt()
    {
        // Not MPEG-TS, or a sample whose opening carried no complete pair of tables. Nothing is
        // known about the broadcast, so nothing is claimed about it.
        var streams = Streams(("h264", MediaStreamType.Video), ("aac", MediaStreamType.Audio));

        Assert.False(BroadcastAudioFacts.Apply(streams, programMap: null));
        Assert.False(streams[1].IsDefault);
    }

    [Fact]
    public void AMapThatDisagreesAboutHowManyTracksThereAreIsNotUsed()
    {
        // Matching is by order, so a map describing a different number of audio tracks than the
        // probe found would attach one track's descriptors to another. Silence is better.
        var streams = Streams(("h264", MediaStreamType.Video), ("mp2", MediaStreamType.Audio));

        Assert.False(BroadcastAudioFacts.Apply(streams, Map(
            (H264Video, 256, []),
            (Mpeg1Audio, 257, []),
            (Mpeg1Audio, 258, []))));

        Assert.False(streams[1].IsDefault);
    }

    [Fact]
    public void ARecordingWithNoAudioAtAllChangesNothing()
    {
        var streams = Streams(("h264", MediaStreamType.Video));

        Assert.False(BroadcastAudioFacts.Apply(streams, Map((H264Video, 256, []))));
    }

    [Fact]
    public void LiveTvAndRecordingsAnswerTheQuestionWithOneFunction()
    {
        // The rule itself, which both paths read. Stated here so that changing it in one place
        // cannot quietly mean two different things in the two routes the same broadcast takes.
        Assert.True(AudioPurpose.Main.BelongsInTheDefaultSet());
        Assert.True(AudioPurpose.Unknown.BelongsInTheDefaultSet());
        Assert.False(AudioPurpose.Supplementary.BelongsInTheDefaultSet());
    }

    [Fact]
    public void AProgramMapIsReadOutOfAnActualRecordedTransportStream()
    {
        // End to end from bytes: a PAT naming the map, then the map, packetised as a recording
        // holds them. This is what the describer reads from the analysis sample.
        var pmtSection = BuildPmtSection((H264Video, 256, []), (Mpeg1Audio, 257, Language("deu")));
        var bytes = Concat(
            Packet(0x0000, BuildPatSection(programMapPid: 0x0100)),
            Packet(0x0100, pmtSection));

        using var stream = new System.IO.MemoryStream(bytes);
        var map = RecordedProgramMap.ReadFrom(stream);

        Assert.NotNull(map);
        Assert.Equal(2, map!.Entries.Count);
        Assert.Equal(ElementaryStreamKind.Audio, map.Entries[1].Kind);
        Assert.Equal("deu", map.Entries[1].Language);
    }

    [Fact]
    public void SomethingThatIsNotATransportStreamYieldsNoMap()
    {
        using var stream = new System.IO.MemoryStream(new byte[4096]);

        Assert.Null(RecordedProgramMap.ReadFrom(stream));
    }

    private static List<MediaStream> Streams(params (string Codec, MediaStreamType Type)[] streams)
    {
        var result = new List<MediaStream>(streams.Length);
        for (var index = 0; index < streams.Length; index++)
        {
            result.Add(new MediaStream
            {
                Index = index,
                Codec = streams[index].Codec,
                Type = streams[index].Type,
                IsDefault = false,
            });
        }

        return result;
    }

    private static ProgramMapTable Map(params (byte StreamType, int Pid, byte[] Descriptors)[] entries)
        => ProgramMapTable.Parse(BuildPmtSection(entries))!;

    private static byte[] Language(string code)
        => Descriptor(0x0A, (byte)code[0], (byte)code[1], (byte)code[2], 0);

    /// <summary>
    /// The AC-3 descriptor, which is how DVB says a private-data stream is AC-3.
    /// </summary>
    private static byte[] Ac3() => Descriptor(0x6A);

    /// <summary>
    /// A DVB supplementary audio descriptor: an extension descriptor whose body names it, then
    /// mix_type, editorial_classification, a reserved bit and language_code_present.
    /// </summary>
    private static byte[] SupplementaryAudio(byte editorialClassification)
        => Descriptor(0x7F, 0x06, (byte)(editorialClassification << 2));

    private static byte[] Descriptor(byte tag, params byte[] body)
        => [tag, (byte)body.Length, .. body];

    private static byte[] Concat(params byte[][] parts)
        => parts.SelectMany(part => part).ToArray();

    private static byte[] BuildPatSection(int programMapPid)
    {
        var section = new List<byte>
        {
            0x00, // table_id
            0, 0, // section_length, filled in below
            0x00, 0x01, // transport_stream_id
            0xC1,
            0x00, 0x00,
            0x00, 0x01, // program_number 1
            (byte)(0xE0 | ((programMapPid >> 8) & 0x1F)), (byte)(programMapPid & 0xFF),
        };

        var sectionLength = section.Count - 3 + 4;
        section[1] = (byte)(0xB0 | ((sectionLength >> 8) & 0x0F));
        section[2] = (byte)(sectionLength & 0xFF);

        return Tests.Core.PsiSectionBytes.WithCrc(section);
    }

    private static byte[] BuildPmtSection(params (byte StreamType, int Pid, byte[] Descriptors)[] entries)
    {
        const int PcrPid = 256;

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
            0xC1,
            0x00, 0x00,
            (byte)(0xE0 | ((PcrPid >> 8) & 0x1F)), (byte)(PcrPid & 0xFF),
            0xF0, 0x00, // program_info_length
        };

        section.AddRange(body);

        var sectionLength = section.Count - 3 + 4;
        section[1] = (byte)(0xB0 | ((sectionLength >> 8) & 0x0F));
        section[2] = (byte)(sectionLength & 0xFF);

        return Tests.Core.PsiSectionBytes.WithCrc(section);
    }

    /// <summary>
    /// One transport stream packet carrying a whole section, the way a recording holds it.
    /// </summary>
    private static byte[] Packet(int pid, byte[] section)
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | ((pid >> 8) & 0x1F)); // payload_unit_start_indicator
        packet[2] = (byte)(pid & 0xFF);
        packet[3] = 0x10; // payload only, continuity 0
        packet[4] = 0x00; // pointer_field

        section.CopyTo(packet, 5);
        for (var i = 5 + section.Length; i < packet.Length; i++)
        {
            packet[i] = 0xFF;
        }

        return packet;
    }
}
