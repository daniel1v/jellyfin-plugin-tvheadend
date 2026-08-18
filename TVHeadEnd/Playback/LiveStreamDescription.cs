using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;
using TVHeadEnd.Streaming;

namespace TVHeadEnd.Playback;

/// <summary>
/// What a live stream contains, read from the transport stream that is arriving.
/// </summary>
/// <remarks>
/// <para>
/// One source, and the right one: the Program Map Table of the bytes being delivered. It is the
/// same table libavformat walks to create its streams, so an entry's position here is the index
/// every later <c>-map</c> argument will mean. Nothing is correlated against a second account of
/// the stream, because there is no second account that could be more current than the stream
/// itself.
/// </para>
/// <para>
/// Only what the table states. Frame size, frame rate, bit rate and codec profile do not appear
/// in a PMT and are not needed for the playback decision, so they are left unset rather than
/// established by a second analysis. Jellyfin treats an absent optional value as "unknown" and
/// carries on; it is a wrong value that makes it choose badly.
/// </para>
/// </remarks>
public sealed record LiveStreamDescription
{
    /// <summary>
    /// Gets the streams, at the indices FFmpeg will give them.
    /// </summary>
    public required IReadOnlyList<MediaStream> Streams { get; init; }

    /// <summary>
    /// Gets a value indicating whether the description may be published as complete.
    /// </summary>
    /// <remarks>
    /// A television channel with no video stream has not been described; something in the table
    /// was not understood. Publishing that as complete would suppress Jellyfin's own inspection
    /// on the strength of a description that is missing the one stream it dereferences while
    /// preparing playback.
    /// </remarks>
    public bool IsUsable => Streams.Any(stream => stream.Type == MediaStreamType.Video);

    /// <summary>
    /// Describes a stream from the program map of the bytes arriving.
    /// </summary>
    /// <param name="programMap">The program map.</param>
    /// <returns>
    /// The description, or <see langword="null"/> when the table names no elementary streams.
    /// </returns>
    public static LiveStreamDescription? FromProgramMap(ProgramMapTable programMap)
    {
        ArgumentNullException.ThrowIfNull(programMap);

        if (programMap.Entries.Count == 0)
        {
            return null;
        }

        // Every entry becomes a stream, in this order, because that is what libavformat does as
        // it walks the table. An entry that could not be classified still occupies its index --
        // leaving a gap would shift everything after it.
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
            Codec = entry.Codec,

            // Left alone. Jellyfin overwrites it for every external live TV service anyway, and
            // the transport stream does not state it.
            IsInterlaced = false,
        },

        ElementaryStreamKind.Audio => new MediaStream
        {
            Type = MediaStreamType.Audio,
            Index = index,
            Codec = entry.Codec,
            Language = entry.Language,
            IsHearingImpaired = entry.IsHearingImpaired,
        },

        ElementaryStreamKind.Subtitle => new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Index = index,
            Codec = entry.Codec,
            Language = entry.Language,
            IsHearingImpaired = entry.IsHearingImpaired,
            SupportsExternalStream = false,
        },

        _ => new MediaStream
        {
            Type = MediaStreamType.Data,
            Index = index,
        },
    };
}
