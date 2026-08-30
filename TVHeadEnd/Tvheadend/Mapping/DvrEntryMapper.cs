using System;
using System.Collections.Generic;
using System.Globalization;
using TVHeadEnd.Core.Broadcast;
using TVHeadEnd.Core.Dvr;
using HtspMessage = Tvheadend.Htsp.Protocol.HtspMessage;

namespace TVHeadEnd.Tvheadend.Mapping;

/// <summary>
/// Reads what TVHeadend says about a DVR entry into what this plugin means by one.
/// </summary>
/// <remarks>
/// <para>
/// Every field name, every identifier that is really a number, every time that is really seconds
/// since 1970, and every state that is really the string "recording" lives here. The entry itself
/// knows none of it: it knows what a recording <em>is</em>, which is a question that outlives any
/// particular protocol and any particular version of it.
/// </para>
/// <para>
/// The rule this exists to keep is the partial one. TVHeadend sends only what changed -- while a
/// recording runs it sends bare statistics every few seconds -- so a field the message does not
/// mention keeps the value it had. Reading an absent field as "now empty" would erase a title, a
/// season, a rating or a file list within seconds of learning it, and the message itself is the
/// only place that difference can still be seen.
/// </para>
/// </remarks>
public static class DvrEntryMapper
{
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

            // The same episode structure the guide sends, under the name TVHeadend gives it on a
            // DVR entry: seasonNumber and episodeNumber are spelled alike, but the free-text form
            // arrives as "episode" here and as "episodeOnscreen" on an event.
            SeasonNumber = ReadPositive(message, "seasonNumber"),
            EpisodeNumber = ReadPositive(message, "episodeNumber"),
            EpisodeOnscreen = message.GetString("episode"),

            ProductionYear = BroadcastProductionYear.FromCopyrightYear(message.GetInt32("copyrightYear")),
            RatingLabel = message.GetString("ratingLabel"),

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

            // The metadata a recording is described by. TVHeadend sends a bare statistics update
            // every few seconds while a recording runs -- bytes written, errors counted, and
            // nothing else -- so a field taken from the update unconditionally would be erased
            // within seconds of being learned, and the recording would go back to being a
            // programme with no season, no episode, no year and no rating.
            SeasonNumber = message.Contains("seasonNumber") ? updated.SeasonNumber : existing.SeasonNumber,
            EpisodeNumber = message.Contains("episodeNumber") ? updated.EpisodeNumber : existing.EpisodeNumber,
            EpisodeOnscreen = message.Contains("episode") ? updated.EpisodeOnscreen : existing.EpisodeOnscreen,
            ProductionYear = message.Contains("copyrightYear") ? updated.ProductionYear : existing.ProductionYear,
            RatingLabel = message.Contains("ratingLabel") ? updated.RatingLabel : existing.RatingLabel,

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
    /// Reads a count TVHeadend states as an unsigned number, where it states a real one.
    /// </summary>
    /// <remarks>
    /// Zero is how the server says "not known" for these -- it omits the field entirely in that
    /// case, and a sender that does not is saying the same thing. Season zero and episode zero
    /// would both be published as real numbers otherwise.
    /// </remarks>
    private static int? ReadPositive(HtspMessage message, string field)
        => message.GetInt32(field) is { } value && value > 0 ? value : null;

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
            read.Add(ReadFile(file));
        }

        return read;
    }

    /// <summary>
    /// Reads one entry of the <c>files</c> list TVHeadend sends with a DVR entry.
    /// </summary>
    private static DvrRecordingFile ReadFile(HtspMessage file) => new()
    {
        StartUtc = ReadFileTime(file, "start"),
        StopUtc = ReadFileTime(file, "stop"),
        Size = file.GetInt64("size"),
        FileName = file.GetString("filename"),
    };

    /// <summary>
    /// Reads a time a file may not have yet.
    /// </summary>
    /// <remarks>
    /// Zero is how an unset time arrives, not a recording made in 1970. It is what a file still
    /// being written reports for its stop, and the difference between "this is how long it is"
    /// and "this is still being written" rests on it.
    /// </remarks>
    private static DateTime? ReadFileTime(HtspMessage file, string field)
    {
        if (file.GetInt64(field) is not { } seconds || seconds <= 0)
        {
            return null;
        }

        return DateTime.UnixEpoch.AddSeconds(seconds);
    }

    private static DateTime ReadUnixTime(HtspMessage message, string field)
    {
        var seconds = message.GetInt64(field);
        return seconds is null ? default : DateTime.UnixEpoch.AddSeconds(seconds.Value);
    }
}
