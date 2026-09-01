using System;
using System.Collections.ObjectModel;
using MediaBrowser.Model.LiveTv;

namespace TVHeadEnd
{
    public class MyRecordingInfo
    {
        /// <summary>
        /// Gets or sets id of the recording.
        /// </summary>
        /// <value>The recording identifier.</value>
        public string? Id { get; set; }

        /// <summary>
        /// Gets or sets the series timer identifier.
        /// </summary>
        /// <value>The series timer identifier.</value>
        public string? SeriesTimerId { get; set; }

        /// <summary>
        /// Gets or sets channelId of the recording.
        /// </summary>
        public string? ChannelId { get; set; }

        /// <summary>
        /// Gets or sets the type of the channel.
        /// </summary>
        /// <value>The type of the channel.</value>
        public ChannelType ChannelType { get; set; }

        /// <summary>
        /// Gets or sets name of the recording.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the path.
        /// </summary>
        /// <value>The path.</value>
        public string? Path { get; set; }

        /// <summary>
        /// Gets or sets the URL.
        /// </summary>
        /// <value>The URL.</value>
        public string? Url { get; set; }

        /// <summary>
        /// Gets or sets the overview.
        /// </summary>
        /// <value>The overview.</value>
        public string? Overview { get; set; }

        /// <summary>
        /// Gets or sets the start date of the recording, in UTC.
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// Gets or sets the end date of the recording, in UTC.
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// Gets or sets the program identifier.
        /// </summary>
        /// <value>The program identifier.</value>
        public string? ProgramId { get; set; }

        /// <summary>
        /// Gets or sets the status.
        /// </summary>
        /// <value>The status.</value>
        public RecordingStatus Status { get; set; }

        /// <summary>
        /// Gets the genres of the program.
        /// </summary>
        /// <value>The genres.</value>
        public Collection<string> Genres { get; } = new Collection<string>();

        /// <summary>
        /// Gets or sets a value indicating whether this instance is repeat.
        /// </summary>
        /// <value><c>true</c> if this instance is repeat; otherwise, <c>false</c>.</value>
        public bool IsRepeat { get; set; }

        /// <summary>
        /// Gets or sets the episode title.
        /// </summary>
        /// <value>The episode title.</value>
        public string? EpisodeTitle { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this instance is hd.
        /// </summary>
        /// <value><c>true</c> if this instance is hd; otherwise, <c>false</c>.</value>
        public bool? IsHD { get; set; }

        /// <summary>
        /// Gets or sets the audio.
        /// </summary>
        /// <value>The audio.</value>
        public ProgramAudio? Audio { get; set; }

        /// <summary>
        /// Gets or sets the original air date.
        /// </summary>
        /// <value>The original air date.</value>
        public DateTime? OriginalAirDate { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this instance is movie.
        /// </summary>
        /// <value><c>true</c> if this instance is movie; otherwise, <c>false</c>.</value>
        public bool IsMovie { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this instance is sports.
        /// </summary>
        /// <value><c>true</c> if this instance is sports; otherwise, <c>false</c>.</value>
        public bool IsSports { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this instance is series.
        /// </summary>
        /// <value><c>true</c> if this instance is series; otherwise, <c>false</c>.</value>
        public bool IsSeries { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this instance is live.
        /// </summary>
        /// <value><c>true</c> if this instance is live; otherwise, <c>false</c>.</value>
        public bool IsLive { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this instance is news.
        /// </summary>
        /// <value><c>true</c> if this instance is news; otherwise, <c>false</c>.</value>
        public bool IsNews { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this instance is kids.
        /// </summary>
        /// <value><c>true</c> if this instance is kids; otherwise, <c>false</c>.</value>
        public bool IsKids { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this instance is premiere.
        /// </summary>
        /// <value><c>true</c> if this instance is premiere; otherwise, <c>false</c>.</value>
        public bool IsPremiere { get; set; }

        /// <summary>
        /// Gets or sets the official rating.
        /// </summary>
        /// <value>The official rating.</value>
        public string? OfficialRating { get; set; }

        /// <summary>
        /// Gets or sets the community rating.
        /// </summary>
        /// <value>The community rating.</value>
        public float? CommunityRating { get; set; }

        /// <summary>
        /// Gets or sets which season of its series the recording belongs to.
        /// </summary>
        /// <remarks>
        /// What the broadcast said, and nothing where it said nothing. Together with
        /// <see cref="EpisodeNumber"/> this is also what makes a recording a series entry when the
        /// broadcast carried no episode title -- see <see cref="IsSeries"/>.
        /// </remarks>
        public int? SeasonNumber { get; set; }

        /// <summary>
        /// Gets or sets which episode of its season the recording is.
        /// </summary>
        public int? EpisodeNumber { get; set; }

        /// <summary>
        /// Gets or sets the year the recorded programme was made.
        /// </summary>
        /// <remarks>
        /// The broadcast's own copyright year. Never the year it was recorded in, which is a
        /// different fact about a different thing.
        /// </remarks>
        public int? ProductionYear { get; set; }

        /// <summary>
        /// Gets or sets supply the image path if it can be accessed directly from the file system.
        /// </summary>
        /// <value>The image path.</value>
        public string? ImagePath { get; set; }

        /// <summary>
        /// Gets or sets supply the image url if it can be downloaded.
        /// </summary>
        /// <value>The image URL.</value>
        public string? ImageUrl { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this instance has image.
        /// </summary>
        /// <value><c>null</c> if [has image] contains no value, <c>true</c> if [has image]; otherwise, <c>false</c>.</value>
        public bool? HasImage { get; set; }

        /// <summary>
        /// Gets or sets the artwork reference exactly as TVHeadend stated it.
        /// </summary>
        /// <remarks>
        /// Kept apart from <see cref="ImageUrl"/> because the two are different things: this is
        /// what the server said, and that is an address a client can fetch. Turning one into the
        /// other needs this server's own address and its secret, which the mapper that builds this
        /// record has neither of.
        /// </remarks>
        public string? ImageReference { get; set; }

        /// <summary>
        /// Gets or sets the backdrop reference exactly as TVHeadend stated it.
        /// </summary>
        public string? FanartReference { get; set; }

        /// <summary>
        /// Gets or sets how long the recording actually runs, in ticks.
        /// </summary>
        /// <remarks>
        /// Measured from the file TVHeadend serves, not from the times the recording was scheduled
        /// for -- see <see cref="Core.Dvr.DvrEntry.PlayableFile"/>. <see langword="null"/> means the
        /// length is not knowable yet, which is what a recording still being written is, and is
        /// deliberately not the same as zero.
        /// <para>
        /// Stated once and read by both the listing and the media source, because two independent
        /// answers to how long a recording is are two answers that can disagree.
        /// </para>
        /// </remarks>
        public long? RunTimeTicks { get; set; }

        /// <summary>
        /// Gets or sets the last moment the recording itself did something.
        /// </summary>
        /// <remarks>
        /// Read from the file TVHeadend wrote: when it was closed, or failing that when it was
        /// opened. <see langword="null"/> for a recording with no file, because then nothing has
        /// happened yet. Nothing scheduled goes into it, so it never states a time that has not
        /// come -- which the scheduled stop, and with pre-padding even the scheduled start, can be.
        /// <para>
        /// This is not the value published as <c>ChannelItemInfo.DateModified</c>; that is a
        /// version marker with a different job, built in
        /// <see cref="Recordings.RecordingItemMapper.PublishedDateFor"/>.
        /// </para>
        /// </remarks>
        public DateTime? DateLastUpdated { get; set; }
    }
}
