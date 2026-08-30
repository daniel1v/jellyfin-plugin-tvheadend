using System.Linq;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.Core.Broadcast;
using TVHeadEnd.Core.Dvr;
using TVHeadEnd.LiveTv;
using TVHeadEnd.Tvheadend.Mapping;
using Xunit;

namespace TVHeadEnd.Tests.Recordings;

/// <summary>
/// What a recording knows about itself, and what a passing statistics update must not take away.
/// </summary>
/// <remarks>
/// <para>
/// TVHeadend sends only what changed. While a recording runs it sends a bare statistics update
/// every few seconds -- bytes written, errors counted -- and every one of those is a chance to
/// erase a field that was read once and can never be read again, because the guide event it came
/// from is gone within days.
/// </para>
/// <para>
/// The other half is what makes a recording an episode. It used to be the episode title alone,
/// which a German broadcast supplies far less often than it supplies a number, so a series with
/// numbered episodes arrived as a shelf of unrelated films.
/// </para>
/// </remarks>
public class RecordingMetadataCompletionTests
{
    [Fact]
    public void TheSeasonAndEpisodeNumbersAreRead()
    {
        var entry = Read(Entry().Set("seasonNumber", 2026).Set("episodeNumber", 14));

        Assert.Equal(2026, entry.SeasonNumber);
        Assert.Equal(14, entry.EpisodeNumber);
    }

    [Fact]
    public void TheYearAndTheRatingAreRead()
    {
        var entry = Read(Entry().Set("copyrightYear", 2023).Set("ratingLabel", "FSK 12"));

        Assert.Equal(2023, entry.ProductionYear);
        Assert.Equal("FSK 12", entry.RatingLabel);
    }

    [Fact]
    public void TheEpisodeNumberAsTheBroadcastWroteItIsRead()
    {
        // TVHeadend calls this "episode" on a DVR entry and "episodeOnscreen" on a guide event --
        // the same field of the same structure under two names. It is what remains when the
        // numbering was in a form nothing could parse.
        Assert.Equal("Folge 14", Read(Entry().Set("episode", "Folge 14")).EpisodeOnscreen);
    }

    [Theory]
    [InlineData("seasonNumber", 0)]
    [InlineData("episodeNumber", 0)]
    public void ZeroIsNotANumberedSeasonOrEpisode(string field, long value)
    {
        // Zero is how the server says it does not know. Published as a real number it would give
        // every recording a season zero and an episode zero.
        var entry = Read(Entry().Set(field, value));

        Assert.Null(entry.SeasonNumber);
        Assert.Null(entry.EpisodeNumber);
    }

    [Fact]
    public void AStatisticsUpdateTakesNothingAway()
    {
        // The update TVHeadend sends every few seconds while a recording runs. It mentions the
        // state and the bytes and nothing else, and everything else must survive it.
        var stored = Read(Entry()
            .Set("seasonNumber", 2026)
            .Set("episodeNumber", 14)
            .Set("copyrightYear", 2023)
            .Set("ratingLabel", "FSK 12")
            .Set("episode", "Folge 14")
            .Set("subtitle", "Der Fall Holdt"));

        var statistics = HtspMessage.Create("dvrEntryUpdate")
            .Set("id", 4711)
            .Set("state", "recording")
            .Set("dataSize", 101027252)
            .Set("dataErrors", 0);

        var merged = DvrEntryMapper.Merge(stored, Read(statistics), statistics);

        Assert.Equal(2026, merged.SeasonNumber);
        Assert.Equal(14, merged.EpisodeNumber);
        Assert.Equal(2023, merged.ProductionYear);
        Assert.Equal("FSK 12", merged.RatingLabel);
        Assert.Equal("Folge 14", merged.EpisodeOnscreen);
        Assert.Equal("Der Fall Holdt", merged.Subtitle);
        Assert.Equal(DvrState.Recording, merged.State);
    }

    [Fact]
    public void AnUpdateThatDoesMentionAFieldReplacesIt()
    {
        // The other half of the same rule: a correction from the server has to land.
        var stored = Read(Entry().Set("seasonNumber", 1).Set("episodeNumber", 3).Set("ratingLabel", "FSK 6"));

        var correction = HtspMessage.Create("dvrEntryUpdate")
            .Set("id", 4711)
            .Set("seasonNumber", 2)
            .Set("episodeNumber", 4)
            .Set("ratingLabel", "FSK 16")
            .Set("copyrightYear", 1999);

        var merged = DvrEntryMapper.Merge(stored, Read(correction), correction);

        Assert.Equal(2, merged.SeasonNumber);
        Assert.Equal(4, merged.EpisodeNumber);
        Assert.Equal("FSK 16", merged.RatingLabel);
        Assert.Equal(1999, merged.ProductionYear);
    }

