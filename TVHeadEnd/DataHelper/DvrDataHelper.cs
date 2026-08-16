using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Domain;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.DataHelper
{
    /// <summary>
    /// Holds the DVR entries TVHeadend has announced.
    /// </summary>
    /// <remarks>
    /// A timer and a recording are the same entry on the server, at different points in its life,
    /// so they are read once into a <see cref="DvrEntry"/> and projected afterwards. Reading the
    /// same HTSP message twice into two unrelated shapes -- which is what this did -- meant every
    /// field was parsed in two places, and the two disagreed: a running recording counted as a
    /// recording in one and as nothing at all in the other.
    /// </remarks>
    public class DvrDataHelper
    {
        private readonly ILogger<DvrDataHelper> _logger;
        private readonly Dictionary<string, DvrEntry> _data;

        public DvrDataHelper(ILogger<DvrDataHelper> logger)
        {
            _logger = logger;
            _data = new Dictionary<string, DvrEntry>();
        }

        public void DvrEntryAdd(HTSMessage message)
        {
            var entry = DvrEntry.FromMessage(message);
            if (entry is null)
            {
                _logger.LogDebug("[TVHclient] DvrDataHelper: entry without an id - skipping");
                return;
            }

            lock (_data)
            {
                _data[entry.Id] = entry;
            }
        }

        /// <summary>
        /// Applies an update to an entry already known.
        /// </summary>
        /// <remarks>
        /// TVHeadend sends only the fields that changed, so the update is merged onto the message
        /// as it stood rather than replacing it -- a state change on its own would otherwise wipe
        /// the title, times and everything else.
        /// </remarks>
        /// <param name="message">The update message.</param>
        public void DvrEntryUpdate(HTSMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            var updated = DvrEntry.FromMessage(message);
            if (updated is null)
            {
                _logger.LogDebug("[TVHclient] DvrDataHelper: entry without an id - skipping");
                return;
            }

            lock (_data)
            {
                if (!_data.TryGetValue(updated.Id, out var existing))
                {
                    _logger.LogDebug("[TVHclient] DvrDataHelper.dvrEntryUpdate id not in database - skipping");
                    return;
                }

                _data[updated.Id] = Merge(existing, updated, message);
            }
        }

        public void DvrEntryDelete(HTSMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            var entry = DvrEntry.FromMessage(message);
            if (entry is null)
            {
                _logger.LogDebug("[TVHclient] DvrDataHelper: entry without an id - skipping");
                return;
            }

            lock (_data)
            {
                _data.Remove(entry.Id);
            }
        }

        /// <summary>
        /// Gets every entry currently known, in no particular order.
        /// </summary>
        /// <returns>The entries.</returns>
        public IReadOnlyList<DvrEntry> GetEntries()
        {
            lock (_data)
            {
                return _data.Values.ToList();
            }
        }

        public Task<IEnumerable<MyRecordingInfo>> BuildDvrInfos(CancellationToken cancellationToken)
        {
            return Task.Run<IEnumerable<MyRecordingInfo>>(
                () => Project(JellyfinDvrMapper.IsRecording, JellyfinDvrMapper.ToRecording, cancellationToken),
                cancellationToken);
        }

        public Task<IEnumerable<TimerInfo>> BuildPendingTimersInfos(CancellationToken cancellationToken)
        {
            return Task.Run<IEnumerable<TimerInfo>>(
                () => Project(JellyfinDvrMapper.IsTimer, JellyfinDvrMapper.ToTimer, cancellationToken),
                cancellationToken);
        }

        /// <summary>
        /// Carries the fields an update actually mentioned over the entry as it stood.
        /// </summary>
        private static DvrEntry Merge(DvrEntry existing, DvrEntry updated, HTSMessage message)
        {
            return existing with
            {
                State = message.ContainsField("state") ? updated.State : existing.State,
                ChannelId = message.ContainsField("channel") ? updated.ChannelId : existing.ChannelId,
                EventId = message.ContainsField("eventId") ? updated.EventId : existing.EventId,
                AutoRecId = message.ContainsField("autorecId") ? updated.AutoRecId : existing.AutoRecId,
                Title = message.ContainsField("title") ? updated.Title : existing.Title,
                Subtitle = message.ContainsField("subtitle") ? updated.Subtitle : existing.Subtitle,
                Description = message.ContainsField("description")
                    || message.ContainsField("summary")
                    || message.ContainsField("subtitle")
                        ? updated.Description
                        : existing.Description,
                StartUtc = message.ContainsField("start") ? updated.StartUtc : existing.StartUtc,
                StopUtc = message.ContainsField("stop") ? updated.StopUtc : existing.StopUtc,
                PrePadding = message.ContainsField("startExtra") ? updated.PrePadding : existing.PrePadding,
                PostPadding = message.ContainsField("stopExtra") ? updated.PostPadding : existing.PostPadding,
                Priority = message.ContainsField("priority") ? updated.Priority : existing.Priority,
                FilePath = message.ContainsField("path") ? updated.FilePath : existing.FilePath,
                Url = message.ContainsField("url") ? updated.Url : existing.Url,
                Error = message.ContainsField("error") ? updated.Error : existing.Error,
            };
        }

        private List<T> Project<T>(
            Func<DvrEntry, bool> belongs,
            Func<DvrEntry, T> describe,
            CancellationToken cancellationToken)
        {
            var result = new List<T>();
            foreach (var entry in GetEntries())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug("[TVHclient] DvrDataHelper: call cancelled - returning partial list");
                    return result;
                }

                if (belongs(entry))
                {
                    result.Add(describe(entry));
                }
            }

            return result;
        }
    }
}
