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

    /// <summary>
    /// Everything before the first elementary stream entry: the section header, the program
    /// number, the PCR PID and the program info length.
    /// </summary>
    private const int MinimumBodyLength = 12;

    private const byte DescriptorRegistration = 0x05;
    private const byte DescriptorLanguage = 0x0A;
    private const byte DescriptorTeletext = 0x56;
    private const byte DescriptorSubtitling = 0x59;
    private const byte DescriptorAc3 = 0x6A;
    private const byte DescriptorEnhancedAc3 = 0x7A;
    private const byte DescriptorDts = 0x7B;
    private const byte DescriptorAac = 0x7C;

    /// <summary>
    /// DVB carries its newer descriptors inside an extension descriptor, whose first body byte
    /// says which one it is. Six is the supplementary audio descriptor.
    /// </summary>
    private const byte DescriptorExtension = 0x7F;
    private const byte ExtensionSupplementaryAudio = 0x06;

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
        if (!PsiSectionHeader.TryValidate(section, TableIdProgramMap, MinimumBodyLength, out var end))
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
        while (offset < end)
        {
            // Every remaining byte belongs to an entry, so a stub too short to be one means the
            // lengths above it were wrong.
            if (offset + 5 > end)
            {
                return null;
            }

            var streamType = section[offset];
            var pid = ((section[offset + 1] & 0x1F) << 8) | section[offset + 2];
            var infoLength = ((section[offset + 3] & 0x0F) << 8) | section[offset + 4];

            var descriptorsStart = offset + 5;
            if (descriptorsStart + infoLength > end)
            {
                // The entry claims more descriptor bytes than the section holds. Truncating to
                // what is there would publish a description of a table that was never received;
                // half a program map is not a program map.
                return null;
            }

            var descriptors = section.Slice(descriptorsStart, infoLength);
            if (!Describe(streamType, pid, descriptors, out var entry))
            {
                return null;
            }

            entries.Add(entry);
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

    private static bool Describe(byte streamType, int pid, ReadOnlySpan<byte> descriptors, out ProgramMapEntry entry)
    {
        entry = null!;

        string? language = null;
        var hearingImpaired = false;

        // Two sources, and the descriptor that states the editorial role outright wins. Kept
        // apart until the end so their order in the table cannot decide the outcome.
        var purposeFromLanguage = AudioPurpose.Unknown;
        var purposeFromSupplementary = AudioPurpose.Unknown;

        // Set only by a descriptor, and only for stream types that need one to be identified.
        string? descriptorCodec = null;
        ElementaryStreamKind? descriptorKind = null;

        var offset = 0;
        while (offset < descriptors.Length)
        {
            if (offset + 2 > descriptors.Length)
            {
                // A descriptor header that does not fit. The loop above allotted this entry
                // exactly the bytes the table said it had, so a remainder too small to be a
                // descriptor means one of the lengths lied.
                return false;
            }

            var tag = descriptors[offset];
            var length = descriptors[offset + 1];
            var bodyStart = offset + 2;
            if (bodyStart + length > descriptors.Length)
            {
                return false;
            }

            var body = descriptors.Slice(bodyStart, length);
            switch (tag)
            {
                case DescriptorLanguage when body.Length >= 4:
                    language ??= ReadLanguage(body[..3]);

                    // ISO 13818-1 audio_type. Only two of its values name an addition to the
                    // programme beyond doubt: 2 is a hearing impaired mix and 3 is a commentary
                    // for the visually impaired. Zero is the ordinary programme track. One is
                    // nominally clean effects and is left unclassified rather than excluded,
                    // because broadcasters use it for main audio as well.
                    hearingImpaired |= body[3] == 2;
                    purposeFromLanguage = FirstConclusive(purposeFromLanguage, body[3] switch
                    {
                        0 => AudioPurpose.Main,
                        2 or 3 => AudioPurpose.Supplementary,
                        _ => AudioPurpose.Unknown,
                    });
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

                case DescriptorExtension
                    when body.Length >= 2 && body[0] == ExtensionSupplementaryAudio:

                    // EN 300 468 supplementary_audio_descriptor. The byte after the extension tag
                    // is mix_type in bit 7, editorial_classification in bits 6..2, then a reserved
                    // bit and language_code_present. Only the three classifications the standard
                    // actually names are acted on; the reserved range says the broadcast is
                    // describing something this does not know, which is not the same as an
                    // addition to the programme.
                    purposeFromSupplementary = FirstConclusive(purposeFromSupplementary, ((body[1] >> 2) & 0x1F) switch
                    {
                        0x00 => AudioPurpose.Main,
                        0x01 or 0x02 or 0x03 => AudioPurpose.Supplementary,
                        _ => AudioPurpose.Unknown,
                    });
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
        if (kind == ElementaryStreamKind.Unknown && descriptorKind is { } resolvedKind)
        {
            kind = resolvedKind;
            codec = descriptorCodec;
        }
        else if (codec is null && descriptorCodec is not null)
        {
            codec = descriptorCodec;
        }

        entry = new ProgramMapEntry
        {
            StreamType = streamType,
            Pid = pid,
            Kind = kind,
            Codec = codec,
            Language = language,
            IsHearingImpaired = hearingImpaired,

            // The supplementary audio descriptor states the role outright, so where it appears it
            // is the answer. The audio type is only consulted in its absence.
            AudioPurpose = purposeFromSupplementary != AudioPurpose.Unknown
                ? purposeFromSupplementary
                : purposeFromLanguage,
        };

        return true;
    }

    /// <summary>
    /// Keeps the first conclusive answer, so a repeated descriptor cannot overwrite one that
    /// already said something.
    /// </summary>
    private static AudioPurpose FirstConclusive(AudioPurpose current, AudioPurpose candidate)
        => current == AudioPurpose.Unknown ? candidate : current;

    private static (ElementaryStreamKind Kind, string? Codec) FromStreamType(byte streamType) => streamType switch
    {
        0x01 or 0x02 => (ElementaryStreamKind.Video, "mpeg2video"),
        0x10 => (ElementaryStreamKind.Video, "mpeg4"),
        0x1B => (ElementaryStreamKind.Video, "h264"),
        0x24 => (ElementaryStreamKind.Video, "hevc"),

        // MPEG audio. The table does not say which layer, and this used to be left unnamed for
        // that reason -- on the principle that an absent value is safer than a guessed one. It is
        // not, here: Jellyfin reads an unnamed codec as one no device profile can match, and the
        // effect is worse than a wrong name would be. Given the same stream, its own ranking
        // picks the unnamed track and direct plays, and its later re-check of that very track
        // refuses it, so the client is sent to a transcode of a channel it could have played.
        //
        // Named, then, and named after what is actually there. DVB mandates Layer II for MPEG
        // audio (ETSI TS 101 154), and FFmpeg reports "mp2" for these stream types -- measured on
        // ZDF, whose three 0x03 tracks it reads as mp2. With the name present Jellyfin compares
        // like with like: it prefers a track the client supports, such as the AC-3 one beside
        // these, and only transcodes when there is genuinely nothing it can play.
        0x03 or 0x04 => (ElementaryStreamKind.Audio, "mp2"),

        0x0F => (ElementaryStreamKind.Audio, "aac"),
        0x11 => (ElementaryStreamKind.Audio, "aac_latm"),

        // The ATSC assignments for AC-3 and E-AC-3. DVB carries both under 0x06 with a
        // descriptor instead, which is handled there.
        0x81 => (ElementaryStreamKind.Audio, "ac3"),
        0x87 => (ElementaryStreamKind.Audio, "eac3"),

        // Types that are data, and say so.
        0x05 or 0x0B or 0x0C or 0x0D or 0x86 => (ElementaryStreamKind.Data, null),

        // 0x06 is private data in PES packets, which is where DVB puts AC-3, E-AC-3, subtitles
        // and teletext. It says nothing by itself; if no descriptor named it, nothing here knows
        // what it is.
        _ => (ElementaryStreamKind.Unknown, null),
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
