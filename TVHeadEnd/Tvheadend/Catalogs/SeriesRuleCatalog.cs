using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Gets every rule the server has announced, as it stated them.
    /// </summary>
    /// <returns>A snapshot, which the caller may hold and read at leisure.</returns>
    public IReadOnlyList<SeriesRule> GetRules()
    {
        lock (_gate)
        {
            return [.. _rules.Values];
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
}
