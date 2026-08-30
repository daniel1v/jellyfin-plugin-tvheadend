using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Core.Media;
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
        // player failed twice before falling back. One stop follows, not three.
        using var stream = LiveStream();

        Assert.True(stream.Consumers.Acquire(ViewerA));
        Assert.False(stream.Consumers.Acquire(ViewerA));
        Assert.False(stream.Consumers.Acquire(ViewerA));
        Assert.Equal(1, stream.ConsumerCount);

        stream.ConsumerCount--;

        Assert.Equal(0, stream.ConsumerCount);
    }

    [Fact]
    public void TwoViewersAreTwoViewers()
    {
        using var stream = LiveStream();

        Assert.True(stream.Consumers.Acquire(ViewerA));
        Assert.True(stream.Consumers.Acquire(ViewerB));

        Assert.Equal(2, stream.ConsumerCount);
    }

    [Fact]
    public void AViewerArrivingAfterSomebodyLeftTakesThatPlaceRatherThanAddingOne()
    {
        // The case the first attempt at this got wrong. A departure says how many are left and
        // not who, so afterwards neither viewer is still known to be here -- and an arrival must
        // be able to be the one who stayed. Counting it as a newcomer would put the stream back
        // to two and leave it held open by somebody who had already gone.
        //
        // Deliberately says nothing about which name survives internally: after an unnamed
        // departure there is no such thing.
        using var stream = LiveStream();
        stream.Consumers.Acquire(ViewerA);
        stream.Consumers.Acquire(ViewerB);

        stream.ConsumerCount--;
        Assert.Equal(1, stream.ConsumerCount);

        Assert.False(stream.Consumers.Acquire(ViewerA));
        Assert.Equal(1, stream.ConsumerCount);
    }

    [Fact]
    public void AStreamTwoViewersShareOutlivesTheFirstOfThemToLeave()
    {
        using var stream = LiveStream();
        stream.Consumers.Acquire(ViewerA);
        stream.Consumers.Acquire(ViewerB);

        stream.ConsumerCount--;
        Assert.Equal(1, stream.ConsumerCount);

        stream.ConsumerCount--;
        Assert.Equal(0, stream.ConsumerCount);
    }

    [Fact]
    public void ACloseTooManyIsHarmlessRatherThanEndless()
    {
        // Jellyfin closes a live stream from four places, so one can arrive twice or late. The
        // property is written as a decrement, which on an empty stream asks for minus one; taken
        // literally that spins for ever, and stored literally it would swallow the next viewer.
        using var stream = LiveStream();
        stream.Consumers.Acquire(ViewerA);

        stream.ConsumerCount--;
        stream.ConsumerCount--;
        stream.ConsumerCount--;

        Assert.Equal(0, stream.ConsumerCount);

        Assert.True(stream.Consumers.Acquire(ViewerB));
        Assert.Equal(1, stream.ConsumerCount);
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
