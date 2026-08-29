using System;
using System.Collections.Generic;
using Tvheadend.Htsp.Protocol;

namespace TVHeadEnd.Tvheadend.Catalogs;

/// <summary>
/// The channel tags TVHeadend has announced over the metadata feed.
/// </summary>
/// <remarks>
/// <para>
/// Held apart from the channels that reference them, which is how TVHeadend sends them and the
/// only arrangement in which a rename behaves. A tag's name lives here once; a channel keeps only
/// the number it was given. So when somebody renames "TV channels" on the server, the next listing
/// says the new name for all hundred and thirty channels, and not one channel record was rewritten
/// to make that true.
/// </para>
/// <para>
/// Nothing about a channel is copied in here, and nothing here knows what references it. The
/// resolution happens where the two are put together, which is the mapping that builds what
/// Jellyfin is told.
/// </para>
/// </remarks>
public sealed class ChannelTagCatalog
{
    private readonly Dictionary<int, ChannelTag> _tags = [];
    private readonly object _gate = new();

    /// <summary>
    /// Gets how many tags are known.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _tags.Count;
            }
        }
    }

    /// <summary>
    /// Records a tag the server added or changed.
    /// </summary>
    /// <remarks>
    /// An update mentions only what changed, so it is merged onto the tag as it stood. TVHeadend
    /// sends a second round of <c>tagUpdate</c> during the initial sync carrying the member list
    /// and nothing else -- taking the name from that unconditionally would blank every tag name
    /// moments after learning it.
    /// </remarks>
    /// <param name="message">The <c>tagAdd</c> or <c>tagUpdate</c> message.</param>
    public void AddOrUpdate(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.GetInt32("tagId") is not { } id)
        {
            return;
        }

        lock (_gate)
        {
            _tags.TryGetValue(id, out var existing);
            _tags[id] = new ChannelTag(id, message.GetString("tagName") ?? existing?.Name);
        }
    }

    /// <summary>
    /// Forgets a tag the server removed.
    /// </summary>
    /// <param name="message">The <c>tagDelete</c> message.</param>
    public void Remove(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.GetInt32("tagId") is { } id)
        {
            lock (_gate)
            {
                _tags.Remove(id);
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
            _tags.Clear();
        }
    }

    /// <summary>
    /// Names the tags a channel references, in the order it references them.
    /// </summary>
    /// <remarks>
    /// A tag this does not know is left out rather than named after its number: the number is the
    /// server's bookkeeping and means nothing to a viewer. Tags with no name are left out for the
    /// same reason. Duplicates are dropped without regard to case, because two tags that read the
    /// same are one label as far as anybody looking at it is concerned.
    /// </remarks>
    /// <param name="tagIds">The tag identifiers a channel carries.</param>
    /// <returns>The names, which may be empty.</returns>
    public IReadOnlyList<string> Resolve(IEnumerable<int> tagIds)
    {
        ArgumentNullException.ThrowIfNull(tagIds);

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            foreach (var id in tagIds)
            {
                if (!_tags.TryGetValue(id, out var tag))
                {
                    continue;
                }

                var name = tag.Name?.Trim();
                if (string.IsNullOrEmpty(name) || !seen.Add(name))
                {
                    continue;
                }

                names.Add(name);
            }
        }

        return names;
    }
}
