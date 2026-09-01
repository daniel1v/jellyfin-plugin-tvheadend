using System;
using System.Globalization;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;

namespace TVHeadEnd.Recordings
{
    /// <summary>
    /// Describes one recording as the item Jellyfin stores for it.
    /// </summary>
    /// <remarks>
    /// What a recording is called, what kind of thing it is, how long it runs, and when it last
    /// changed enough to be worth writing again. The last of those is the delicate one: Jellyfin
    /// rewrites a stored channel item only when the date it is given is later than the date it
    /// stored, so that date is a version marker as much as it is a fact, and it has to be both at
    /// once without ever reading the clock.
    /// </remarks>
    public static class RecordingItemMapper
    {
        /// <summary>
        /// How many times the shape of a recording's media sources has changed since that floor.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ChannelManager rewrites a stored channel item only when the item is new or when
        /// ChannelItemInfo.DateModified is strictly later than the date it stored. It compares no
        /// part of MediaSources, and DataVersion does not help either -- that only discards the
        /// cached listing response, not the items already in the database. So a changed media
        /// source has no way to reach the recordings somebody already has, and they keep whatever
        /// the previous version gave them.
        /// </para>
        /// <para>
        /// What reaches them is an offset added to the recording's own anchor, not a date of its
        /// own -- see <see cref="PublishedDateFor"/> for how the two combine. For an unchanged
        /// recording the published date is greater than the stored value exactly once per
        /// increment, so each upgrade rewrites every item once and then leaves it alone; and it
        /// stays true however long after the release the plugin is installed, because it is
        /// measured from the recording rather than the calendar.
        /// </para>
        /// <para>
        /// <strong>What it does not carry.</strong> Most of what a channel item says about the
        /// programme -- the name, the genres, the index numbers, the production year, the official
        /// and community ratings, the overview -- is assigned inside <c>if (isNew)</c> in
        /// ChannelManager.GetChannelItemEntity and is never re-read for an item that already
        /// exists. Forcing a save does not change that: it stores the item as it stands, and the
        /// fields were never reassigned. So raising this for a change that only adds item metadata
        /// would rewrite every recording once and deliver nothing, and it is deliberately not
        /// raised for one. The few fields ChannelManager does re-read -- SeriesName, ExternalId,
        /// RunTimeTicks, and the media sources -- reach existing recordings on their own, each
        /// forcing the save itself when it differs.
        /// </para>
        /// <para>
        /// Counted in whole days, because one increment has to clear how far short of its booking
        /// a recording fell as well as the seconds earlier versions stepped in. Raise it by one per
        /// change to the published <em>media source</em> shape.
        /// </para>
        /// </remarks>
        public const int SchemaRevision = 10;

