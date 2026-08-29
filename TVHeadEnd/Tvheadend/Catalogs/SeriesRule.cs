namespace TVHeadEnd.Tvheadend.Catalogs;

/// <summary>
/// One autorec entry, as TVHeadend stated it.
/// </summary>
/// <param name="Id">The TVHeadend identifier.</param>
/// <param name="Title">The title the rule matches on, which TVHeadend reads as a regular expression.</param>
/// <param name="SeriesLink">The series the rule is bound to, where it is bound to one.</param>
/// <param name="ChannelId">The channel the rule is limited to, if any.</param>
/// <param name="DaysOfWeek">The days the rule applies on, Monday in the lowest bit.</param>
/// <param name="Start">The first minute of the start window, on the server's clock, or -1.</param>
/// <param name="StartWindow">The last minute of the start window, on the server's clock, or -1.</param>
/// <param name="RetentionDays">How long a finished recording is kept. Not a property of the rule's life.</param>
/// <param name="PrePaddingMinutes">The padding before a recording.</param>
/// <param name="PostPaddingMinutes">The padding after a recording.</param>
/// <param name="Priority">The recording priority.</param>
/// <param name="BroadcastType">Which broadcasts the rule accepts.</param>
/// <param name="MaxCount">How many recordings the rule keeps, or 0 for unlimited.</param>
/// <param name="Description">The rule's description.</param>
public sealed record SeriesRule(
    string Id,
    string? Title,
    string? SeriesLink,
    string? ChannelId,
    int? DaysOfWeek,
    int? Start,
    int? StartWindow,
    int? RetentionDays,
    long? PrePaddingMinutes,
    long? PostPaddingMinutes,
    int? Priority,
    int? BroadcastType,
    int? MaxCount,
    string? Description);
