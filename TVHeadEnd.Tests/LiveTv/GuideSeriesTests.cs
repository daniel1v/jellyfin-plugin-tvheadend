using MediaBrowser.Controller.LiveTv;
using TVHeadEnd.LiveTv;
using Xunit;

namespace TVHeadEnd.Tests.LiveTv;

/// <summary>
/// What makes a programme in the guide part of a series.
/// </summary>
/// <remarks>
/// The answer decides whether Jellyfin offers to record the series at all, and what a series
/// recording is then bound to. An episode title and a series link are two different things the
/// broadcast may carry, independently of one another.
/// </remarks>
public class GuideSeriesTests
{
    [Fact]
    public void ASeriesLinkMakesItASeriesWithOrWithoutAnEpisodeTitle()
    {
        // The case that used to fall through. DVB episodes often carry no subtitle at all, and
        // requiring one meant the series link the server had already sent went unused.
        var program = Program(seriesLink: "crid://bds.tv/1234");

        TvheadendGuide.ApplySeriesFacts(program, subtitle: null);

        Assert.True(program.IsSeries);
        Assert.Equal("crid://bds.tv/1234", program.SeriesId);
        Assert.Null(program.EpisodeTitle);
    }

    [Fact]
    public void AnEpisodeTitleAndASeriesLinkBothSurvive()
    {
        var program = Program(seriesLink: "crid://bds.tv/1234");

        TvheadendGuide.ApplySeriesFacts(program, "Der Fall Holdt");

        Assert.True(program.IsSeries);
        Assert.Equal("Der Fall Holdt", program.EpisodeTitle);
        Assert.Equal("crid://bds.tv/1234", program.SeriesId);
    }

    [Fact]
    public void AnEpisodeTitleAloneIsStillASeries()
    {
        // Unchanged: a broadcast that names an episode is one of several, whether or not the
        // server also links them.
        var program = Program(seriesLink: null);

        TvheadendGuide.ApplySeriesFacts(program, "Der Fall Holdt");

        Assert.True(program.IsSeries);
        Assert.Null(program.SeriesId);
    }

    [Fact]
    public void NeitherOneInventsASeries()
    {
        var program = Program(seriesLink: null);

        TvheadendGuide.ApplySeriesFacts(program, subtitle: null);

        Assert.False(program.IsSeries);
        Assert.Null(program.SeriesId);
        Assert.Null(program.EpisodeTitle);
    }

    [Fact]
    public void AnEmptySeriesLinkIsNoLinkAtAll()
    {
        var program = Program(seriesLink: string.Empty);

        TvheadendGuide.ApplySeriesFacts(program, subtitle: null);

        Assert.False(program.IsSeries);
    }

    private static ProgramInfo Program(string? seriesLink)
        => new() { Id = "1", Name = "Tatort", SeriesId = seriesLink };
}
