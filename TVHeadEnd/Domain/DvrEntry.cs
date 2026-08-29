using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tvheadend.Htsp.Protocol;

namespace TVHeadEnd.Domain;

/// <summary>
/// One TVHeadend DVR entry, whatever state it is in.
/// </summary>
/// <remarks>
/// <para>
/// TVHeadend does not distinguish a timer from a recording: both are this entry, moving from
/// <see cref="DvrState.Scheduled"/> through <see cref="DvrState.Recording"/> to
/// <see cref="DvrState.Completed"/>. Jellyfin does distinguish them, asking for timers through
/// ILiveTvService and for recordings through IChannel, so the split belongs in the mappers that
/// answer those two questions -- not in how the entry is read from the server.
/// </para>
/// <para>
/// Series rules are a separate TVHeadend entity, the autorec entry, and are modelled separately.
/// What an entry keeps of one is <see cref="AutoRecId"/>: the rule that created it.
/// </para>
/// </remarks>
public sealed record DvrEntry
{
    private static readonly DateTime UnixEpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// One shared empty list, so that two entries without files compare as equal.
    /// </summary>
    /// <remarks>
    /// Record equality compares an <see cref="IReadOnlyList{T}"/> by reference, and two separately
    /// created empty lists are not the same reference. See <see cref="HasSameContentAs"/>.
    /// </remarks>
    private static readonly IReadOnlyList<DvrRecordingFile> NoFiles = [];

    /// <summary>
    /// Gets the TVHeadend identifier of the entry.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets where the entry has got to.
    /// </summary>
    public DvrState State { get; init; }

    /// <summary>
    /// Gets the channel the entry records.
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// Gets the identifier of the EPG event this entry was made from, if any.
    /// </summary>
    public string? EventId { get; init; }

    /// <summary>
    /// Gets the identifier of the series rule that created this entry, if any.
    /// </summary>
    public string? AutoRecId { get; init; }

    /// <summary>
    /// Gets the title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets the episode title.
    /// </summary>
    public string? Subtitle { get; init; }

    /// <summary>
    /// Gets the overview.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets when the recording is scheduled to start.
    /// </summary>
    public DateTime StartUtc { get; init; }

    /// <summary>
    /// Gets when the recording is scheduled to stop.
    /// </summary>
    /// <remarks>
    /// What was planned, not what happened. A recording stopped by hand ends before this, and
    /// until it has, this is a time in the future -- see <see cref="Files"/> for the times of the
    /// bytes that actually exist.
    /// </remarks>
    public DateTime StopUtc { get; init; }

    /// <summary>
    /// Gets the files TVHeadend has written for this entry, in the order the server lists them.
    /// </summary>
    /// <remarks>
    /// Empty for an entry that has not started, and for a server too old to send the list at all.
    /// The order is the server's own and is kept, because which file is last is what decides
    /// <see cref="PlayableFile"/>.
    /// </remarks>
    public IReadOnlyList<DvrRecordingFile> Files { get; init; } = [];

    /// <summary>
    /// Gets the padding before the scheduled start.
    /// </summary>
    public TimeSpan PrePadding { get; init; }

    /// <summary>
    /// Gets the padding after the scheduled stop.
    /// </summary>
    public TimeSpan PostPadding { get; init; }

    /// <summary>
    /// Gets the recording priority.
    /// </summary>
    public int? Priority { get; init; }

    /// <summary>
    /// Gets the DVB content type the entry was recorded under, where the server states one.
    /// </summary>
    /// <remarks>
    /// The same <c>content_descriptor</c> byte the guide carries, copied onto the DVR entry when
    /// the recording was scheduled -- so it outlives the event it came from, which for a recording
    /// is most of its life. It is what says whether a recording is a film, sport, news or for
    /// children, and the recordings channel groups by exactly that.
    /// </remarks>
    public int? ContentType { get; init; }

    /// <summary>
    /// Gets the path of the recording on the TVHeadend server. Of no use to Jellyfin, which
    /// generally runs elsewhere, but it is what the server reports.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets the server-relative address the recording is served from, as TVHeadend states it.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Gets what TVHeadend reports went wrong, if anything.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets the reference to the entry's own artwork, as TVHeadend states it.
    /// </summary>
    /// <remarks>
    /// Taken from the DVR entry rather than rebuilt from the EPG event it was made from. The
    /// server copies the artwork onto the entry when it schedules the recording, so it is still
    /// there after the event has aged out of the guide -- which for a recording is most of its
    /// life.
    /// </remarks>
    public string? Image { get; init; }

    /// <summary>
    /// Gets the reference to the entry's backdrop, where the server has one.
    /// </summary>
    public string? FanartImage { get; init; }

