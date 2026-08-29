using System;
using System.Linq;
using MediaBrowser.Controller.LiveTv;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.LiveTv;
using Xunit;

namespace TVHeadEnd.Tests.LiveTv;

/// <summary>
/// What a guide entry says about the programme, beyond when it is on.
/// </summary>
/// <remarks>
/// Every one of these is a fact the broadcast carried and this plugin was throwing away. The rule
/// throughout is that nothing is invented: a field the broadcast did not send arrives as nothing
/// at all, rather than as a plausible-looking substitute derived from a different field.
/// </remarks>
public class GuideMetadataTests
{
    [Fact]
    public void TheEpisodeIdentityAndTheSeriesIdentityAreKeptApart()
    {
        // Two different questions. The series link says which broadcasts belong together and is
        // what a series recording binds to; the episode link names this one programme. Measured on
        // the live server: 135 of 5174 events carried an episodeUri.
        var program = Read(Event()
            .Set("serieslinkUri", "crid://onid-1/s/aabbcc")
            .Set("episodeUri", "crid://onid-1/e/6a61405f7d3b62ba7be7ed76"));

        Assert.Equal("crid://onid-1/s/aabbcc", program.SeriesId);
        Assert.Equal("crid://onid-1/e/6a61405f7d3b62ba7be7ed76", program.ShowId);
    }

    [Fact]
    public void NoEpisodeIdentityIsInventedWhereTheBroadcastGivesNone()
    {
        // A generated identifier would be worse than none: it would look stable and be different
        // on every server, so nothing could ever match a repeat against it.
        var program = Read(Event().Set("serieslinkUri", "crid://onid-1/s/aabbcc"));

        Assert.Null(program.ShowId);
        Assert.Equal("crid://onid-1/s/aabbcc", program.SeriesId);
    }

