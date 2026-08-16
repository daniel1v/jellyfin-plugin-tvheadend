using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

public class LiveTvMediaSourceFactoryTests
{
    [Fact]
    public void CreatePendingReturnsTicketFreeServerMediatedSource()
    {
        const string internalChannelId = "f586be12201f194ac90fdc57268b0d2e";

        var source = LiveTvMediaSourceFactory.CreatePending(internalChannelId);

        Assert.Equal(internalChannelId, source.Id);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.Null(source.Path);
        Assert.True(source.RequiresOpening);
        Assert.False(source.RequiresClosing);
        Assert.False(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.False(source.SupportsProbing);
        Assert.Empty(source.MediaStreams);
    }

    [Fact]
    public void CreateOpenedReturnsProbeableServerMediatedSource()
    {
        const string internalChannelId = "f586be12201f194ac90fdc57268b0d2e";
        const string streamUrl = "http://tvheadend.invalid/stream/channel/1?ticket=redacted";

        var source = LiveTvMediaSourceFactory.CreateOpened(internalChannelId, streamUrl);

        Assert.Equal(internalChannelId, source.Id);
        Assert.Equal(streamUrl, source.Path);
        Assert.False(source.RequiresOpening);
        Assert.True(source.RequiresClosing);
        Assert.False(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsProbing);
        Assert.Empty(source.MediaStreams);
    }

    [Fact]
    public void CreatedSourcesNameBothSpellingsOfTheTransportStreamContainer()
    {
        // Client profiles are split over "mpegts" and "ts", and ContainerHelper compares
        // them for exact equality.
        var pending = LiveTvMediaSourceFactory.CreatePending("f586be12201f194ac90fdc57268b0d2e");
        var opened = LiveTvMediaSourceFactory.CreateOpened("f586be12201f194ac90fdc57268b0d2e", "http://tvheadend.invalid/stream");

        Assert.Equal("mpegts,ts", pending.Container);
        Assert.Equal("mpegts,ts", opened.Container);
    }
}
