using MediaBrowser.Model.LiveTv;
using TVHeadEnd;
using TVHeadEnd.Recordings;
using Xunit;

namespace TVHeadEnd.Tests.Recordings;

/// <summary>
/// What the item Jellyfin stores for a recording says about it.
/// </summary>
/// <remarks>
/// <para>
/// Two of these fields decide how a recording is filed rather than merely how it reads. The series
/// name is what puts every episode of a programme together; the season and episode numbers are
/// what order them once they are.
/// </para>
/// <para>
/// Jellyfin's channel manager writes most of this only when it first stores an item -- see
/// <c>GetChannelItemEntity</c>, where the whole block is inside <c>if (isNew)</c>. The series name
/// is one of the few it does re-read, which is why that one reaches recordings somebody already
/// has and the numbers do not.
/// </para>
/// </remarks>
public class RecordingChannelItemTests
{
    [Fact]
    public void ASeriesIsNamedEvenWhenTheEpisodeIsNot()
    {
        // The case that used to fall through. A German broadcast numbers its episodes far more
        // often than it names them, and tying the series name to the episode title left a numbered
        // episode standing alone in a library that had every other episode of the same programme.
        var item = RecordingItemMapper.BuildChannelItem(
            Recording("Tatort", season: 2026, episode: 14, episodeTitle: null, isSeries: true),
            RecordingMediaSourceFactory.BuildPlaceholderSource("1f6cf027e0f2168c8ffaab722d151bb1"));

        Assert.Equal("Tatort", item.SeriesName);
        Assert.Equal("Tatort", item.Name);
        Assert.Equal(14, item.IndexNumber);
        Assert.Equal(2026, item.ParentIndexNumber);
    }

    [Fact]
    public void AnEpisodeWithATitleIsCalledByIt()
    {
        var item = RecordingItemMapper.BuildChannelItem(
            Recording("Wolfsland", season: null, episode: null, episodeTitle: "Das schwarze Herz", isSeries: true),
            RecordingMediaSourceFactory.BuildPlaceholderSource("id"));

        Assert.Equal("Das schwarze Herz", item.Name);
        Assert.Equal("Wolfsland", item.SeriesName);
    }

    [Fact]
    public void SomethingThatIsNotASeriesBelongsToNoSeries()
    {
        var item = RecordingItemMapper.BuildChannelItem(
            Recording("Der Untergang", season: null, episode: null, episodeTitle: null, isSeries: false),
            RecordingMediaSourceFactory.BuildPlaceholderSource("id"));

        Assert.Null(item.SeriesName);
        Assert.Equal("Der Untergang", item.Name);
    }

    [Fact]
    public void TheYearAndTheRatingReachTheItem()
    {
        var recording = Recording("Wolfsland", null, null, null, isSeries: false);
        recording.ProductionYear = 2023;
        recording.OfficialRating = "FSK 12";
        recording.CommunityRating = 6.6f;

        var item = RecordingItemMapper.BuildChannelItem(recording, RecordingMediaSourceFactory.BuildPlaceholderSource("id"));

        Assert.Equal(2023, item.ProductionYear);
        Assert.Equal("FSK 12", item.OfficialRating);
        Assert.Equal(6.6f, item.CommunityRating);
    }

    [Fact]
    public void NothingIsFilledInWithASubstitute()
    {
        // A missing season is a missing season, not season one, and a missing year is not the year
        // the recording was made in.
        var item = RecordingItemMapper.BuildChannelItem(
            Recording("Der Untergang", season: null, episode: null, episodeTitle: null, isSeries: false),
            RecordingMediaSourceFactory.BuildPlaceholderSource("id"));

        Assert.Null(item.IndexNumber);
        Assert.Null(item.ParentIndexNumber);
        Assert.Null(item.ProductionYear);
        Assert.Null(item.OfficialRating);
    }

    [Fact]
    public void TheItemStillCarriesOnlyAPlaceholderSource()
    {
        // The listing must not analyse the recordings it lists, and the metadata added here does
        // not change that: what a recording contains is still answered when playback is negotiated.
        var item = RecordingItemMapper.BuildChannelItem(
            Recording("Tatort", 2026, 14, null, isSeries: true),
            RecordingMediaSourceFactory.BuildPlaceholderSource("1f6cf027e0f2168c8ffaab722d151bb1"));

        var source = Assert.Single(item.MediaSources);
        Assert.Equal(MediaBrowser.Model.Dto.MediaSourceType.Placeholder, source.Type);
        Assert.Empty(source.MediaStreams);
        Assert.Equal("1f6cf027e0f2168c8ffaab722d151bb1", source.Id);
    }

    private static MyRecordingInfo Recording(
        string name,
        int? season,
        int? episode,
        string? episodeTitle,
        bool isSeries)
        => new()
        {
            Id = "4711",
            Name = name,
            SeasonNumber = season,
            EpisodeNumber = episode,
            EpisodeTitle = episodeTitle,
            IsSeries = isSeries,
            Status = RecordingStatus.Completed,
        };
}
