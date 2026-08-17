using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// A late reader must not be dropped a fixed distance behind the live edge. That lands it in the
/// middle of a picture with no program tables, which is exactly the state a tuner hands over and
/// which no decoder recovers from on its own.
/// </summary>
public class StreamBootstrapIndexTests
{
    private const int PacketLength = 188;

    [Fact]
    public void WithNoRecordedAccessPointThereIsNoJoinPosition()
    {
        var index = new StreamBootstrapIndex();

        Assert.False(index.TryGetJoinPosition(0, out _));
    }

    [Fact]
    public void TheLatestAccessPointIsChosen()
    {
        // The least delay a reader can safely have; everything recorded has already been written.
        var index = new StreamBootstrapIndex();
        index.RecordRandomAccessPoint(1000);
        index.RecordRandomAccessPoint(5000);
        index.RecordRandomAccessPoint(3000);

        Assert.True(index.TryGetJoinPosition(0, out var position));
        Assert.Equal(5000, position);
    }

    [Fact]
    public void AccessPointsThatHaveBeenOverwrittenAreNotOffered()
    {
        var index = new StreamBootstrapIndex();
        index.RecordRandomAccessPoint(1000);
        index.RecordRandomAccessPoint(2000);

        // The ring has lapped past both.
        Assert.False(index.TryGetJoinPosition(9000, out _));
    }

    [Fact]
    public void AccessPointsStillInsideTheWindowSurvivePruning()
    {
        var index = new StreamBootstrapIndex();
        index.RecordRandomAccessPoint(1000);
        index.RecordRandomAccessPoint(8000);

        Assert.True(index.TryGetJoinPosition(5000, out var position));
        Assert.Equal(8000, position);
    }

    [Fact]
    public void TheBootstrapPrefixCarriesBothProgramTables()
    {
        // Without them the reader cannot map the elementary streams, whatever it joins at.
        var index = new StreamBootstrapIndex();
        index.RecordProgramAssociationTable(TablePacket(0x00));
        index.RecordProgramMapTable(TablePacket(0x2c));

        var prefix = index.CreateBootstrapPrefix();

        Assert.True(index.HasProgramTables);
        Assert.Equal(2 * PacketLength, prefix.Length);
        Assert.Equal(0x47, prefix[0]);
        Assert.Equal(0x47, prefix[PacketLength]);
    }

    [Fact]
    public void WithoutTablesThePrefixIsEmpty()
    {
        var index = new StreamBootstrapIndex();

        Assert.False(index.HasProgramTables);
        Assert.Empty(index.CreateBootstrapPrefix());
    }

    [Fact]
    public void ResetForgetsEveryAccessPoint()
    {
        var index = new StreamBootstrapIndex();
        index.RecordRandomAccessPoint(1000);

        index.Reset();

        Assert.False(index.TryGetJoinPosition(0, out _));
    }

    private static byte[] TablePacket(int pid)
    {
        var packet = new byte[PacketLength];
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | ((pid >> 8) & 0x1F));
        packet[2] = (byte)(pid & 0xFF);
        packet[3] = 0x10;
        return packet;
    }
}
