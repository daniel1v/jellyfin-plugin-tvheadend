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
/// <remarks>
/// An autorec entry says more than a Jellyfin series timer can hold, so the rule is kept as the
/// server stated it and projected on the way out. What Jellyfin cannot express is still here to
/// be read back when a rule is written again -- an edit that only changed the padding must not
/// quietly reset everything the editor never showed.
/// </remarks>
public sealed class SeriesRuleCatalog
{
    /// <summary>
    /// Every day of the week set, which is how TVHeadend spells "any day".
    /// </summary>
    /// <remarks>
    /// Sent explicitly rather than left out. An update that omits the field leaves the old filter
    /// in place, so a rule narrowed to Mondays could never be widened again.
    /// </remarks>
    public const int AllDaysOfWeek = 0x7F;

    /// <summary>
    /// No day of the week set, which is how TVHeadend spells a rule that matches nothing.
    /// </summary>
    public const int NoDaysOfWeek = 0;

    /// <summary>
    /// What TVHeadend puts in <c>start</c> and <c>startWindow</c> for a rule with no time limit.
    /// </summary>
    public const int AnyTime = -1;

    /// <summary>
    /// DVR_AUTOREC_BTYPE_ALL, meaning record any broadcast.
    /// </summary>
    public const int BroadcastTypeAll = 0;

    /// <summary>
    /// DVR_AUTOREC_BTYPE_NEW_OR_UNKNOWN, meaning record only what is flagged new or unflagged.
    /// </summary>
    public const int BroadcastTypeNewOrUnknown = 1;