    /// <summary>
    /// Gets a value indicating whether the recording TVHeadend still lists no longer has a file
    /// behind it.
    /// </summary>
    /// <remarks>
    /// A removed recording keeps its entry, and its state stays "completed"; the only sign is an
    /// error mentioning a missing file. Listing it would offer something unplayable.
    /// </remarks>
    public bool FileIsMissing =>
        Error is not null && Error.Contains("missing", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the file a viewer actually gets, which is the last one.
    /// </summary>
    /// <remarks>
    /// Not a choice this plugin makes. TVHeadend serves <c>/dvrfile/&lt;id&gt;</c> from the last
    /// file of the entry, so describing anything else -- the first, or the several joined together
    /// -- would describe something nobody can play. An entry split across several files is
    /// therefore reported as its last part, and the parts before it are not offered at all.
    /// </remarks>
    public DvrRecordingFile? PlayableFile => Files.Count == 0 ? null : Files[^1];

    /// <summary>
    /// Gets how long the file a viewer gets actually runs.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> while the recording is still being written, and where the server
    /// gave no usable times for it.
    /// </remarks>
    public TimeSpan? RecordedDuration => PlayableFile?.Duration;

    /// <summary>
    /// Gets how long the recording was scheduled to run.
    /// </summary>
    public TimeSpan? ScheduledDuration => StopUtc > StartUtc ? StopUtc - StartUtc : null;

    /// <summary>
    /// Gets the last moment the recording itself is known to have done something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the file only: when it was closed, or failing that when it was opened. Nothing
    /// scheduled is consulted, so this never claims a time that has not happened. It is
    /// <see langword="null"/> for an entry with no file, because then nothing has.
    /// </para>
    /// <para>
    /// This deliberately does not double as the version marker Jellyfin compares to decide whether
    /// to rewrite a stored item. The two pull in opposite directions -- one must be truthful, the
    /// other must only ever rise -- and one value could not be both: an entry whose scheduled start
    /// had not arrived yet, which pre-padding makes an ordinary case, reported a future as though
    /// it had passed. The marker is built separately, in RecordingsChannel.PublishedDateFor.
    /// </para>
    /// </remarks>
    public DateTime? RecordedActivityUtc => PlayableFile is { } file ? file.StopUtc ?? file.StartUtc : null;

    /// <summary>
    /// Reports whether another entry says exactly the same thing as this one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TVHeadend sends a <c>dvrEntryUpdate</c> for a running recording every few seconds carrying
    /// nothing but its statistics -- bytes written, disk space, errors counted. None of that is
    /// read into a <see cref="DvrEntry"/>, so those updates produce an entry identical to the one
    /// already held, and counting them as changes rotated the recordings cache continuously for
    /// as long as anything was recording.
    /// </para>
    /// <para>
    /// The record's own equality does everything except the file list: for an
    /// <see cref="IReadOnlyList{T}"/> it falls back to reference equality, so a <c>files</c> block
    /// re-sent unchanged would compare as different every time. Both sides are therefore compared
    /// against one shared empty list first, and the files element by element after it.
    /// </para>
    /// </remarks>
    /// <param name="other">The entry to compare against.</param>
    /// <returns>Whether the two carry the same information.</returns>
    public bool HasSameContentAs(DvrEntry other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var thisWithoutFiles = this with { Files = NoFiles };
        var otherWithoutFiles = other with { Files = NoFiles };

        return thisWithoutFiles == otherWithoutFiles && Files.SequenceEqual(other.Files);
    }

    /// <summary>
    /// Reads an entry from the HTSP message TVHeadend sent for it.
    /// </summary>
    /// <param name="message">The <c>dvrEntryAdd</c> or <c>dvrEntryUpdate</c> message.</param>
    /// <returns>The entry, or <see langword="null"/> if the message carries no identifier.</returns>
    public static DvrEntry? FromMessage(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.GetInt32("id") is not { } id)
        {
            return null;
        }

        return new DvrEntry
        {
            Id = id.ToString(CultureInfo.InvariantCulture),
            State = ReadState(message.GetString("state")),
            ChannelId = ReadId(message, "channel"),
            EventId = ReadId(message, "eventId"),
            AutoRecId = message.GetString("autorecId"),
            Title = message.GetString("title"),
            Subtitle = message.GetString("subtitle"),

            // Up to HTSP v31 "description" is a collapsed fallback of
            // description/summary/subtitle; from v32 on the three fields are independent, so
            // fall back to keep an overview in both layouts.
            Description = message.GetString("description")
                ?? message.GetString("summary")
                ?? message.GetString("subtitle"),

            StartUtc = ReadUnixTime(message, "start"),
            StopUtc = ReadUnixTime(message, "stop"),

            // TVHeadend states padding in minutes.
            PrePadding = TimeSpan.FromMinutes(message.GetInt64("startExtra") ?? 0),
            PostPadding = TimeSpan.FromMinutes(message.GetInt64("stopExtra") ?? 0),

            Priority = message.GetInt32("priority"),
            ContentType = message.GetInt32("contentType"),
            Files = ReadFiles(message),
            FilePath = message.GetString("path"),
            Url = message.GetString("url"),
            Error = message.GetString("error"),
            Image = message.GetString("image"),
            FanartImage = message.GetString("fanartImage"),
        };
    }

