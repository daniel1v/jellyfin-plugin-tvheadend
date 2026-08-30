using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TVHeadEnd.Core.Dvr;

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
    /// Gets which season of its series the recording belongs to, where the broadcast said.
    /// </summary>
    public int? SeasonNumber { get; init; }

    /// <summary>
    /// Gets which episode of its season the recording is, where the broadcast said.
    /// </summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>
    /// Gets how the broadcast wrote its own episode number, where it wrote one at all.
    /// </summary>
    /// <remarks>
    /// TVHeadend sends this on a DVR entry as <c>episode</c> and on a guide event as
    /// <c>episodeOnscreen</c> -- the same field of the same structure under two names. It is free
    /// text, "S02E14" or "Folge 3", and it is what remains when the broadcast numbered its
    /// episodes in a form nothing could parse into <see cref="SeasonNumber"/> and
    /// <see cref="EpisodeNumber"/>. Kept because it is evidence that a recording is an episode
    /// even when the numbers are not there.
    /// </remarks>
    public string? EpisodeOnscreen { get; init; }

    /// <summary>
    /// Gets the year the recorded programme was made, where the broadcast stated one.
    /// </summary>
    public int? ProductionYear { get; init; }

    /// <summary>
    /// Gets the parental rating the broadcast carried, in the words the broadcaster used.
    /// </summary>
    /// <remarks>
    /// Taken as text and not interpreted. TVHeadend has already resolved this from whichever
    /// rating authority the broadcast named, and what it hands over is the label meant to be
    /// shown -- converting a German FSK number into an American certificate, or the other way
    /// round, would be this plugin inventing a claim about what a viewer may watch.
    /// </remarks>
    public string? RatingLabel { get; init; }

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
}
