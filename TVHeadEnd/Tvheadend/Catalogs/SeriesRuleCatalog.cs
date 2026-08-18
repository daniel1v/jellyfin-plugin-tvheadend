using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using Tvheadend.Htsp.Protocol;

namespace TVHeadEnd.Tvheadend.Catalogs;

/// <summary>
/// The autorec entries TVHeadend has announced, which are what Jellyfin calls series timers.
/// </summary>
public sealed class SeriesRuleCatalog
{
    /// <summary>
    /// How far ahead an entry with no retention is reported to run.
    /// </summary>
    private const int DefaultRetentionDays = 365;

    private readonly ILogger<SeriesRuleCatalog> _logger;
    private readonly Dictionary<string, SeriesRule> _rules = [];
    private readonly object _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesRuleCatalog"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public SeriesRuleCatalog(ILogger<SeriesRuleCatalog> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets how many rules are known.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _rules.Count;
            }
        }
    }

    /// <summary>
    /// Records a rule the server added or changed.
    /// </summary>
    /// <remarks>
    /// An update mentions only what changed, so it is merged onto the rule as it stood.
    /// </remarks>
    /// <param name="message">The <c>autorecEntryAdd</c> or <c>autorecEntryUpdate</c> message.</param>
    public void AddOrUpdate(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var id = message.GetString("id");
        if (id is null)
        {
            _logger.LogDebug("A TVHeadend series rule arrived without an identifier and was skipped");
            return;
        }

        lock (_gate)
        {
            _rules.TryGetValue(id, out var existing);
            _rules[id] = new SeriesRule(
                id,
                message.GetString("title") ?? existing?.Title,
                message.GetInt32("channel")?.ToString(CultureInfo.InvariantCulture) ?? existing?.ChannelId,
                message.GetInt32("daysOfWeek") ?? existing?.DaysOfWeek,
                message.GetInt32("retention") ?? existing?.RetentionDays,
                message.GetInt64("startExtra") ?? existing?.PrePaddingMinutes,
                message.GetInt64("stopExtra") ?? existing?.PostPaddingMinutes,
                message.GetInt32("priority") ?? existing?.Priority,
                message.GetString("description") ?? existing?.Description);
        }
    }

    /// <summary>
    /// Forgets a rule the server removed.
    /// </summary>
    /// <param name="message">The <c>autorecEntryDelete</c> message.</param>
    public void Remove(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.GetString("id") is { } id)
        {
            lock (_gate)
            {
                _rules.Remove(id);
            }
        }
    }

    /// <summary>
    /// Discards everything, for a connection that is starting over.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _rules.Clear();
        }
    }

    /// <summary>
    /// Describes the rules for Jellyfin.
    /// </summary>
    /// <returns>The series timers.</returns>
    public IReadOnlyList<SeriesTimerInfo> ToSeriesTimers()
    {
        List<SeriesRule> snapshot;
        lock (_gate)
        {
            snapshot = [.. _rules.Values];
        }

        var now = DateTime.UtcNow;
        var result = new List<SeriesTimerInfo>(snapshot.Count);
        foreach (var rule in snapshot)
        {
            var retention = rule.RetentionDays is > 0 and < DefaultRetentionDays * 10
                ? rule.RetentionDays.Value
                : DefaultRetentionDays;

            result.Add(new SeriesTimerInfo
            {
                Id = rule.Id,
                Name = rule.Title,
                SeriesId = rule.Title,
                Overview = rule.Description,
                ChannelId = rule.ChannelId,
                RecordAnyChannel = string.IsNullOrEmpty(rule.ChannelId),
                Days = ToDays(rule.DaysOfWeek),
                RecordAnyTime = rule.DaysOfWeek is null or 0 or 0x7F,
                StartDate = now,
                EndDate = now.AddDays(retention),
                Priority = rule.Priority ?? 0,
                PrePaddingSeconds = (int)((rule.PrePaddingMinutes ?? 0) * 60),
                PostPaddingSeconds = (int)((rule.PostPaddingMinutes ?? 0) * 60),
                IsPrePaddingRequired = rule.PrePaddingMinutes is > 0,
                IsPostPaddingRequired = rule.PostPaddingMinutes is > 0,
            });
        }

        return result;
    }

    /// <summary>
    /// Turns the days of a series timer into TVHeadend's bit field.
    /// </summary>
    /// <param name="days">The days.</param>
    /// <returns>The bit field, Monday in the lowest bit.</returns>
    public static int ToDaysOfWeek(IEnumerable<DayOfWeek> days)
    {
        ArgumentNullException.ThrowIfNull(days);

        var result = 0;
        foreach (var day in days)
        {
            result |= day switch
            {
                DayOfWeek.Monday => 0x01,
                DayOfWeek.Tuesday => 0x02,
                DayOfWeek.Wednesday => 0x04,
                DayOfWeek.Thursday => 0x08,
                DayOfWeek.Friday => 0x10,
                DayOfWeek.Saturday => 0x20,
                DayOfWeek.Sunday => 0x40,
                _ => 0,
            };
        }

        return result;
    }

    /// <summary>
    /// Gets the minutes from midnight UTC, which is how TVHeadend states a start window.
    /// </summary>
    /// <param name="time">The time.</param>
    /// <returns>The minutes from midnight.</returns>
    public static int ToMinutesFromMidnight(DateTime time)
    {
        var utc = time.ToUniversalTime();
        return (utc.Hour * 60) + utc.Minute;
    }

    private static List<DayOfWeek> ToDays(int? daysOfWeek)
    {
        var result = new List<DayOfWeek>();
        if (daysOfWeek is not { } bits || bits == 0)
        {
            return result;
        }

        if ((bits & 0x01) != 0)
        {
            result.Add(DayOfWeek.Monday);
        }

        if ((bits & 0x02) != 0)
        {
            result.Add(DayOfWeek.Tuesday);
        }

        if ((bits & 0x04) != 0)
        {
            result.Add(DayOfWeek.Wednesday);
        }

        if ((bits & 0x08) != 0)
        {
            result.Add(DayOfWeek.Thursday);
        }

        if ((bits & 0x10) != 0)
        {
            result.Add(DayOfWeek.Friday);
        }

        if ((bits & 0x20) != 0)
        {
            result.Add(DayOfWeek.Saturday);
        }

        if ((bits & 0x40) != 0)
        {
            result.Add(DayOfWeek.Sunday);
        }

        return result;
    }

    private sealed record SeriesRule(
        string Id,
        string? Title,
        string? ChannelId,
        int? DaysOfWeek,
        int? RetentionDays,
        long? PrePaddingMinutes,
        long? PostPaddingMinutes,
        int? Priority,
        string? Description);
}
