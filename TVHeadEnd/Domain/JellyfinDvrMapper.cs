using System;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

namespace TVHeadEnd.Domain
{
    /// <summary>
    /// Projects a <see cref="DvrEntry"/> into the two shapes Jellyfin asks for.
    /// </summary>
    /// <remarks>
    /// Jellyfin wants timers from ILiveTvService and recordings from IChannel, and has no notion
    /// that they are the same thing on the server. These are the only place that split exists.
    /// </remarks>
    public static class JellyfinDvrMapper
    {
        /// <summary>
        /// Gets a value indicating whether the entry belongs in Jellyfin's timer list.
        /// </summary>
        /// <remarks>
        /// Everything that has not finished, a recording in progress included. Jellyfin's model
        /// has RecordingStatus.InProgress for exactly that, and it is what a timer list is for:
        /// a recording that has started is still the thing a viewer stops, and stopping it is a
        /// timer operation. Listing only what had not started left a running recording with no
        /// entry anywhere that could be cancelled.
        /// </remarks>
        /// <param name="entry">The DVR entry.</param>
        /// <returns>Whether it belongs in the timer list.</returns>
        public static bool IsTimer(DvrEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            return entry.State is DvrState.Scheduled or DvrState.Recording;
        }

        /// <summary>
        /// Gets a value indicating whether the entry belongs in Jellyfin's recording list.
        /// </summary>
        /// <remarks>
        /// Everything with a file behind it. A recording in progress has one and plays -- that is
        /// what continuing to watch what is being recorded means -- and a completed one has one
        /// unless the server says it has gone.
        /// <para>
        /// Missed and invalid entries are not recordings. TVHeadend keeps them so that something
        /// says why nothing was recorded, but they have no file, and offering them put items in
        /// the library that could only fail when opened.
        /// </para>
        /// </remarks>
        /// <param name="entry">The DVR entry.</param>
        /// <returns>Whether it belongs in the recording list.</returns>
        public static bool IsRecording(DvrEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            if (entry.FileIsMissing)
            {
                return false;
            }

            return entry.State is DvrState.Recording or DvrState.Completed;
        }

        /// <summary>
        /// Describes the entry as a Jellyfin timer.
        /// </summary>
        /// <param name="entry">The DVR entry.</param>
        /// <returns>The timer.</returns>
        public static TimerInfo ToTimer(DvrEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            return new TimerInfo
            {
                Id = entry.Id,
                ChannelId = entry.ChannelId,
                ProgramId = entry.EventId,
                SeriesTimerId = entry.AutoRecId,
                Name = entry.Title,
                Overview = entry.Description,
                StartDate = entry.StartUtc,
                EndDate = entry.StopUtc,
                Status = ToRecordingStatus(entry.State),
                Priority = entry.Priority ?? 0,
                PrePaddingSeconds = (int)entry.PrePadding.TotalSeconds,
                PostPaddingSeconds = (int)entry.PostPadding.TotalSeconds,
                IsPrePaddingRequired = entry.PrePadding > TimeSpan.Zero,
                IsPostPaddingRequired = entry.PostPadding > TimeSpan.Zero,
            };
        }

        /// <summary>
        /// Describes the entry as a Jellyfin recording.
        /// </summary>
        /// <param name="entry">The DVR entry.</param>
        /// <returns>The recording.</returns>
        public static MyRecordingInfo ToRecording(DvrEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            var recording = new MyRecordingInfo
            {
                Id = entry.Id,
                ChannelId = entry.ChannelId,
                ProgramId = entry.EventId,
                SeriesTimerId = entry.AutoRecId,
                Name = entry.Title,
                Overview = entry.Description,
                StartDate = entry.StartUtc,
                EndDate = entry.StopUtc,
                Status = ToRecordingStatus(entry.State),

                // What the server said, not an address yet -- see MyRecordingInfo.ImageReference.
                // This used to be a flat "HasImage = false", which was a claim rather than an
                // answer: TVHeadend copies the artwork onto the DVR entry when it schedules the
                // recording, and it was being thrown away unread.
                ImageReference = entry.Image,
                FanartReference = entry.FanartImage,
                HasImage = !string.IsNullOrEmpty(entry.Image),

                // When this recording last became something different. Jellyfin re-saves a
                // channel item -- and with it the description of what it contains -- only when
                // the item is new or something it compares has changed, and the modification
                // date is the only one of those a plugin controls. Left unset, as it was, it
                // stays at DateTime.MinValue, is never greater than what Jellyfin stored, and
                // the description of an existing recording can never be corrected.
                DateLastUpdated = entry.StopUtc > entry.StartUtc ? entry.StopUtc : entry.StartUtc,

                // Left empty on purpose: a path here makes Jellyfin bypass this plugin and try to
                // open a file that lives on the TVHeadend server, not on its own.
                Path = string.Empty,
                Url = entry.Url,
            };

            if (!string.IsNullOrEmpty(entry.Subtitle))
            {
                recording.EpisodeTitle = entry.Subtitle;
                recording.IsSeries = true;
            }

            return recording;
        }

        private static RecordingStatus ToRecordingStatus(DvrState state) => state switch
        {
            DvrState.Scheduled => RecordingStatus.New,
            DvrState.Recording => RecordingStatus.InProgress,
            DvrState.Completed => RecordingStatus.Completed,
            DvrState.Missed => RecordingStatus.Error,
            DvrState.Invalid => RecordingStatus.Error,
            _ => RecordingStatus.Error,
        };
    }
}
