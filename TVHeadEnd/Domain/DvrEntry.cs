using System;
using System.Globalization;
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
    /// Gets when the recording starts.
    /// </summary>
    public DateTime StartUtc { get; init; }

    /// <summary>
    /// Gets when the recording stops.
    /// </summary>
    public DateTime StopUtc { get; init; }

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
            FilePath = message.GetString("path"),
            Url = message.GetString("url"),
            Error = message.GetString("error"),
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
            FilePath = message.Contains("path") ? updated.FilePath : existing.FilePath,
            Url = message.Contains("url") ? updated.Url : existing.Url,
            Error = message.Contains("error") ? updated.Error : existing.Error,
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

    private static DateTime ReadUnixTime(HtspMessage message, string field)
    {
        var seconds = message.GetInt64(field);
        return seconds is null ? default : UnixEpochUtc.AddSeconds(seconds.Value);
    }
}
