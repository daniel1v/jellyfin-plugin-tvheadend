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

    private long _revision;

    /// <summary>
    /// Initializes a new instance of the <see cref="DvrCatalog"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public DvrCatalog(ILogger<DvrCatalog> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets a number that changes whenever the catalog does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What TVHeadend has actually confirmed, which is the only thing worth caching against. It
    /// used to be a timestamp set when the plugin sent a command, and that answers a different
    /// question: a recording created in TVHeadend's own web interface changed nothing, a recording
    /// starting or finishing changed nothing, and a command whose reply arrived before its
    /// <c>dvrEntryAdd</c> moved it too early -- so Jellyfin could refresh against the catalog as
    /// it was before the change and then never refresh again.
    /// </para>
    /// <para>
    /// A counter rather than a clock, because two changes inside the same tick are still two
    /// changes.
    /// </para>
    /// </remarks>
    public long Revision
    {
        get
        {
            lock (_gate)
            {
                return _revision;
            }
        }
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
            _revision++;
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
                // An entry this connection has not been told about. Announced or not, it is new
                // here, so it counts.
                _entries[updated.Id] = updated;
                _revision++;
                return;
            }

            var merged = DvrEntry.Merge(existing, updated, message);

            // Only when the entry actually says something different. TVHeadend sends an update
            // for a running recording every few seconds carrying only its statistics -- bytes
            // written, disk space, errors counted -- none of which this plugin reads, and all of
            // which used to count as a change. Since the recordings channel started supplying a
            // cache key built on this number, that meant the whole listing was discarded and
            // rebuilt every few seconds for as long as anything was recording.
            if (merged.HasSameContentAs(existing))
            {
                return;
            }

            _entries[updated.Id] = merged;
            _revision++;
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
            if (_entries.Remove(entry.Id))
            {
                _revision++;
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
            // Counted as a change: a reconnection replaces the whole picture, and anything cached
            // against the picture before it has to be built again.
            _entries.Clear();
            _revision++;
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
    /// Gets one entry as the server last announced it.
    /// </summary>
    /// <remarks>
    /// The only state worth deciding against: what an operation should do to an entry depends on
    /// where that entry has got to, and the catalog is the one place that answer comes from the
    /// server rather than from what the plugin last asked for.
    /// </remarks>
    /// <param name="id">The TVHeadend entry identifier.</param>
    /// <returns>The entry, or <see langword="null"/> if the server has not announced one.</returns>
    public DvrEntry? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry) ? entry : null;
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