    [Fact]
    public void TheCopyrightYearBecomesTheProductionYear()
    {
        Assert.Equal(1962, Read(Event().Set("copyrightYear", 1962)).ProductionYear);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    public void ANumberThatCannotBeAYearIsNotPublishedAsOne(long copyrightYear)
    {
        Assert.Null(Read(Event().Set("copyrightYear", copyrightYear)).ProductionYear);
    }

    [Fact]
    public void TheYearIsNeverTakenFromWhenTheProgrammeIsOn()
    {
        // The one substitution that would look right and be wrong. A 1962 film shown tonight is a
        // 1962 film, and a broadcast that states no year states no year.
        var program = Read(Event().Set("firstAired", 1546300800));

        Assert.Null(program.ProductionYear);
    }

    [Fact]
    public void TheRatingLabelIsPassedOnInTheBroadcastersOwnWords()
    {
        // TVHeadend has already resolved this from whichever authority the broadcast named.
        // Converting an FSK number into an American certificate would be this plugin inventing a
        // claim about who may watch something.
        Assert.Equal("FSK 12", Read(Event().Set("ratingLabel", "FSK 12")).OfficialRating);
    }

    [Theory]
    [InlineData(100, 10f)]
    [InlineData(66, 6.6f)]
    [InlineData(50, 5f)]
    [InlineData(1, 0.1f)]
    public void TheStarRatingIsBroughtOntoTheScaleJellyfinReads(long starRating, float expected)
    {
        // TVHeadend stores a percentage: its XMLTV grabber turns "3.3/5" into 66. Jellyfin's
        // community rating is the number clients render as "x out of ten", so 66 handed over
        // unchanged is a programme rated 66 out of 10 -- wrong in a way nothing reports.
        Assert.Equal(expected, Read(Event().Set("starRating", starRating)).CommunityRating);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(255)]
    public void ARatingOnAScaleThisDoesNotKnowIsNotGuessedAt(long starRating)
    {
        // Zero is TVHeadend's "unrated". Above a hundred the percentage reading no longer holds
        // and there is nothing else it could be, so saying nothing leaves Jellyfin free to fill
        // the rating in from its own providers.
        Assert.Null(Read(Event().Set("starRating", starRating)).CommunityRating);
    }

    [Fact]
    public void ANewProgrammeIsAPremiere()
    {
        Assert.True(Read(Event().Set("isNew", 1)).IsPremiere);
    }

    [Fact]
    public void APremiereIsNeverInferredFromSilence()
    {
        // The absence of a "previously shown" marker is not the presence of a premiere, and DVB
        // omits far more than it states.
        Assert.False(Read(Event()).IsPremiere);
    }

    [Fact]
    public void TheBroadcastersOwnCategoriesAreOffered()
    {
        var program = Read(Event().Set("category", ["Krimi", "Drama"]));

        Assert.Equal(["Krimi", "Drama"], program.Genres);
    }

    [Fact]
    public void CategoriesAndTheDvbTableAreBothKept()
    {
        // Neither replaces the other and nothing is translated between them: "Krimi" and
        // "Detective" are both true of the same programme, and a viewer searching either should
        // find it. The broadcaster's own words come first.
        var program = Read(Event()
            .Set("category", ["Krimi", "Drama"])
            .Set("contentType", 0x11));

        Assert.Equal(["Krimi", "Drama", "Movie", "Detective"], program.Genres);
    }

    [Fact]
    public void TheSameGenreSpeltTwoWaysIsListedOnce()
    {
        var program = Read(Event()
            .Set("category", ["movie", "Krimi"])
            .Set("contentType", 0x11));

        Assert.Equal(["movie", "Krimi", "Detective"], program.Genres);
    }

    [Fact]
    public void EmptyCategoriesAreNotGenres()
    {
        var program = Read(Event().Set("category", ["  ", string.Empty, " Drama "]));

        Assert.Equal(["Drama"], program.Genres);
    }

    [Fact]
    public void TheDvbClassificationStillDecidesWhatKindOfProgrammeItIs()
    {
        // The recordings channel groups on exactly these, and free text never touches them: a
        // broadcaster writing "Kinderfilm" does not make Jellyfin's Movies folder work, and the
        // content descriptor byte does.
        var program = Read(Event()
            .Set("category", ["Krimi"])
            .Set("contentType", 0x11));

        Assert.True(program.IsMovie);
        Assert.False(program.IsSports);
        Assert.False(program.IsNews);
        Assert.False(program.IsKids);
    }

    [Fact]
    public void CategoriesAloneStillProduceGenres()
    {
        // An event with free-text categories and no content descriptor, which is what an XMLTV
        // grabber without a DVB source produces.
        var program = Read(Event().Set("category", ["Talkshow"]));

        Assert.Equal(["Talkshow"], program.Genres);
        Assert.False(program.IsMovie);
    }

    [Fact]
    public void TheConfiguredMetadataLanguageIsWhatIsAskedFor()
    {
        // TVHeadend holds a broadcast's text once per language and picks between them per request.
        var request = TvheadendGuide.BuildEventsRequest(42, End, "de");

        Assert.Equal("de", request.GetString("language"));
        Assert.Equal(42, request.GetInt32("channelId"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoConfiguredLanguageMeansNoLanguageIsStated(string? configured)
    {
        // TVHeadend then falls back to the language the connection authenticated with, which is a
        // better answer than any this could invent -- and "und" would ask for the one language
        // that means "undetermined" and match almost nothing.
        var request = TvheadendGuide.BuildEventsRequest(42, End, configured);

        Assert.False(request.Contains("language"));
    }

    [Fact]
    public void NothingElseAboutTheRequestChanges()
    {
        var withLanguage = TvheadendGuide.BuildEventsRequest(42, End, "de");
        var without = TvheadendGuide.BuildEventsRequest(42, End, null);

        Assert.Equal("getEvents", withLanguage.Method);
        Assert.Equal(
            without.FieldNames.Order(StringComparer.Ordinal),
            withLanguage.FieldNames.Where(name => name != "language").Order(StringComparer.Ordinal));
        Assert.Equal(without.GetInt64("maxTime"), withLanguage.GetInt64("maxTime"));
    }

    [Fact]
    public void AnEventWithNoTimesIsNotAProgramme()
    {
        Assert.Null(TvheadendGuide.ReadProgram(HtspMessage.Create("event").Set("title", "Tagesschau")));
    }

    private static readonly DateTime End = new(2026, 8, 30, 20, 0, 0, DateTimeKind.Utc);

    private static ProgramInfo Read(HtspMessage entry)
    {
        var program = TvheadendGuide.ReadProgram(entry);
        Assert.NotNull(program);
        return program;
    }

    private static HtspMessage Event()
        => HtspMessage.Create("event")
            .Set("eventId", 4711)
            .Set("channelId", 1)
            .Set("start", 1788004800)
            .Set("stop", 1788006600)
            .Set("title", "Tatort");
}
