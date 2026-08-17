using System;
using System.Linq;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// A Matroska stream in a ring buffer loses its initialisation header the first time the buffer
/// wraps -- about eight minutes at the measured 8.5 Mbit/s. Without the header nothing after
/// that point is decodable, so it is captured while it goes past.
/// </summary>
public class MatroskaBootstrapIndexTests
{
    private static readonly byte[] ClusterId = [0x1F, 0x43, 0xB6, 0x75];

    [Fact]
    public void TheHeaderIsEverythingAheadOfTheFirstCluster()
    {
        var index = new MatroskaBootstrapIndex();
        var header = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x02, 0x03 };

        index.Record(0, Concat(header, ClusterId, [0xAA, 0xBB]), null);

        Assert.True(index.HasHeader);
        Assert.Equal(header, index.CreateBootstrapPrefix());
    }

    [Fact]
    public void ClusterStartsBecomeJoinPositions()
    {
        var index = new MatroskaBootstrapIndex();
        var header = new byte[] { 0x1A, 0x45, 0xDF, 0xA3 };

        index.Record(0, Concat(header, ClusterId), null);
        index.Record(1000, Concat([0x00, 0x00], ClusterId), null);

        Assert.True(index.TryGetJoinPosition(0, out var position));
        Assert.Equal(1002, position);
    }

    [Fact]
    public void WithoutAHeaderThereIsNoJoinPosition()
    {
        // A reader given a cluster without track definitions cannot decode it, so offering the
        // position would be worse than sending it to the start.
        var index = new MatroskaBootstrapIndex();
        index.Record(0, [0xAA, 0xBB, 0xCC], null);

        Assert.False(index.HasHeader);
        Assert.False(index.TryGetJoinPosition(0, out _));
    }

    [Fact]
    public void ClustersStillInsideTheWindowAreOffered()
    {
        var index = new MatroskaBootstrapIndex();
        index.Record(0, Concat([0x1A, 0x45], ClusterId), null);
        index.Record(500, ClusterId, null);

        Assert.True(index.TryGetJoinPosition(400, out var position));
        Assert.Equal(500, position);
    }

    [Fact]
    public void ClustersOverwrittenByTheRingAreNotOffered()
    {
        var index = new MatroskaBootstrapIndex();
        index.Record(0, Concat([0x1A, 0x45], ClusterId), null);
        index.Record(500, ClusterId, null);

        Assert.False(index.TryGetJoinPosition(9000, out _));
    }

    [Fact]
    public void AnIdentifierSplitAcrossChunksIsStillFound()
    {
        // The upstream body arrives in chunks that respect nothing.
        var index = new MatroskaBootstrapIndex();
        index.Record(0, Concat([0x1A, 0x45, 0xDF, 0xA3], ClusterId), null);
        index.Record(8, [0x00, 0x1F, 0x43], null);
        index.Record(11, [0xB6, 0x75, 0x11], null);

        Assert.True(index.TryGetJoinPosition(0, out var position));
        Assert.Equal(9, position);
    }

    [Fact]
    public void SomethingThatIsNotMatroskaNeverClaimsAHeader()
    {
        var index = new MatroskaBootstrapIndex();
        index.Record(0, Enumerable.Repeat((byte)0x47, 4096).ToArray(), null);

        Assert.False(index.HasHeader);
        Assert.Empty(index.CreateBootstrapPrefix());
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];
}
