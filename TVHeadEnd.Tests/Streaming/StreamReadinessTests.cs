using System;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// What a stream has to establish before it is handed over. A channel nothing is known about
/// pays for the answer once; a channel already described does not pay again on every tune.
/// </summary>
public class StreamReadinessTests
{
    private const long Plenty = 4 * 1024 * 1024;

    [Fact]
    public void ADescribedChannelDoesNotWaitForARandomAccessVerdict()
    {
        // The regression this exists for: every tune of every ordinary channel held the picture
        // back for seconds to re-derive a classification that was already stored.
        Assert.True(TvheadendLiveStream.ShouldPublish(
            describedAlready: true,
            isTransportStream: true,
            H264RandomAccessKind.Unknown,
            buffered: Plenty,
            elapsed: TimeSpan.Zero));
    }

    [Fact]
    public void AnUndescribedChannelWaitsForTheVerdict()
    {
        Assert.False(TvheadendLiveStream.ShouldPublish(
            describedAlready: false,
            isTransportStream: true,
            H264RandomAccessKind.Unknown,
            buffered: Plenty,
            elapsed: TimeSpan.Zero));
    }

    [Fact]
    public void AnUndescribedChannelIsPublishedOnceTheVerdictIsIn()
    {
        Assert.True(TvheadendLiveStream.ShouldPublish(
            describedAlready: false,
            isTransportStream: true,
            H264RandomAccessKind.Idr,
            buffered: Plenty,
            elapsed: TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void AnUndescribedChannelIsNotHeldBackForever()
    {
        // A broadcaster that signals nothing the probe recognises must not stall the tune.
        Assert.True(TvheadendLiveStream.ShouldPublish(
            describedAlready: false,
            isTransportStream: true,
            H264RandomAccessKind.Unknown,
            buffered: Plenty,
            elapsed: TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ADescribedChannelStillWaitsForSomethingToPlay()
    {
        Assert.False(TvheadendLiveStream.ShouldPublish(
            describedAlready: true,
            isTransportStream: true,
            H264RandomAccessKind.Idr,
            buffered: 0,
            elapsed: TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ANonTransportStreamIsNotHeldForARandomAccessVerdict()
    {
        // The probe reads MPEG-TS. There is nothing for it to conclude about a Matroska body,
        // so waiting on it would be waiting for something that cannot arrive.
        Assert.True(TvheadendLiveStream.ShouldPublish(
            describedAlready: false,
            isTransportStream: false,
            H264RandomAccessKind.Unknown,
            buffered: Plenty,
            elapsed: TimeSpan.FromSeconds(3)));
    }
}