    private const int MinutesPerDay = 24 * 60;

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
    /// An update mentions only what changed, so every field falls back to the rule as it stood.
    /// That includes the fields nothing in Jellyfin ever shows: they are what a later write has to
    /// put back.
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
                message.GetString("name") ?? existing?.Name,
                message.GetString("title") ?? existing?.TitlePattern,
                message.GetString("serieslinkUri") ?? existing?.SeriesLink,
                message.GetInt32("channel")?.ToString(CultureInfo.InvariantCulture) ?? existing?.ChannelId,
                message.GetInt32("daysOfWeek") ?? existing?.DaysOfWeek,
                message.GetInt32("start") ?? existing?.Start,
                message.GetInt32("startWindow") ?? existing?.StartWindow,
                message.GetInt32("retention") ?? existing?.RetentionDays,
                message.GetInt64("startExtra") ?? existing?.PrePaddingMinutes,
                message.GetInt64("stopExtra") ?? existing?.PostPaddingMinutes,
                message.GetInt32("priority") ?? existing?.Priority,
                message.GetInt32("broadcastType") ?? existing?.BroadcastType,
                message.GetInt32("maxCount") ?? existing?.MaxCount,
                message.GetString("comment") ?? existing?.Comment);
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
    /// Gets one rule exactly as the server stated it.
    /// </summary>
    /// <remarks>
    /// Read before a rule is written back, so that the fields Jellyfin has no way of showing --
    /// and therefore no way of returning -- are put back rather than replaced by a default.
    /// </remarks>
    /// <param name="id">The TVHeadend identifier.</param>
    /// <returns>The rule, or <see langword="null"/> if the server has not announced one.</returns>
    public SeriesRule? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        lock (_gate)
        {
            return _rules.TryGetValue(id, out var rule) ? rule : null;
        }
    }

    /// <summary>
    /// Describes the rules for Jellyfin.
    /// </summary>
    /// <param name="serverOffset">How far the TVHeadend server's clock is from UTC.</param>
    /// <returns>The series timers.</returns>
    public IReadOnlyList<SeriesTimerInfo> ToSeriesTimers(TimeSpan serverOffset)
    {
        List<SeriesRule> snapshot;
        lock (_gate)
        {
            snapshot = [.. _rules.Values];
        }

        var today = DateTime.UtcNow.Date;
        var result = new List<SeriesTimerInfo>(snapshot.Count);
        foreach (var rule in snapshot)
        {
            result.Add(ToSeriesTimer(rule, serverOffset, today));
        }

        return result;
    }

    /// <summary>
    /// Describes one rule as a Jellyfin series timer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only what the two models actually agree on. Three earlier translations said things
    /// TVHeadend had not: the series identifier was the rule's title, so every rule claimed to be
    /// a series of that name; a rule was "any time" whenever it ran on every day of the week,
    /// which is a different question entirely; and the end date was invented from the retention,
    /// which is how long a finished recording is kept, not when the rule stops applying.
    /// </para>
    /// <para>
    /// What Jellyfin cannot express is simply not answered here -- see the catalog's own rule
    /// object, which keeps it for the write.
    /// </para>
    /// </remarks>
    /// <param name="rule">The rule as the server stated it.</param>
    /// <param name="serverOffset">How far the TVHeadend server's clock is from UTC.</param>
    /// <param name="onDateUtc">The date the rule's time of day is reported against.</param>
    /// <returns>The series timer.</returns>
    internal static SeriesTimerInfo ToSeriesTimer(SeriesRule rule, TimeSpan serverOffset, DateTime onDateUtc)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var anyTime = !IsTimeOfDay(rule.Start) || !IsTimeOfDay(rule.StartWindow);

        // The window the rule accepts, as an instant rather than as minutes on the server's clock.
        // Only the time of day means anything; the date is today's so that a client showing it
        // shows something sane.
        var start = anyTime ? onDateUtc : ToUtc(rule.Start!.Value, serverOffset, onDateUtc);
        var window = anyTime
            ? TimeSpan.Zero
            : TimeSpan.FromMinutes(WindowLength(rule.Start!.Value, rule.StartWindow!.Value));

        return new SeriesTimerInfo
        {
            Id = rule.Id,

            // The rule's own name, which is a different field from the pattern it matches titles
            // with. Reporting the pattern as the name showed "S\.W\.A\.T\." in the library and,
            // worse, invited the next edit to escape it again.
            //
            // A rule made before this plugin knew the difference, or by hand in the server's own
            // interface, may have no name at all. The pattern is then all there is to show, and it
            // is shown as it stands -- unescaping it would be reading a regular expression as if
            // it were a title, which for a rule somebody wrote as a regular expression it is not.
            Name = !string.IsNullOrEmpty(rule.Name) ? rule.Name : rule.TitlePattern,

            Overview = rule.Comment,

            // What TVHeadend binds the rule to, where it has one. Not the title: a title is what
            // the rule is called, and saying it was the series meant every rule that happened to
            // share a name was the same series.
            SeriesId = rule.SeriesLink,

            ChannelId = rule.ChannelId,
            RecordAnyChannel = string.IsNullOrEmpty(rule.ChannelId),

            // From the day mask and nothing else.
            Days = ToDays(rule.DaysOfWeek),

            // From the start window and nothing else.
            RecordAnyTime = anyTime,
            StartDate = start,
            EndDate = start + window,

            Priority = rule.Priority ?? 0,
            PrePaddingSeconds = (int)((rule.PrePaddingMinutes ?? 0) * 60),
            PostPaddingSeconds = (int)((rule.PostPaddingMinutes ?? 0) * 60),
            IsPrePaddingRequired = rule.PrePaddingMinutes is > 0,
            IsPostPaddingRequired = rule.PostPaddingMinutes is > 0,

            // TVHeadend's "new or unknown" is the one broadcast type Jellyfin has a word for.
            // Anything else the server may be set to is left as false here and, crucially, not
            // written back -- see TvheadendDvr.ApplySeriesFields.
            RecordNewOnly = rule.BroadcastType == BroadcastTypeNewOrUnknown,

            // How many recordings the rule keeps. A positive number means the same thing on both
            // sides and travels unchanged.
            //
            // Zero does not. TVHeadend reads it as "no limit of its own, use the DVR profile's",
            // and Jellyfin has no way to say that -- it has a number, and no state for inheriting
            // one. So zero is carried across as zero and left to mean whatever each side means by
            // it; inventing a large number to stand for "unlimited" would replace a limit the
            // profile sets with one this plugin made up.
            KeepUpTo = rule.MaxCount is > 0 ? rule.MaxCount.Value : 0,
        };
    }

    /// <summary>
    /// Turns the days of a series timer into TVHeadend's bit field.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The field is always sent, including when it means no restriction: an omitted field leaves
    /// whatever filter the rule already had, so a rule narrowed to Mondays could never be widened.
    /// </para>
    /// <para>
    /// TVHeadend tells no days from every day -- zero matches nothing, 0x7F matches everything --
    /// and Jellyfin does not. An empty list reaches here from two different places: a new timer
    /// that has not been given any days, which means the ordinary daily rule; and a rule the
    /// server itself set to zero, read back and returned untouched. <paramref name="whenNoneGiven"/>
    /// is which of those the caller is in.
    /// </para>
    /// </remarks>
    /// <param name="days">The days.</param>
    /// <param name="whenNoneGiven">What an empty list means here.</param>
    /// <returns>The bit field, Monday in the lowest bit.</returns>
    public static int ToDaysOfWeek(IEnumerable<DayOfWeek> days, int whenNoneGiven)
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

        return result == 0 ? whenNoneGiven : result;
    }

    /// <summary>
    /// Gets the minutes from midnight on the TVHeadend server's own clock.
    /// </summary>
    /// <remarks>
    /// An autorec start window is stated in the server's local time. This used to read it as UTC,
    /// which is right only for a server that happens to be at UTC; using this process's own zone
    /// instead would be right only when the two machines share one.
    /// </remarks>
    /// <param name="utc">The instant.</param>
    /// <param name="serverOffset">How far the TVHeadend server's clock is from UTC.</param>
    /// <returns>The minutes from midnight.</returns>
    public static int ToMinutesFromMidnight(DateTime utc, TimeSpan serverOffset)
    {
        var onTheServersClock = DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(serverOffset);
        var minutes = (int)onTheServersClock.TimeOfDay.TotalMinutes;

        return ((minutes % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;
    }

    /// <summary>
    /// Gets the instant at which the server's clock reads the given minutes from midnight.
    /// </summary>
    /// <param name="minutesFromMidnight">The minutes from midnight, on the server's clock.</param>
    /// <param name="serverOffset">How far the TVHeadend server's clock is from UTC.</param>
    /// <param name="onDateUtc">The date to place the time on.</param>
    /// <returns>The instant.</returns>
    internal static DateTime ToUtc(int minutesFromMidnight, TimeSpan serverOffset, DateTime onDateUtc)
        => DateTime.SpecifyKind(onDateUtc.Date, DateTimeKind.Utc)
            .AddMinutes(minutesFromMidnight)
            .Subtract(serverOffset);

    /// <summary>
    /// Gets how long a start window runs, in minutes.
    /// </summary>
    /// <remarks>
    /// A window may cross midnight -- 23:30 to 00:10 is forty minutes, not minus 1,400.
    /// </remarks>
    /// <param name="start">The first minute of the window.</param>
    /// <param name="startWindow">The last minute of the window.</param>
    /// <returns>The length in minutes.</returns>
    internal static int WindowLength(int start, int startWindow)
        => (((startWindow - start) % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;

    /// <summary>
    /// Reports whether a value is a time of day rather than TVHeadend's "any time".
    /// </summary>
    /// <param name="minutes">The value the server stated.</param>
    /// <returns>Whether it names a time.</returns>
    internal static bool IsTimeOfDay(int? minutes) => minutes is >= 0 and < MinutesPerDay;

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
}
