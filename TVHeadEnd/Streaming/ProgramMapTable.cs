using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TVHeadEnd.Streaming;

/// <summary>
/// The Program Map Table of the stream actually being delivered.
/// </summary>
/// <remarks>
/// <para>
/// The only description of a live stream this plugin keeps. TVHeadend forwards the broadcast
/// untouched, so the table that arrives is the broadcaster's own account of what is in it --
/// and it is the same table libavformat reads, entry by entry, to decide how many streams the
/// file has and in what order. Reading it here is therefore not a second opinion about the
/// stream; it is the same source FFmpeg will use, read earlier.
/// </para>
/// <para>
/// Only what the table states is taken from it. Frame size, frame rate, bit rate and codec
/// profile are not in a PMT, are not needed for the playback decision, and are not guessed at.
/// </para>
/// </remarks>
/// <param name="ProgramNumber">The program this table describes.</param>
/// <param name="PcrPid">The PID carrying the program clock reference.</param>
/// <param name="Entries">The elementary streams, in the order the table lists them.</param>
public sealed record ProgramMapTable(int ProgramNumber, int PcrPid, IReadOnlyList<ProgramMapEntry> Entries)
{
    private const byte TableIdProgramMap = 0x02;

    private const byte DescriptorRegistration = 0x05;
    private const byte DescriptorLanguage = 0x0A;
    private const byte DescriptorTeletext = 0x56;
    private const byte DescriptorSubtitling = 0x59;
    private const byte DescriptorAc3 = 0x6A;
    private const byte DescriptorEnhancedAc3 = 0x7A;
    private const byte DescriptorDts = 0x7B;
    private const byte DescriptorAac = 0x7C;

    /// <summary>
    /// Gets the PID of the first video stream, or -1 when the program carries none.
    /// </summary>
    public int VideoPid => Entries.FirstOrDefault(entry => entry.IsVideo)?.Pid ?? -1;

    /// <summary>
    /// Gets the stream type of the first video stream, or zero when the program carries none.
    /// </summary>
    public byte VideoStreamType => Entries.FirstOrDefault(entry => entry.IsVideo)?.StreamType ?? 0;

    /// <summary>
    /// Parses a complete PMT section.
    /// </summary>
    /// <param name="section">The reassembled section.</param>
    /// <returns>The table, or <see langword="null"/> when the section is not a usable PMT.</returns>
    public static ProgramMapTable? Parse(ReadOnlySpan<byte> section)
    {
        if (section.Length < 13 || section[0] != TableIdProgramMap)
        {
            return null;
        }

        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];

        // The declared length has to fit what was collected, and the last four bytes are the CRC
        // rather than table content.
        var end = 3 + sectionLength - 4;
        if (end > section.Length || end < 12)
        {
            return null;
        }

        var programNumber = (section[3] << 8) | section[4];
        var pcrPid = ((section[8] & 0x1F) << 8) | section[9];
        var programInfoLength = ((section[10] & 0x0F) << 8) | section[11];

        var offset = 12 + programInfoLength;
        if (offset > end)
        {
            return null;
        }

        var entries = new List<ProgramMapEntry>();
        while (offset + 5 <= end)
        {
            var streamType = section[offset];
            var pid = ((section[offset + 1] & 0x1F) << 8) | section[offset + 2];
            var infoLength = ((section[offset + 3] & 0x0F) << 8) | section[offset + 4];

            var descriptorsStart = offset + 5;
            var descriptorsEnd = Math.Min(descriptorsStart + infoLength, end);
            var descriptors = descriptorsStart <= descriptorsEnd
                ? section[descriptorsStart..descriptorsEnd]
                : default;

            entries.Add(Describe(streamType, pid, descriptors));
            offset = descriptorsStart + infoLength;
        }

