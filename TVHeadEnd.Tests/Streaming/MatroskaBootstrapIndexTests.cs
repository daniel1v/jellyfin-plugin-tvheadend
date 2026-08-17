using System;
using System.Collections.Generic;
using System.Linq;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// A Matroska stream in a ring buffer loses its initialisation header the first time the buffer
/// wraps, so it is captured while it goes past. Cluster positions have to come from parsing the
/// element tree: the four bytes of the cluster identifier occur constantly inside compressed
/// video, and searching for them lands readers in the middle of a picture.
/// </summary>
public class MatroskaBootstrapIndexTests
{
    [Fact]
    public void TheHeaderIsEverythingAheadOfTheFirstCluster()
    {
        var index = new MatroskaBootstrapIndex();
        var header = Concat(EbmlHeader(), SegmentStart(), Tracks());

        index.Record(0, Concat(header, Cluster(), Cluster()), null);

        Assert.True(index.HasHeader);
        Assert.Equal(header, index.CreateBootstrapPrefix());
    }

    [Fact]
    public void ClusterStartsBecomeJoinPositions()
    {
        var index = new MatroskaBootstrapIndex();
        var header = Concat(EbmlHeader(), SegmentStart(), Tracks());
        var first = Cluster();

        index.Record(0, Concat(header, first, Cluster()), null);

        Assert.True(index.TryGetJoinPosition(0, out var position));
        Assert.Equal(header.Length + first.Length, position);
    }

    [Fact]
    public void VideoPayloadThatLooksLikeAClusterIsNotOne()
    {
        // The defect this parser exists for. These four bytes appear all the time inside an
        // eight megabit stream; what follows them is not a readable size and a cluster child.
        var index = new MatroskaBootstrapIndex();
        var noise = Concat([0x1F, 0x43, 0xB6, 0x75], [0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        var header = Concat(EbmlHeader(), SegmentStart(), Tracks());
        var real = Cluster();

        index.Record(0, Concat(header, ClusterContaining(noise), real, Filler(64)), null);

        Assert.True(index.TryGetJoinPosition(0, out var position));

        // The only positions offered are the two real clusters, never the noise between them.
        Assert.Equal(header.Length + ClusterContaining(noise).Length, position);
    }

    [Fact]
    public void WithoutAHeaderThereIsNoJoinPosition()
    {
        var index = new MatroskaBootstrapIndex();
        index.Record(0, Filler(256), null);

        Assert.False(index.HasHeader);
        Assert.False(index.TryGetJoinPosition(0, out _));
    }

    [Fact]
    public void ClustersOverwrittenByTheRingAreNotOffered()
    {
        var index = new MatroskaBootstrapIndex();
        index.Record(0, Concat(EbmlHeader(), SegmentStart(), Tracks(), Cluster(), Filler(64)), null);

        Assert.False(index.TryGetJoinPosition(900000, out _));
    }

    [Fact]
    public void AnElementSplitAcrossChunksIsStillFound()
    {
        // The upstream body arrives in chunks that respect nothing.
        var index = new MatroskaBootstrapIndex();
        var stream = Concat(EbmlHeader(), SegmentStart(), Tracks(), Cluster(), Cluster(), Filler(64));

        foreach (var chunk in Chunks(stream, 7))
        {
            index.Record(chunk.Position, chunk.Data, null);
        }

        Assert.True(index.HasHeader);
        Assert.True(index.TryGetJoinPosition(0, out _));
    }

    [Fact]
    public void SomethingThatIsNotMatroskaNeverClaimsAHeader()
    {
        var index = new MatroskaBootstrapIndex();
        index.Record(0, Enumerable.Repeat((byte)0x47, 4096).ToArray(), null);

        Assert.False(index.HasHeader);
        Assert.Empty(index.CreateBootstrapPrefix());
    }

    [Fact]
    public void MatroskaPositionsAreUsedExactly()
    {
        // Rounding a position down to a transport stream packet boundary moves it into the
        // middle of an element.
        Assert.Equal(1, new MatroskaBootstrapIndex().Alignment);
    }

    private static byte[] EbmlHeader() => Concat([0x1A, 0x45, 0xDF, 0xA3], [0x84], [0x01, 0x02, 0x03, 0x04]);

    private static byte[] SegmentStart() => Concat([0x18, 0x53, 0x80, 0x67], [0xFF]);

    private static byte[] Tracks() => Concat([0x16, 0x54, 0xAE, 0x6B], [0x84], [0x11, 0x22, 0x33, 0x44]);

    /// <summary>
    /// A cluster of unknown size, as live Matroska writes them, beginning with a timecode.
    /// </summary>
    private static byte[] Cluster() => ClusterContaining([]);

    private static byte[] ClusterContaining(byte[] payload)
        => Concat([0x1F, 0x43, 0xB6, 0x75], [0xFF], [0xE7, 0x81, 0x00], payload);

    private static byte[] Filler(int length) => Enumerable.Repeat((byte)0x5A, length).ToArray();

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    private static IEnumerable<(long Position, byte[] Data)> Chunks(byte[] data, int size)
    {
        for (var offset = 0; offset < data.Length; offset += size)
        {
            yield return (offset, data[offset..Math.Min(offset + size, data.Length)]);
        }
    }
}
