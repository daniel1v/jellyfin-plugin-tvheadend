using System.Linq;
using MediaBrowser.Model.LiveTv;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.Core.Broadcast;
using TVHeadEnd.Core.Dvr;
using TVHeadEnd.LiveTv;
using TVHeadEnd.Tvheadend.Mapping;
using Xunit;

namespace TVHeadEnd.Tests.Recordings;

/// <summary>
/// What a recording says about itself, beyond when it ran.
/// </summary>
/// <remarks>
/// The recordings channel sorts its folders by IsMovie, IsSports, IsNews and IsKids, and those
/// come from one DVB content byte TVHeadend copies onto the DVR entry when it schedules the
/// recording. The projection was dropping the byte, so every one of those folders was empty for
/// a reason nothing reported.
/// </remarks>
public class RecordingMetadataTests
{
    /// <summary>
    /// Movie/Drama, refined as "Detective". High nibble 0x1, low nibble 0x1.
    /// </summary>
    private const int DetectiveFilm = 0x11;

    /// <summary>
    /// Sports, refined as "Football". High nibble 0x4, low nibble 0x3.
    /// </summary>
    private const int Football = 0x43;

    [Fact]
    public void TheContentTypeIsReadFromWhatTvheadendAnnounced()
    {
        var entry = DvrEntryMapper.FromMessage(Message(1, contentType: DetectiveFilm))!;

        Assert.Equal(DetectiveFilm, entry.ContentType);
    }

    [Fact]
    public void AnUpdateThatDoesNotMentionTheContentTypeKeepsIt()
    {
        // TVHeadend sends only what changed. A state change carries no content type, and reading
        // the update on its own would leave the recording unclassified from the moment it started.
        var scheduled = DvrEntryMapper.FromMessage(Message(1, contentType: Football))!;

        var update = Message(1);
        update.Set("state", "recording");

        var merged = DvrEntryMapper.Merge(scheduled, DvrEntryMapper.FromMessage(update)!, update);

        Assert.Equal(Football, merged.ContentType);
        Assert.Equal(DvrState.Recording, merged.State);
    }

    [Fact]
    public void AnUpdateThatMentionsTheContentTypeChangesIt()
    {
        var scheduled = DvrEntryMapper.FromMessage(Message(1, contentType: Football))!;

        var update = Message(1, contentType: DetectiveFilm);

        var merged = DvrEntryMapper.Merge(scheduled, DvrEntryMapper.FromMessage(update)!, update);

        Assert.Equal(DetectiveFilm, merged.ContentType);
    }

    [Fact]
    public void ARecordingIsClassifiedTheWayAGuideProgrammeIs()
    {
        // One table, read twice. A film in the guide and the recording made from it must not
        // disagree about being a film.
        var entry = DvrEntryMapper.FromMessage(Message(1, state: "completed", contentType: DetectiveFilm))!;
        var recording = JellyfinDvrMapper.ToRecording(entry);

        var described = DvbContentType.Describe(DetectiveFilm);

        Assert.True(recording.IsMovie);
        Assert.False(recording.IsSports);
        Assert.False(recording.IsNews);
        Assert.False(recording.IsKids);
        Assert.Equal(described.Genres, recording.Genres);
        Assert.Contains("Detective", recording.Genres);
    }

    [Fact]
    public void SportIsSportAndCarriesItsGenres()
    {
        var entry = DvrEntryMapper.FromMessage(Message(1, state: "completed", contentType: Football))!;
        var recording = JellyfinDvrMapper.ToRecording(entry);

        Assert.True(recording.IsSports);
        Assert.False(recording.IsMovie);
        Assert.Contains("Football", recording.Genres);
    }

    [Fact]
    public void ARecordingTheServerSaysNothingAboutIsClassifiedAsNothing()
    {
        var entry = DvrEntryMapper.FromMessage(Message(1, state: "completed"))!;
        var recording = JellyfinDvrMapper.ToRecording(entry);

        Assert.False(recording.IsMovie);
        Assert.False(recording.IsSports);
        Assert.False(recording.IsNews);
        Assert.False(recording.IsKids);
        Assert.Empty(recording.Genres);
    }

    [Theory]
    [InlineData(0x11, "Movies")]
    [InlineData(0x43, "Sports")]
    [InlineData(0x20, "News")]
    [InlineData(0x51, "Kids")]
    public void TheRecordingGroupsAreFilledByTheseVeryFlags(int contentType, string group)
    {
        // The predicates the recordings channel uses to build its folders, applied to what the
        // projection now produces. Before this they matched nothing, whatever was recorded.
        var recording = JellyfinDvrMapper.ToRecording(
            DvrEntryMapper.FromMessage(Message(1, state: "completed", contentType: contentType))!);

        var landsIn = new[]
        {
            (Group: "Kids", Matches: recording.IsKids),
            (Group: "Movies", Matches: recording.IsMovie),
            (Group: "News", Matches: recording.IsNews),
            (Group: "Sports", Matches: recording.IsSports),
        }.Where(candidate => candidate.Matches).Select(candidate => candidate.Group);

        Assert.Equal([group], landsIn);
    }

    [Fact]
    public void ArtworkSurvivesAnUpdateAboutSomethingElse()
    {
        var scheduled = DvrEntryMapper.FromMessage(Message(1, image: "imagecache/42", fanart: "imagecache/43"))!;

        var update = Message(1);
        update.Set("state", "recording");

        var merged = DvrEntryMapper.Merge(scheduled, DvrEntryMapper.FromMessage(update)!, update);

        Assert.Equal("imagecache/42", merged.Image);
        Assert.Equal("imagecache/43", merged.FanartImage);
    }

    [Fact]
    public void ArtworkTheUpdateNamesReplacesWhatWasThere()
    {
        // Artwork was missing from the merge, so a picture arriving after the entry was scheduled
        // -- which is when it arrives -- was read and then dropped.
        var scheduled = DvrEntryMapper.FromMessage(Message(1, image: "imagecache/42", fanart: "imagecache/43"))!;

        var update = Message(1, image: "imagecache/99", fanart: "imagecache/100");

        var merged = DvrEntryMapper.Merge(scheduled, DvrEntryMapper.FromMessage(update)!, update);

        Assert.Equal("imagecache/99", merged.Image);
        Assert.Equal("imagecache/100", merged.FanartImage);
    }

    [Fact]
    public void AMapperThatOnlyReadsOneDvrEntryCannotKnowWhatItsChannelCarries()
    {
        // A DVR entry does not say whether its channel carries pictures, so the projection cannot
        // answer it and the value stays at the enum's default -- which is TV. That default is the
        // bug: every radio recording was published as video. What fills it in is
        // TvheadendRecordings.GetAllAsync, the one place every recording passes and the channel
        // catalog is in reach.
        var recording = JellyfinDvrMapper.ToRecording(
            DvrEntryMapper.FromMessage(Message(1, state: "completed"))!);

        Assert.Equal(ChannelType.TV, recording.ChannelType);
    }

    private static HtspMessage Message(
        int id,
        string state = "scheduled",
        int? contentType = null,
        string? image = null,
        string? fanart = null)
    {
        var message = new HtspMessage();
        message.Set("id", (long)id);
        message.Set("state", state);

        if (contentType is { } value)
        {
            message.Set("contentType", (long)value);
        }

        if (image is not null)
        {
            message.Set("image", image);
        }

        if (fanart is not null)
        {
            message.Set("fanartImage", fanart);
        }

        return message;
    }
}
