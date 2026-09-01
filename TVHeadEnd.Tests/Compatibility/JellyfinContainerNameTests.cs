using MediaBrowser.Controller.MediaEncoding;
using TVHeadEnd.Compatibility.Jellyfin12;
using TVHeadEnd.Playback;
using Xunit;

namespace TVHeadEnd.Tests.Compatibility;

/// <summary>
/// What Jellyfin calls the container, which is a naming convention and not a fact about bytes.
/// </summary>
public class JellyfinContainerNameTests
{
    [Theory]
    [InlineData("mpegts")]
    [InlineData("ts")]
    [InlineData("MPEGTS")]
    public void EitherSpellingIsReportedAsTheOneJellyfinProduces(string probed)
    {
        Assert.Equal("ts", JellyfinContainerNames.Describe(probed, "ts"));
    }

    [Fact]
    public void TheNameReachesFfmpegAsAFormatItActuallyHas()
    {
        // With hardware acceleration the container is passed as -f, translated on the way by
        // EncodingHelper. Naming two spellings at once broke playback outright: "mpegts,ts" is in
        // no translation table and reached FFmpeg unchanged.
        Assert.Equal("mpegts", EncodingHelper.GetInputFormat(JellyfinContainerNames.TransportStream));
        Assert.DoesNotContain(',', JellyfinContainerNames.TransportStream);
    }

    [Fact]
    public void ALiveChannelAndARecordingNameTheSameContainerTheSameWay()
    {
        Assert.Equal(LiveMediaSource.Container, JellyfinContainerNames.TransportStream);
    }

    [Theory]
    [InlineData("matroska,webm")]
    [InlineData("mp4")]
    public void AnyOtherContainerIsReportedAsFound(string probed)
    {
        Assert.Equal(probed, JellyfinContainerNames.Describe(probed, "ts"));
    }

    [Fact]
    public void AnAnalysisThatFoundNothingLeavesTheAssumptionInPlace()
    {
        Assert.Equal("ts", JellyfinContainerNames.Describe(null, "ts"));
        Assert.Equal("ts", JellyfinContainerNames.Describe(string.Empty, "ts"));
    }
}
