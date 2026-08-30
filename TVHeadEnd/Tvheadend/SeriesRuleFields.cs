using System;
using System.Collections.Generic;

namespace TVHeadEnd.Tvheadend;

/// <summary>
/// What TVHeadend's own autorec fields mean.
/// </summary>
/// <remarks>
/// The server's vocabulary, kept where the server is: a day mask with Monday in the lowest bit,
/// a start stated as minutes from midnight on the server's own clock, and the sentinel that means
/// "at any time". Both directions read it -- the rules coming in and the edits going back out --
/// which is why it lives beside neither.
/// </remarks>
public static class SeriesRuleFields
{
    /// <summary>
    /// Every day of the week, which is how TVHeadend states a rule with no day restriction.
    /// </summary>
    public const int AllDaysOfWeek = 0x7F;

    /// <summary>
    /// No day at all, which matches nothing.
    /// </summary>
    public const int NoDaysOfWeek = 0;

    /// <summary>
    /// The start and start window TVHeadend uses for a rule that may record at any time.
    /// </summary>
    public const int AnyTime = -1;

    /// <summary>
    /// The broadcast type that records everything the rule matches.
    /// </summary>
    public const int BroadcastTypeAll = 0;

    /// <summary>
    /// The broadcast type that records only what the guide calls new or does not classify.
    /// </summary>
    public const int BroadcastTypeNewOrUnknown = 1;

    private const int MinutesPerDay = 24 * 60;

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
    public static DateTime ToUtc(int minutesFromMidnight, TimeSpan serverOffset, DateTime onDateUtc)
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
    public static int WindowLength(int start, int startWindow)
        => (((startWindow - start) % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;

    /// <summary>
    /// Reports whether a value is a time of day rather than TVHeadend's "any time".
    /// </summary>
    /// <param name="minutes">The value the server stated.</param>
    /// <returns>Whether it names a time.</returns>
    public static bool IsTimeOfDay(int? minutes) => minutes is >= 0 and < MinutesPerDay;

    /// <summary>
    /// Reads TVHeadend's day bit field as the days it names.
    /// </summary>
    /// <param name="daysOfWeek">The bit field, Monday in the lowest bit.</param>
    /// <returns>The days.</returns>
    public static IReadOnlyList<DayOfWeek> ToDays(int? daysOfWeek)
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