    /// <summary>
    /// Carries the fields an update actually mentioned over the entry as it stood.
    /// </summary>
    /// <remarks>
    /// TVHeadend sends only what changed, so replacing the entry outright would wipe the title,
    /// the times and everything else whenever the state alone moved on.
    /// </remarks>
    /// <param name="existing">The entry as it stood.</param>
    /// <param name="updated">The entry read from the update.</param>
    /// <param name="message">The update, consulted for which fields it mentioned.</param>
    /// <returns>The merged entry.</returns>
    public static DvrEntry Merge(DvrEntry existing, DvrEntry updated, HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(updated);
        ArgumentNullException.ThrowIfNull(message);

        return existing with
        {
            State = message.Contains("state") ? updated.State : existing.State,
            ChannelId = message.Contains("channel") ? updated.ChannelId : existing.ChannelId,
            EventId = message.Contains("eventId") ? updated.EventId : existing.EventId,
            AutoRecId = message.Contains("autorecId") ? updated.AutoRecId : existing.AutoRecId,
            Title = message.Contains("title") ? updated.Title : existing.Title,
            Subtitle = message.Contains("subtitle") ? updated.Subtitle : existing.Subtitle,
            Description = message.Contains("description")
                || message.Contains("summary")
                || message.Contains("subtitle")
                    ? updated.Description
                    : existing.Description,
            StartUtc = message.Contains("start") ? updated.StartUtc : existing.StartUtc,
            StopUtc = message.Contains("stop") ? updated.StopUtc : existing.StopUtc,
            PrePadding = message.Contains("startExtra") ? updated.PrePadding : existing.PrePadding,
            PostPadding = message.Contains("stopExtra") ? updated.PostPadding : existing.PostPadding,
            Priority = message.Contains("priority") ? updated.Priority : existing.Priority,
            ContentType = message.Contains("contentType") ? updated.ContentType : existing.ContentType,

            // An update mentions the file list only when it changed, and a state change does not.
            // Replacing it unconditionally would empty it every time a recording moved on -- and
            // the move from recording to completed, the one update that settles a file's stop, is
            // exactly when losing it would cost the real duration.
            Files = message.Contains("files") ? updated.Files : existing.Files,
            FilePath = message.Contains("path") ? updated.FilePath : existing.FilePath,
            Url = message.Contains("url") ? updated.Url : existing.Url,
            Error = message.Contains("error") ? updated.Error : existing.Error,

            // Artwork was missing from this list, so a picture the server sent later -- which is
            // when it arrives, once the entry has been scheduled and the metadata catches up --
            // was read into the update and then dropped on the floor. Every field the merge does
            // not mention keeps the old value forever, which for artwork meant "none".
            Image = message.Contains("image") ? updated.Image : existing.Image,
            FanartImage = message.Contains("fanartImage") ? updated.FanartImage : existing.FanartImage,
        };
    }

    private static DvrState ReadState(string? state) => state switch
    {
        "scheduled" => DvrState.Scheduled,
        "recording" => DvrState.Recording,
        "completed" => DvrState.Completed,
        "missed" => DvrState.Missed,
        "invalid" => DvrState.Invalid,
        _ => DvrState.Unknown,
    };

    private static string? ReadId(HtspMessage message, string field)
        => message.GetInt32(field)?.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the <c>files</c> list, keeping the server's order and every entry in it.
    /// </summary>
    /// <remarks>
    /// A file the server described incompletely is kept rather than skipped. Which file is last is
    /// what decides the one a viewer gets, so dropping one would silently make the entry describe
    /// a part nobody is served; an incomplete file simply has no duration to offer, and the mapper
    /// falls back from there.
    /// </remarks>
    private static IReadOnlyList<DvrRecordingFile> ReadFiles(HtspMessage message)
    {
        var files = message.GetMapList("files");
        if (files.Count == 0)
        {
            return [];
        }

        var read = new List<DvrRecordingFile>(files.Count);
        foreach (var file in files)
        {
            read.Add(DvrRecordingFile.FromMessage(file));
        }

        return read;
    }

    private static DateTime ReadUnixTime(HtspMessage message, string field)
    {
        var seconds = message.GetInt64(field);
        return seconds is null ? default : UnixEpochUtc.AddSeconds(seconds.Value);
    }
}
