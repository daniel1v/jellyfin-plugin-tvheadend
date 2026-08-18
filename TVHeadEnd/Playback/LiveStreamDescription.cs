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
    /// <para>
    /// Two conditions, and both are about not suppressing Jellyfin's own inspection on the
    /// strength of something incomplete. There has to be a video stream, because Jellyfin
    /// dereferences one while preparing playback and because a television channel without one
    /// means the table was not understood. And nothing may be left unclassified: an entry this
    /// plugin could not identify is a hole in the description, and Jellyfin finding out for
    /// itself is better than being told a partial answer is the whole one.
    /// </para>
    /// <para>
    /// A radio service is not published as complete either, for the same reason -- it has no
    /// video, so there is nothing here to say about it that Jellyfin cannot establish better.
    /// </para>
    /// </remarks>
    public bool IsUsable
        => Streams.Any(stream => stream.Type == MediaStreamType.Video) && !HasUnclassifiedStream;

    /// <summary>
    /// Gets a value indicating whether the program map named something this plugin could not
    /// identify.
    /// </summary>
    public required bool HasUnclassifiedStream { get; init; }

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

        return new LiveStreamDescription
        {
            Streams = streams,
            HasUnclassifiedStream = programMap.Entries.Any(
                entry => entry.Kind == ElementaryStreamKind.Unknown),
        };
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
