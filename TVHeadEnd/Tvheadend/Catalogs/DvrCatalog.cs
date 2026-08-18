using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Domain;
using HtspMessage = Tvheadend.Htsp.Protocol.HtspMessage;

namespace TVHeadEnd.Tvheadend.Catalogs;

/// <summary>
/// The DVR entries TVHeadend has announced: its timers and its recordings, which are the same
/// thing at different points in their life.
/// </summary>
public sealed class DvrCatalog
{
    private readonly ILogger<DvrCatalog> _logger;
    private readonly Dictionary<string, DvrEntry> _entries = [];
    private readonly object _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DvrCatalog"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public DvrCatalog(ILogger<DvrCatalog> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets how many entries are known.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Records an entry the server added.
    /// </summary>
    /// <param name="message">The <c>dvrEntryAdd</c> message.</param>
    public void Add(HtspMessage message)
    {
        var entry = DvrEntry.FromMessage(message);
        if (entry is null)
        {
            _logger.LogDebug("A TVHeadend DVR entry arrived without an identifier and was skipped");
            return;
        }

        lock (_gate)
        {
            _entries[entry.Id] = entry;
        }
    }

    /// <summary>
    /// Applies an update to an entry already known.
    /// </summary>
    /// <param name="message">The <c>dvrEntryUpdate</c> message.</param>
    public void Update(HtspMessage message)
    {
        var updated = DvrEntry.FromMessage(message);
        if (updated is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(updated.Id, out var existing))
            {
                _entries[updated.Id] = updated;
                return;
            }

            _entries[updated.Id] = DvrEntry.Merge(existing, updated, message);
        }
    }

    /// <summary>
    /// Forgets an entry the server removed.
    /// </summary>
    /// <param name="message">The <c>dvrEntryDelete</c> message.</param>
    public void Remove(HtspMessage message)
    {
        var entry = DvrEntry.FromMessage(message);
        if (entry is null)
        {
            return;
        }

        lock (_gate)
        {
            _entries.Remove(entry.Id);
        }
    }

    /// <summary>
    /// Discards everything, for a connection that is starting over.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <summary>
    /// Gets every entry currently known.
    /// </summary>
    /// <returns>The entries.</returns>
    public IReadOnlyList<DvrEntry> GetEntries()
    {
        lock (_gate)
        {
            return [.. _entries.Values];
        }
    }

    /// <summary>
    /// Gets the entries Jellyfin should show as timers.
    /// </summary>
    /// <returns>The timers.</returns>
    public IReadOnlyList<MediaBrowser.Controller.LiveTv.TimerInfo> GetTimers()
        => [.. GetEntries().Where(JellyfinDvrMapper.IsTimer).Select(JellyfinDvrMapper.ToTimer)];

    /// <summary>
    /// Gets the entries Jellyfin should show as recordings.
    /// </summary>
    /// <returns>The recordings.</returns>
    public IReadOnlyList<MyRecordingInfo> GetRecordings()
        => [.. GetEntries().Where(JellyfinDvrMapper.IsRecording).Select(JellyfinDvrMapper.ToRecording)];
}