        return new ProgramMapTable(programNumber, pcrPid, entries);
    }

    /// <summary>
    /// Gets the PIDs the table announces.
    /// </summary>
    /// <returns>The PIDs.</returns>
    public IReadOnlySet<int> GetPids() => Entries.Select(entry => entry.Pid).ToHashSet();

    /// <summary>
    /// Renders the layout for a log line.
    /// </summary>
    /// <returns>A short description.</returns>
    public string Describe()
        => string.Join(
            ", ",
            Entries.Select((entry, index) => string.Create(
                CultureInfo.InvariantCulture,
                $"{index}:{entry.Kind.ToString().ToLowerInvariant()}/{entry.Codec ?? "?"}/pid={entry.Pid}")));

    private static ProgramMapEntry Describe(byte streamType, int pid, ReadOnlySpan<byte> descriptors)
    {
        string? language = null;
        var hearingImpaired = false;

        // Set only by a descriptor, and only for stream types that need one to be identified.
        string? descriptorCodec = null;
        ElementaryStreamKind? descriptorKind = null;

        var offset = 0;
        while (offset + 2 <= descriptors.Length)
        {
            var tag = descriptors[offset];
            var length = descriptors[offset + 1];
            var bodyStart = offset + 2;
            if (bodyStart + length > descriptors.Length)
            {
                break;
            }

            var body = descriptors.Slice(bodyStart, length);
            switch (tag)
            {
                case DescriptorLanguage when body.Length >= 4:
                    language ??= ReadLanguage(body[..3]);

                    // ISO 13818-1 audio_type: 2 is "hearing impaired", 3 is a visual
                    // impairment commentary. Only the first is a Jellyfin flag.
                    hearingImpaired |= body[3] == 2;
                    break;

                case DescriptorSubtitling when body.Length >= 4:
                    language ??= ReadLanguage(body[..3]);

                    // subtitling_type 0x20..0x25 are the "for the hard of hearing" variants.
                    hearingImpaired |= body[3] is >= 0x20 and <= 0x25;
                    descriptorKind = ElementaryStreamKind.Subtitle;
                    descriptorCodec = "dvb_subtitle";
                    break;

                case DescriptorTeletext:
                    if (body.Length >= 3)
                    {
                        language ??= ReadLanguage(body[..3]);
                    }

                    descriptorKind = ElementaryStreamKind.Subtitle;
                    descriptorCodec = "dvb_teletext";
                    break;

                case DescriptorAc3:
                    descriptorKind = ElementaryStreamKind.Audio;
                    descriptorCodec = "ac3";
                    break;

                case DescriptorEnhancedAc3:
                    descriptorKind = ElementaryStreamKind.Audio;
                    descriptorCodec = "eac3";
                    break;

                case DescriptorDts:
                    descriptorKind = ElementaryStreamKind.Audio;
                    descriptorCodec = "dts";
                    break;

                case DescriptorAac:
                    descriptorKind = ElementaryStreamKind.Audio;
                    descriptorCodec = "aac";
                    break;

                case DescriptorRegistration when body.Length >= 4:
                    (descriptorKind, descriptorCodec) = ReadRegistration(body[..4]) switch
                    {
                        "AC-3" => (ElementaryStreamKind.Audio, "ac3"),
                        "EAC3" => (ElementaryStreamKind.Audio, "eac3"),
                        "DTS1" or "DTS2" or "DTS3" => (ElementaryStreamKind.Audio, "dts"),
                        "HEVC" => (ElementaryStreamKind.Video, "hevc"),
                        _ => (descriptorKind, descriptorCodec),
                    };
                    break;

                default:
                    break;
            }

            offset = bodyStart + length;
        }

        // The stream type decides wherever it is unambiguous; a descriptor only has the last
        // word for the private types, which state nothing on their own.
        var (kind, codec) = FromStreamType(streamType);
        if (kind == ElementaryStreamKind.Data && descriptorKind is { } resolvedKind)
        {
            kind = resolvedKind;
            codec = descriptorCodec;
        }
        else if (codec is null && descriptorCodec is not null)
        {
            codec = descriptorCodec;
        }

        return new ProgramMapEntry
        {
            StreamType = streamType,
            Pid = pid,
            Kind = kind,
            Codec = codec,
            Language = language,
            IsHearingImpaired = hearingImpaired,
        };
    }

    private static (ElementaryStreamKind Kind, string? Codec) FromStreamType(byte streamType) => streamType switch
    {
        0x01 or 0x02 => (ElementaryStreamKind.Video, "mpeg2video"),
        0x10 => (ElementaryStreamKind.Video, "mpeg4"),
        0x1B => (ElementaryStreamKind.Video, "h264"),
        0x24 => (ElementaryStreamKind.Video, "hevc"),
        0x03 or 0x04 => (ElementaryStreamKind.Audio, "mp2"),
        0x0F => (ElementaryStreamKind.Audio, "aac"),
        0x11 => (ElementaryStreamKind.Audio, "aac_latm"),
        0x81 => (ElementaryStreamKind.Audio, "ac3"),
        0x87 => (ElementaryStreamKind.Audio, "eac3"),

        // 0x06 is private data in PES packets, which is where DVB puts AC-3, E-AC-3, subtitles
        // and teletext. It says nothing by itself; the descriptors decide.
        _ => (ElementaryStreamKind.Data, null),
    };

    private static string? ReadLanguage(ReadOnlySpan<byte> code)
    {
        foreach (var character in code)
        {
            if (character is < 0x20 or > 0x7E)
            {
                return null;
            }
        }

        var language = Encoding.ASCII.GetString(code).Trim();
        return language.Length == 3 ? language.ToLowerInvariant() : null;
    }

    private static string ReadRegistration(ReadOnlySpan<byte> identifier)
        => Encoding.ASCII.GetString(identifier).Trim();
}
