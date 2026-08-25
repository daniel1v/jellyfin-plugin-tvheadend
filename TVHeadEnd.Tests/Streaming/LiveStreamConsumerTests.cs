using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// How long a live stream is held open, and for whom.
/// </summary>
/// <remarks>
/// The distinction being protected is between a viewer and an attempt. A client that fails to
/// start playback negotiates again, and Jellyfin answers by asking for the stream once more.
/// Counting those asks left the stream held open by attempts nobody was watching, because a
/// client reports one stop, not one per attempt it abandoned.
/// </remarks>
public class LiveStreamConsumerTests
{
    private const string ViewerA = "Jellyfin for Androidffd8cff37cd3311d";
    private const string ViewerB = "Jellyfin Web4b7696f7983f4680";

    [Fact]
    public void OneViewerNegotiatingSeveralTimesIsStillOneViewer()
    {
        // Measured shape of the defect: three opens for a single Android viewer, because its
        // player failed twice before falling back.
        var consumers = new LiveStreamConsumers();

        Assert.True(consumers.Acquire(ViewerA));
        Assert.False(consumers.Acquire(ViewerA));
        Assert.False(consumers.Acquire(ViewerA));

        Assert.Equal(1, consumers.Count);
    }

    [Fact]
    public void TwoViewersAreTwoViewers()
    {
        var consumers = new LiveStreamConsumers();

        Assert.True(consumers.Acquire(ViewerA));
        Assert.True(consumers.Acquire(ViewerB));

        Assert.Equal(2, consumers.Count);
    }

    [Fact]
    public void TheStreamOutlivesTheFirstViewerToLeave()
    {
        var consumers = new LiveStreamConsumers();
        consumers.Acquire(ViewerA);
        consumers.Acquire(ViewerB);

        Assert.Equal(1, consumers.ReleaseOne());
    }

    [Fact]
    public void TheLastViewerToLeaveTakesTheStreamWithThem()
    {
        var consumers = new LiveStreamConsumers();
        consumers.Acquire(ViewerA);
        consumers.Acquire(ViewerB);

        consumers.ReleaseOne();

        Assert.Equal(0, consumers.ReleaseOne());
    }

    [Fact]
    public void LeavingTwiceIsNotWorseThanLeavingOnce()
    {
        // Jellyfin closes a live stream from several places, and a late or repeated close must
        // not drive the count below nothing -- which would take the stream away from whoever
        // arrives next.
        var consumers = new LiveStreamConsumers();
        consumers.Acquire(ViewerA);

        Assert.Equal(0, consumers.ReleaseOne());
        Assert.Equal(0, consumers.ReleaseOne());
        Assert.Equal(0, consumers.Count);

        Assert.True(consumers.Acquire(ViewerB));
        Assert.Equal(1, consumers.Count);
    }

    [Fact]
    public void AStreamOpenedThreeTimesForOneViewerClosesOnTheFirstStop()
    {
        // The contract as Jellyfin uses it: MediaSourceManager decrements ConsumerCount once for
        // the stop the client reports, and closes the stream when that reaches nothing. Before
        // this, three opens for one viewer left it at two and the stream ran on unwatched.
        using var stream = LiveStream();

        stream.Consumers.Acquire(ViewerA);
        stream.Consumers.Acquire(ViewerA);
        stream.Consumers.Acquire(ViewerA);
        Assert.Equal(1, stream.ConsumerCount);

        stream.ConsumerCount--;

        Assert.Equal(0, stream.ConsumerCount);
    }

    [Fact]
    public void AStreamTwoViewersShareSurvivesTheFirstStop()
    {
        using var stream = LiveStream();

        stream.Consumers.Acquire(ViewerA);
        stream.Consumers.Acquire(ViewerB);
        Assert.Equal(2, stream.ConsumerCount);

        stream.ConsumerCount--;
        Assert.Equal(1, stream.ConsumerCount);

        stream.ConsumerCount--;
        Assert.Equal(0, stream.ConsumerCount);
    }

    private static TvheadendLiveStream LiveStream()
        => new(
            "42",
            "Das Erste HD",
            "http://tvheadend/stream",
            new Dictionary<string, string>(),
            new MediaSourceInfo(),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            LiveStreamBuffer.MinimumSizeMegabytes,
            new UnusedClientFactory(),
            NullLogger.Instance);

    /// <summary>
    /// Never called: this stream is only ever counted, never opened.
    /// </summary>
    private sealed class UnusedClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
