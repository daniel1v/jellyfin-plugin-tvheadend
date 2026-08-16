using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.DataHelper
{
    public class DvrDataHelper
    {
        private readonly ILogger<DvrDataHelper> _logger;
        private readonly Dictionary<string, HTSMessage> _data;

        private readonly DateTime _initialDateTimeUTC = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private long _lastChangeTicks;

        public DvrDataHelper(ILogger<DvrDataHelper> logger)
        {
            _logger = logger;
            _data = new Dictionary<string, HTSMessage>();
        }

        /// <summary>
        /// Gets a stamp that moves whenever the set of recordings changes: one appears, one goes,
        /// or one finishes. Jellyfin caches the contents of a channel for three hours and only
        /// asks again when the channel's data version differs, so without this a recording made
        /// on the TVHeadend server would surface hours after it was made. Progress reports during
        /// an ongoing recording deliberately leave it alone; they would invalidate the cache
        /// every few seconds for a listing that has not changed.
        /// </summary>
        public long LastChangeTicks => Interlocked.Read(ref _lastChangeTicks);

        public void DvrEntryAdd(HTSMessage message)
        {
            string? id = message.GetString("id");
            if (id == null)
            {
                _logger.LogDebug("[TVHclient] DvrDataHelper: entry without an id - skipping");
                return;
            }

            lock (_data)
            {
                if (_data.ContainsKey(id))
                {
                    _logger.LogDebug("[TVHclient] DvrDataHelper.dvrEntryAdd id already in database - skipping");
                    return;
                }

                _data.Add(id, message);
            }

            MarkChanged();
        }

        public void DvrEntryUpdate(HTSMessage message)
        {
            string? id = message.GetString("id");
            if (id == null)
            {
                _logger.LogDebug("[TVHclient] DvrDataHelper: entry without an id - skipping");
                return;
            }

            bool stateChanged;
            lock (_data)
            {
                if (!_data.TryGetValue(id, out HTSMessage? oldMessage) || oldMessage == null)
                {
                    _logger.LogDebug("[TVHclient] DvrDataHelper.dvrEntryUpdate id not in database - skipping");
                    return;
                }

                var previousState = oldMessage.GetString("state");

                foreach (KeyValuePair<string, object> entry in message)
                {
                    if (oldMessage.ContainsField(entry.Key))
                    {
                        oldMessage.RemoveField(entry.Key);
                    }

                    oldMessage.PutField(entry.Key, entry.Value);
                }

                stateChanged = !string.Equals(previousState, oldMessage.GetString("state"), StringComparison.Ordinal);
            }

            if (stateChanged)
            {
                MarkChanged();
            }
        }

        public void DvrEntryDelete(HTSMessage message)
        {
            string? id = message.GetString("id");
            if (id == null)
            {
                _logger.LogDebug("[TVHclient] DvrDataHelper: entry without an id - skipping");
                return;
            }

            bool removed;
            lock (_data)
            {
                removed = _data.Remove(id);
            }

            if (removed)
            {
                MarkChanged();
            }
        }

        private void MarkChanged() => Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);

        public Task<IEnumerable<MyRecordingInfo>> BuildDvrInfos(CancellationToken cancellationToken)
        {
            return Task.Run<IEnumerable<MyRecordingInfo>>(() =>
            {
                lock (_data)
                {
                    List<MyRecordingInfo> result = new List<MyRecordingInfo>();
                    foreach (KeyValuePair<string, HTSMessage> entry in _data)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogDebug("[TVHclient] DvrDataHelper.buildDvrInfos: call cancelled - returning partial list");
                            return result;
                        }

                        HTSMessage m = entry.Value;
                        MyRecordingInfo ri = new MyRecordingInfo();

                        try
                        {
                            if (m.ContainsField("error"))
                            {
                                // When TVHeadend recordings are removed, their info can
                                // still be kept around with a status of "completed".
                                // The only way to identify them is from the error string
                                // which is set to "File missing". Use that to not show
                                // non-existing deleted recordings.
                                if (m.GetString("error")?.Contains("missing", StringComparison.Ordinal) == true)
                                {
                                    continue;
                                }
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("id"))
                            {
                                ri.Id = string.Empty + m.GetInt("id");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("path"))
                            {
                                ri.Path = string.Empty + m.GetString("path");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("url"))
                            {
                                ri.Url = string.Empty + m.GetString("url");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("channel"))
                            {
                                ri.ChannelId = string.Empty + m.GetInt("channel");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("start"))
                            {
                                long unixUtc = m.GetLong("start");
                                ri.StartDate = _initialDateTimeUTC.AddSeconds(unixUtc).ToUniversalTime();
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("stop"))
                            {
                                long unixUtc = m.GetLong("stop");
                                ri.EndDate = _initialDateTimeUTC.AddSeconds(unixUtc).ToUniversalTime();
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("title"))
                            {
                                ri.Name = m.GetString("title");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            // Up to HTSP v31 "description" is a collapsed fallback of
                            // description/summary/subtitle; from v32 on the three fields are
                            // independent, so fall back to keep an overview in both layouts.
                            ri.Overview = m.GetString("description", null)
                                ?? m.GetString("summary", null)
                                ?? m.GetString("subtitle", null);
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("subtitle"))
                            {
                                ri.EpisodeTitle = m.GetString("subtitle");
                                ri.IsSeries = true;
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        ri.HasImage = false;
                        // public string ImagePath { get; set; }
                        // public string ImageUrl { get; set; }

                        try
                        {
                            if (m.ContainsField("state"))
                            {
                                string? state = m.GetString("state");
                                switch (state)
                                {
                                    case "completed":
                                        ri.Status = RecordingStatus.Completed;
                                        break;
                                    case "scheduled":
                                        ri.Status = RecordingStatus.New;
                                        continue;
                                    // break;
                                    case "missed":
                                        ri.Status = RecordingStatus.Error;
                                        break;
                                    case "recording":
                                        ri.Status = RecordingStatus.InProgress;
                                        break;

                                    default:
                                        _logger.LogCritical("[TVHclient] DvrDataHelper.buildDvrInfos: state '{State}' not handled", state);
                                        continue;
                                        // break;
                                }
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        // Path must not be set to force emby use of the LiveTvService methods!!!!
                        // if (m.ContainsField("path"))
                        // {
                        //    ri.Path = m.GetString("path");
                        // }

                        try
                        {
                            if (m.ContainsField("autorecId"))
                            {
                                ri.SeriesTimerId = m.GetString("autorecId");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("eventId"))
                            {
                                ri.ProgramId = string.Empty + m.GetInt("eventId");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        /*
                                public ProgramAudio? Audio { get; set; }
                                public ChannelType ChannelType { get; set; }
                                public float? CommunityRating { get; set; }
                                public List<string> Genres { get; set; }
                                public bool? IsHD { get; set; }
                                public bool IsKids { get; set; }
                                public bool IsLive { get; set; }
                                public bool IsMovie { get; set; }
                                public bool IsNews { get; set; }
                                public bool IsPremiere { get; set; }
                                public bool IsRepeat { get; set; }
                                public bool IsSeries { get; set; }
                                public bool IsSports { get; set; }
                                public string OfficialRating { get; set; }
                                public DateTime? OriginalAirDate { get; set; }
                                public string Url { get; set; }
                         */

                        result.Add(ri);
                    }

                    return result;
                }
            });
        }

        public Task<IEnumerable<TimerInfo>> BuildPendingTimersInfos(CancellationToken cancellationToken)
        {
            return Task.Run<IEnumerable<TimerInfo>>(() =>
            {
                lock (_data)
                {
                    List<TimerInfo> result = new List<TimerInfo>();
                    foreach (KeyValuePair<string, HTSMessage> entry in _data)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogDebug("[TVHclient] DvrDataHelper.buildDvrInfos: call cancelled - returning partial list");
                            return result;
                        }

                        HTSMessage m = entry.Value;
                        TimerInfo ti = new TimerInfo();

                        try
                        {
                            if (m.ContainsField("id"))
                            {
                                ti.Id = string.Empty + m.GetInt("id");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("channel"))
                            {
                                ti.ChannelId = string.Empty + m.GetInt("channel");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("start"))
                            {
                                long unixUtc = m.GetLong("start");
                                ti.StartDate = _initialDateTimeUTC.AddSeconds(unixUtc).ToUniversalTime();
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("stop"))
                            {
                                long unixUtc = m.GetLong("stop");
                                ti.EndDate = _initialDateTimeUTC.AddSeconds(unixUtc).ToUniversalTime();
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("title"))
                            {
                                ti.Name = m.GetString("title");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            ti.Overview = m.GetString("description", null)
                                ?? m.GetString("summary", null)
                                ?? m.GetString("subtitle", null);
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("state"))
                            {
                                string? state = m.GetString("state");
                                switch (state)
                                {
                                    case "scheduled":
                                        ti.Status = RecordingStatus.New;
                                        break;
                                    default:
                                        // only scheduled timers need to be delivered
                                        continue;
                                }
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("startExtra"))
                            {
                                ti.PrePaddingSeconds = (int)m.GetLong("startExtra") * 60;
                                ti.IsPrePaddingRequired = true;
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("stopExtra"))
                            {
                                ti.PostPaddingSeconds = (int)m.GetLong("stopExtra") * 60;
                                ti.IsPostPaddingRequired = true;
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("priority"))
                            {
                                ti.Priority = m.GetInt("priority");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("autorecId"))
                            {
                                ti.SeriesTimerId = m.GetString("autorecId");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.ContainsField("eventId"))
                            {
                                ti.ProgramId = string.Empty + m.GetInt("eventId");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        result.Add(ti);
                    }

                    return result;
                }
            });
        }
    }
}
