using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using TVHeadEnd.Compatibility.Jellyfin12;
using TVHeadEnd.Core.Media;

namespace TVHeadEnd.Playback;

/// <summary>
/// What a live stream contains, read from the transport stream that is arriving.
/// </summary>
/// <remarks>
/// <para>
/// One source, and the right one: the Program Map Table of the bytes being delivered. It is the
/// same table libavformat walks to create its streams, so an entry's position here is the index
/// every later <c>-map</c> argument will mean.
/// </para>
/// <para>
/// This is the whole description a live source ever gets. There is no probe behind it and no
/// fallback to one: a stream this cannot describe is not published at all, because a media source
/// that asks Jellyfin to inspect a live channel costs a second read of a stream that is already
/// being read, and answers a question the program map has already answered.
/// </para>
/// <para>
/// Only what the table states. Frame size, frame rate, bit rate and codec profile do not appear
/// in a PMT and are left unset rather than established by a second analysis. Jellyfin treats an
/// absent optional value as unknown and carries on; it is a wrong value that makes it choose
/// badly.
/// </para>
/// </remarks>
public sealed record LiveStreamDescription
{
    /// <summary>
    /// Gets the streams, at the indices FFmpeg will give them.
    /// </summary>
    public required IReadOnlyList<MediaStream> Streams { get; init; }

    /// <summary>
    /// Describes a stream from the program map of the bytes arriving.
    /// </summary>
    /// <remarks>
    /// The channel's own kind decides what has to be present, rather than being inferred from the
    /// table: a program map with no video is a complete description of a radio service and an
    /// incomplete one of a television channel, and only the channel list knows which this is.
    /// </remarks>
    /// <param name="programMap">The program map.</param>
    /// <param name="channelType">What the channel list says this channel is.</param>
    /// <returns>
    /// The description, or <see langword="null"/> when the table does not carry the elementary
    /// stream the channel's kind requires.
    /// </returns>
    public static LiveStreamDescription? FromProgramMap(ProgramMapTable programMap, ChannelType channelType)
    {
        ArgumentNullException.ThrowIfNull(programMap);

        if (programMap.Entries.Count == 0)
        {
            return null;
        }

        var required = channelType == ChannelType.Radio
            ? ElementaryStreamKind.Audio
            : ElementaryStreamKind.Video;

        // The one stream the channel cannot be played without. Anything else the table names is
        // a bonus; this is the difference between a description and a guess.
        if (!programMap.Entries.Any(entry => entry.Kind == required))
        {
            return null;
        }

        // Every entry becomes a stream, in this order, because that is what libavformat does as
        // it walks the table. An entry that could not be classified still occupies its index --
        // leaving a gap would shift everything after it -- and does not disqualify the rest.
        var streams = new List<MediaStream>(programMap.Entries.Count);
        for (var index = 0; index < programMap.Entries.Count; index++)
        {
            streams.Add(Describe(programMap.Entries[index], index));
        }

        return new LiveStreamDescription { Streams = streams };
    }

    private static MediaStream Describe(ProgramMapEntry entry, int index) => entry.Kind switch
    {
        ElementaryStreamKind.Video => new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = index,
            Codec = JellyfinCodecNames.For(entry.Codec),

            // Left alone. Jellyfin overwrites it for every external live TV service anyway, and
            // the transport stream does not state it.
            IsInterlaced = false,
        },

        ElementaryStreamKind.Audio => new MediaStream
        {
            Type = MediaStreamType.Audio,
            Index = index,
            Codec = JellyfinCodecNames.For(entry.Codec),
            Language = entry.Language,
            IsHearingImpaired = entry.IsHearingImpaired,

            // The purpose is broadcast metadata; deciding that a purpose makes a track one of
            // Jellyfin's defaults is not. That second half is a host's selection rule and lives
            // in Compatibility/Jellyfin12, where the recordings path reads it too -- the same
            // broadcast reaches Jellyfin by two routes and must be described the same way by both.
            IsDefault = entry.AudioPurpose.BelongsInTheDefaultSet(),
        },

        ElementaryStreamKind.Subtitle => new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Index = index,
            Codec = JellyfinCodecNames.For(entry.Codec),
            Language = entry.Language,
            IsHearingImpaired = entry.IsHearingImpaired,
            SupportsExternalStream = false,
        },

        // Data, and anything the table did not identify. It keeps its index so the streams after
        // it keep theirs, and claims nothing about itself.
        _ => new MediaStream
        {
            Type = MediaStreamType.Data,
            Index = index,
        },
    };
}
