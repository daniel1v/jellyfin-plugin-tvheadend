using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.LiveTv;
using TVHeadEnd.Recordings;
using TVHeadEnd.Tvheadend.Catalogs;
using Xunit;

namespace TVHeadEnd.Tests.Recordings;

/// <summary>
/// What Jellyfin caches the recordings listing under.
/// </summary>
/// <remarks>
/// The listing is cached on disk under a path built from this key, and ChannelManager reaches the
/// key only through <see cref="IHasCacheKey"/>. So the interface and the signature are not
/// decoration: without them the method is never called and every listing is cached under an empty
/// key that nothing can invalidate.
/// </remarks>
public class RecordingsCacheKeyTests
{
    [Fact]
    public void TheRecordingsChannelOffersJellyfinACacheKey()
    {
        // It had the method and not the interface, so nothing ever asked for it.
        Assert.True(typeof(IHasCacheKey).IsAssignableFrom(typeof(RecordingsChannel)));
    }

    [Fact]
    public void TheSignatureIsTheOneJellyfinCalls()
    {
        // A method of the right name and the wrong shape is a method that does not implement the
        // interface, which is how this one came to be dead code.
        var method = typeof(RecordingsChannel).GetMethod(nameof(IHasCacheKey.GetCacheKey), [typeof(string)]);

        Assert.NotNull(method);
        Assert.Equal(typeof(string), method!.ReturnType);
    }

    [Fact]
    public void NothingChangedMeansTheSameKey()
    {
        // Within one run of the server the cache is discarded when TVHeadend says the recordings
        // changed, and at no other time. A key that moved on its own would be a timer, which is
        // what this deliberately is not.
        Assert.Equal(
            RecordingsChannel.ComposeCacheKey("epoch-a", 7),
            RecordingsChannel.ComposeCacheKey("epoch-a", 7));
    }

    [Fact]
    public void ADvrChangeMeansANewKey()
    {
        Assert.NotEqual(
            RecordingsChannel.ComposeCacheKey("epoch-a", 7),
            RecordingsChannel.ComposeCacheKey("epoch-a", 8));
    }

    [Fact]
    public void ARestartMeansANewKeyEvenAtTheSameRevision()
    {
        // The revision counts changes since the connection was made, so it starts again at zero
        // every restart -- while the cache on disk does not. Without the epoch a restarted server
        // asks under a key the previous run already wrote and is served that run's recordings.
        Assert.NotEqual(
            RecordingsChannel.ComposeCacheKey("epoch-a", 0),
            RecordingsChannel.ComposeCacheKey("epoch-b", 0));
    }

    [Fact]
    public void TheEpochCannotBeConfusedWithTheRevision()
    {
        // Two parts concatenated without a separator can spell the same key from different halves.
        Assert.NotEqual(
            RecordingsChannel.ComposeCacheKey("a", 11),
            RecordingsChannel.ComposeCacheKey("a1", 1));
    }

    [Fact]
    public void ARadioRecordingIsPublishedAsAudioAndATvRecordingAsVideo()
    {
        // The consequence of the channel type being set at all. A radio recording published as
        // video is a concert behind a black screen.
        Assert.Equal(
            ChannelMediaType.Audio,
            RecordingItemMapper.MediaTypeFor(ChannelType.Radio));

        Assert.Equal(
            ChannelMediaType.Video,
            RecordingItemMapper.MediaTypeFor(ChannelType.TV));
    }

    [Fact]
    public void TheChannelCatalogIsWhatAnswersWhichOfTheTwoARecordingCameFrom()
    {
        // The lookup TvheadendRecordings.GetAllAsync makes for every recording it hands over.
        // TVHeadend states the kind on the channel's service, not on the DVR entry, which is why
        // the recording has to be told.
        var catalog = new ChannelCatalog(NullLogger<ChannelCatalog>.Instance);
        catalog.AddOrUpdate(Channel(1, "Radio Eins", "Radio"));
        catalog.AddOrUpdate(Channel(2, "Das Erste HD", "HDTV"));

        Assert.Equal(ChannelType.Radio, JellyfinChannelMapper.ChannelTypeFor(catalog.Get("1"), null));
        Assert.Equal(ChannelType.TV, JellyfinChannelMapper.ChannelTypeFor(catalog.Get("2"), null));
    }

    private static HtspMessage Channel(int id, string name, string serviceType)
    {
        var service = new HtspMessage();
        service.Set("type", serviceType);

        var message = new HtspMessage();
        message.Set("channelId", (long)id);
        message.Set("channelName", name);
        message.Set("channelNumber", (long)id);
        message.Set("services", new[] { service });
        return message;
    }
}