        /// <summary>
        /// The floor every recording's modification date is lifted to.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It exists so that a recording TVHeadend has not touched in years still carries a date
        /// the schema revision can be counted from. It is not itself the revision, and it may only
        /// ever move <em>forward</em>: raising it raises the published date of every recording
        /// below it, which is what keeps those dates monotone; lowering it would drop them all at
        /// once and freeze every stored item.
        /// </para>
        /// <para>
        /// Moved once, from 2026-08-19, so that it sits above every date the schema-6 build
        /// published while it was briefly deployed. It carries nothing else: making a recording
        /// stopped early clear its own earlier publication is the anchor's job, not the floor's,
        /// and a floor could never have done it -- the shortfall is however long the recording had
        /// left to run, which no fixed date knows.
        /// </para>
        /// </remarks>
        private static readonly DateTime DateFloorUtc = new(2026, 8, 29, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// How long a recording runs, as one answer for the listing and the media source alike.
        /// </summary>
        /// <remarks>
        /// It used to be <c>EndDate - StartDate</c>, which is how long the recording was
        /// <em>scheduled</em> for. A recording stopped by hand was published at its planned length,
        /// so a client could seek into minutes that were never written. What is published now is
        /// measured from the file TVHeadend actually serves, and is absent -- not zero, not the
        /// plan -- while that file is still growing.
        /// </remarks>
        /// <param name="recording">The recording.</param>
        /// <returns>The runtime in ticks, or <see langword="null"/> where it is not knowable.</returns>
        public static long? Runtime(MyRecordingInfo recording) => recording.RunTimeTicks;

        /// <summary>
        /// What kind of thing a recording is published as.
        /// </summary>
        /// <remarks>
        /// Read twice and therefore stated once: the channel item is published with it, and the
        /// item identifier is derived from it. Two spellings of this would be two different items.
        /// </remarks>
        /// <param name="recording">The recording.</param>
        /// <returns>The content type.</returns>
        public static ChannelMediaContentType ContentTypeFor(MyRecordingInfo recording)
        {
            ArgumentNullException.ThrowIfNull(recording);

            if (recording.IsMovie)
            {
                return ChannelMediaContentType.Movie;
            }

            return recording.IsSeries ? ChannelMediaContentType.Episode : ChannelMediaContentType.Clip;
        }

        /// <summary>
        /// The modification date a recording is published with: a version marker, not a fact about
        /// the recording.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Jellyfin rewrites a stored channel item only when this is strictly greater than the
        /// value it holds, so it is the plugin's only way of reaching recordings somebody already
        /// has. That makes it a persistence version, and the two jobs cannot be done by one value:
        /// the recording's real activity time must be truthful -- see
        /// <see cref="MyRecordingInfo.DateLastUpdated"/> -- while this must only ever rise, even
        /// when the truth about a recording turns out to be earlier than what was published for it
        /// before.
        /// </para>
        /// <para>
        /// <c>max(real activity, floor) + revision days + state seconds</c>. The anchor follows
        /// what the recording actually did, so a real change always raises it and can never be
        /// masked. The scheduled stop is deliberately not in it: putting it there would make the
        /// anchor stop moving for every recording that ends below its booking, which is the whole
        /// population this correction is about.
        /// </para>
        /// <para>
        /// The revision step is a day, and that size is the point. Every earlier version published
        /// from the scheduled stop and stepped in seconds, so this has to clear however far short
        /// of its booking a recording fell -- an amount no fixed date can know in advance, but
        /// which is bounded by the length of the booking. A day covers any recording anybody
        /// makes. It is a constant offset rather than a fixed future date, so it lifts every
        /// recording equally and blocks nothing: a later real change still rises above it.
        /// </para>
        /// <para>
        /// The state step carries the one transition the anchor cannot. A server too old to send
        /// the file list gives the same anchor while recording and once completed, and that
        /// transition is exactly when the final runtime becomes known and has to be stored.
        /// </para>
        /// <para>
        /// Nothing here reads the clock or anything else that differs between runs: the same
        /// recording in the same state publishes the same value after a restart, on any machine.
        /// A value derived from the current time would be later than the stored date on every
        /// listing and rewrite every recording for ever.
        /// </para>
        /// </remarks>
        /// <param name="recording">The recording being published.</param>
        /// <returns>The date to publish.</returns>
        public static DateTime PublishedDateFor(MyRecordingInfo recording)
        {
            ArgumentNullException.ThrowIfNull(recording);

            // What the recording did, or failing that when it was due to begin -- an entry with no
            // file has done nothing, and its scheduled start is the only thing left to hang on.
            var anchor = recording.DateLastUpdated ?? recording.StartDate;

            if (anchor < DateFloorUtc)
            {
                anchor = DateFloorUtc;
            }

            return anchor
                .AddDays(SchemaRevision)
                .AddSeconds(ProgressOrdinal(recording.Status));
        }

        /// <summary>
        /// How far through its life the recording is, as a step the published date can carry.
        /// </summary>
        /// <remarks>
        /// One second apiece, well inside the minute the schema revision moves in, so the two
        /// cannot run into one another.
        /// </remarks>
        /// <param name="status">The recording's status.</param>
        /// <returns>The step.</returns>
        private static int ProgressOrdinal(MediaBrowser.Model.LiveTv.RecordingStatus status) => status switch
        {
            MediaBrowser.Model.LiveTv.RecordingStatus.InProgress => 1,
            MediaBrowser.Model.LiveTv.RecordingStatus.Completed => 2,
            _ => 0,
        };

        /// <summary>
        /// Gets what kind of item a recording from this sort of channel is published as.
        /// </summary>
        /// <remarks>
        /// A radio recording published as video is a concert behind a black screen. It happened
        /// because the recording was never told what its channel carried and took the enum's
        /// default, which is TV -- see TvheadendRecordings for where it is told.
        /// </remarks>
        /// <param name="channelType">What the channel it was recorded from carries.</param>
        /// <returns>The media type to publish.</returns>
        public static ChannelMediaType MediaTypeFor(MediaBrowser.Model.LiveTv.ChannelType channelType)
            => channelType == MediaBrowser.Model.LiveTv.ChannelType.Radio
                ? ChannelMediaType.Audio
                : ChannelMediaType.Video;

        /// <summary>
        /// Describes one recording as the item Jellyfin stores for it.
        /// </summary>
        /// <param name="item">The recording.</param>
        /// <param name="placeholder">
        /// The source the listing carries for it, which promises nothing and exists so that a
        /// listing never has to analyse what it lists.
        /// </param>
        /// <returns>The channel item.</returns>
        public static ChannelItemInfo BuildChannelItem(MyRecordingInfo item, MediaSourceInfo placeholder)
        {
            ArgumentNullException.ThrowIfNull(item);

            var channelItem = new ChannelItemInfo
            {
                // What to call it, and what it belongs to, are two questions. The episode title is
                // the better name where there is one; the series name follows from whether this is
                // an episode at all, which a broadcast can say by numbering it rather than naming
                // it. Tying the series name to the episode title left a numbered episode of
                // "Tatort" standing on its own, in a library that had every other episode of it.
                Name = string.IsNullOrEmpty(item.EpisodeTitle) ? item.Name : item.EpisodeTitle,
                SeriesName = item.IsSeries ? item.Name : null,

                IndexNumber = item.EpisodeNumber,
                ParentIndexNumber = item.SeasonNumber,
                ProductionYear = item.ProductionYear,

                OfficialRating = item.OfficialRating,
                CommunityRating = item.CommunityRating,
                ContentType = ContentTypeFor(item),
                Genres = [.. item.Genres],
                ImageUrl = item.ImageUrl,
                Id = item.Id,
                MediaType = MediaTypeFor(item.ChannelType),
                IsLiveStream = false,

                // A placeholder, carrying no streams at all. The listing must not analyse the
                // recordings it lists -- that is one range request and one FFprobe run per
                // recording, on every listing -- and describing them from guesswork instead is
                // worse than saying nothing: Jellyfin maps streams by their position in this
                // list, so invented entries send FFmpeg's "-map" arguments to the wrong tracks.
                // What the recording contains is answered by GetChannelItemMediaInfo when
                // playback is negotiated. The Placeholder type is what tells Jellyfin this is
                // not a description it should act on; GetPlaybackMediaSources checks for exactly
                // that before it would otherwise force a remote probe of its own.
                MediaSources = [placeholder],

                // Stated on the item, because the source deliberately carries nothing. Without a
                // duration Jellyfin treats the recording as a stream of unknown length, which is
                // exactly right while it is still being written and exactly wrong once it is not.
                RunTimeTicks = Runtime(item),
                PremiereDate = item.StartDate,
                DateCreated = item.StartDate,
                // Two reasons the stored item can be out of date: the recording itself changed,
                // and this plugin now describes it differently than the version that wrote the
                // stored copy. The date carries both -- TVHeadend's own, floored, plus one step
                // per description change since. Without the second an upgrade never reaches
                // recordings somebody already has.
                DateModified = PublishedDateFor(item),

                Overview = item.Overview,
                // People = item.People
                Etag = item.Status.ToString(),
            };

            return channelItem;
        }
    }
}
