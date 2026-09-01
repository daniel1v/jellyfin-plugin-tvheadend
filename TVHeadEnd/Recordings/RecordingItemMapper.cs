using System;
using System.Globalization;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using TVHeadEnd.Compatibility.Jellyfin12;

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
                DateModified = RecordingPublicationVersion.PublishedDateFor(item),

                Overview = item.Overview,
                // People = item.People
                Etag = item.Status.ToString(),
            };

            return channelItem;
        }
    }
}