    [Fact]
    public void ANumberedEpisodeWithNoEpisodeTitleIsStillASeries()
    {
        // The case that used to fall through, and the reason a shelf of "Tatort" recordings stood
        // there as separate films.
        var recording = JellyfinDvrMapper.ToRecording(Read(Entry()
            .Set("title", "Tatort")
            .Set("seasonNumber", 2026)
            .Set("episodeNumber", 14)));

        Assert.True(recording.IsSeries);
        Assert.Null(recording.EpisodeTitle);
        Assert.Equal(2026, recording.SeasonNumber);
        Assert.Equal(14, recording.EpisodeNumber);
    }

    [Fact]
    public void AnEpisodeTitleAloneIsStillASeries()
    {
        var recording = JellyfinDvrMapper.ToRecording(Read(Entry()
            .Set("title", "Wolfsland")
            .Set("subtitle", "Das schwarze Herz")));

        Assert.True(recording.IsSeries);
        Assert.Equal("Das schwarze Herz", recording.EpisodeTitle);
    }

    [Fact]
    public void AWrittenOutEpisodeNumberIsSeriesEvidenceToo()
    {
        var recording = JellyfinDvrMapper.ToRecording(Read(Entry()
            .Set("title", "Tatort")
            .Set("episode", "Folge 14")));

        Assert.True(recording.IsSeries);
    }

    [Fact]
    public void NoEvidenceMeansNoSeries()
    {
        var recording = JellyfinDvrMapper.ToRecording(Read(Entry().Set("title", "Der Untergang")));

        Assert.False(recording.IsSeries);
    }

    [Fact]
    public void ASeriesRuleOnItsOwnDoesNotMakeSomethingASeries()
    {
        // An autorec entry is a saved search, and a viewer may save one for a keyword, a title or
        // a channel. Treating everything one catches as a television series would turn a standing
        // search for "Tatort" and one for "Fussball" into the same claim.
        var recording = JellyfinDvrMapper.ToRecording(Read(Entry()
            .Set("title", "Sportschau")
            .Set("autorecId", "ebf5ceed98dfc9f1ebc8a2d052cd9233")));

        Assert.False(recording.IsSeries);
        Assert.Equal("ebf5ceed98dfc9f1ebc8a2d052cd9233", recording.SeriesTimerId);
    }

    [Fact]
    public void TheMetadataReachesTheRecordingJellyfinIsHanded()
    {
        var recording = JellyfinDvrMapper.ToRecording(Read(Entry()
            .Set("title", "Tatort")
            .Set("seasonNumber", 2026)
            .Set("episodeNumber", 14)
            .Set("copyrightYear", 2023)
            .Set("ratingLabel", "FSK 12")));

        Assert.Equal(2026, recording.SeasonNumber);
        Assert.Equal(14, recording.EpisodeNumber);
        Assert.Equal(2023, recording.ProductionYear);
        Assert.Equal("FSK 12", recording.OfficialRating);
    }

    [Fact]
    public void NothingIsFilledInWhereTheBroadcastSaidNothing()
    {
        var recording = JellyfinDvrMapper.ToRecording(Read(Entry().Set("title", "Der Untergang")));

        Assert.Null(recording.SeasonNumber);
        Assert.Null(recording.EpisodeNumber);
        Assert.Null(recording.ProductionYear);
        Assert.Null(recording.OfficialRating);
    }

    [Fact]
    public void WhatWasAlreadyReadIsStillRead()
    {
        // The fields this change did not touch, asserted alongside the ones it added: a merge list
        // is a place where adding an entry can silently drop one.
        var entry = Read(Entry()
            .Set("title", "Wolfsland")
            .Set("subtitle", "Das schwarze Herz")
            .Set("description", "Ein toter Diplom-Biologe")
            .Set("contentType", 0x11)
            .Set("channel", 1460599120)
            .Set("image", "imagecache/42"));

        Assert.Equal("Wolfsland", entry.Title);
        Assert.Equal("Das schwarze Herz", entry.Subtitle);
        Assert.Equal("Ein toter Diplom-Biologe", entry.Description);
        Assert.Equal(0x11, entry.ContentType);
        Assert.Equal("1460599120", entry.ChannelId);
        Assert.Equal("imagecache/42", entry.Image);

        var recording = JellyfinDvrMapper.ToRecording(entry);
        Assert.True(recording.IsMovie);
        Assert.Equal(["Movie", "Detective"], recording.Genres.ToList());
    }

    private static DvrEntry Read(HtspMessage message)
    {
        var entry = DvrEntryMapper.FromMessage(message);
        Assert.NotNull(entry);
        return entry;
    }

    private static HtspMessage Entry()
        => HtspMessage.Create("dvrEntryAdd")
            .Set("id", 4711)
            .Set("state", "completed")
            .Set("start", 1788033900)
            .Set("stop", 1788039300);
}
