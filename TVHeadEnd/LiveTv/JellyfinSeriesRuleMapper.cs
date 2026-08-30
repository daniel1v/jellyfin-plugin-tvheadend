using System;
using System.Collections.Generic;
using MediaBrowser.Controller.LiveTv;
using TVHeadEnd.Tvheadend;
using TVHeadEnd.Tvheadend.Catalogs;

namespace TVHeadEnd.LiveTv;

/// <summary>
/// Describes TVHeadend's own recording rules as the series timers Jellyfin shows.
/// </summary>
/// <remarks>
/// <para>
/// The two models disagree in ways that are easy to paper over and expensive to get wrong. A
/// TVHeadend rule keeps a retention in days, which is not an end date; it matches on a title
/// pattern, which is not a series identity; and it says "any time" with a start window rather
/// than with a day mask. Every one of those is preserved here rather than smoothed out, because
/// what is smoothed out on the way to Jellyfin comes back as an edit that changes the rule.
/// </para>
/// <para>
/// Reading only. What Jellyfin's edits mean on the way back is TvheadendDvr's question.
/// </para>
/// </remarks>
public static class JellyfinSeriesRuleMapper
{
    /// <summary>
    /// Describes every rule for Jellyfin.
    /// </summary>
    /// <param name="rules">The rules TVHeadend has announced.</param>
    /// <param name="serverOffset">How far the TVHeadend server's clock is from UTC.</param>
    /// <returns>The series timers.</returns>
    public static IReadOnlyList<SeriesTimerInfo> ToSeriesTimers(
        IReadOnlyList<SeriesRule> rules,
        TimeSpan serverOffset)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var today = DateTime.UtcNow.Date;
        var result = new List<SeriesTimerInfo>(rules.Count);
        foreach (var rule in rules)
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

        var anyTime = !SeriesRuleFields.IsTimeOfDay(rule.Start) || !SeriesRuleFields.IsTimeOfDay(rule.StartWindow);

        // The window the rule accepts, as an instant rather than as minutes on the server's clock.
        // Only the time of day means anything; the date is today's so that a client showing it
        // shows something sane.
        var start = anyTime ? onDateUtc : SeriesRuleFields.ToUtc(rule.Start!.Value, serverOffset, onDateUtc);
        var window = anyTime
            ? TimeSpan.Zero
            : TimeSpan.FromMinutes(SeriesRuleFields.WindowLength(rule.Start!.Value, rule.StartWindow!.Value));

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
            Days = [.. SeriesRuleFields.ToDays(rule.DaysOfWeek)],

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
            RecordNewOnly = rule.BroadcastType == SeriesRuleFields.BroadcastTypeNewOrUnknown,

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
}
