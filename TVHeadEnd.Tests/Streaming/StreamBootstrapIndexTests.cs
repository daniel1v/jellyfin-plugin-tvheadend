using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// Where a reader joins a running stream, and what it is given when it does.
/// </summary>
/// <remarks>
/// A late reader must not be dropped a fixed distance behind the live edge: that lands it in the
/// middle of a picture with no program tables, which is exactly the state a tuner hands over and
/// which no decoder recovers from on its own. The tables and the position are therefore one state,
/// and these tests are mostly about the ways it must not come apart.
/// </remarks>
public class StreamBootstrapIndexTests
{
    private const int PacketLength = 188;

    [Fact]
    public void WithNothingRecordedThereIsNoJoinPosition()
    {
        var index = new StreamBootstrapIndex();

        var join = index.CreateJoin(0);

        Assert.Null(join.Position);
        Assert.Empty(join.Tables);
    }

    [Fact]
    public void TheLatestAccessPointIsChosen()
    {
        // The least delay a reader can safely have; everything recorded has already been written.
        var index = new StreamBootstrapIndex();
        index.Publish(Tables(generation: 0), basePosition: 0, [1000, 5000, 3000]);

        Assert.Equal(5000, index.CreateJoin(0).Position);
    }

    [Fact]
    public void AccessPointsThatHaveBeenOverwrittenAreNotOffered()
    {
        var index = new StreamBootstrapIndex();
        index.Publish(Tables(generation: 0), basePosition: 0, [1000, 2000]);

        // The ring has lapped past both. The tables still go out, so a reader taking the oldest
        // bytes can at least map the streams once it resynchronises.
        var join = index.CreateJoin(9000);

        Assert.Null(join.Position);
        Assert.Equal(2 * PacketLength, join.Tables.Length);
    }

    [Fact]
    public void AccessPointsStillInsideTheWindowSurvivePruning()
    {
        var index = new StreamBootstrapIndex();
        index.Publish(Tables(generation: 0), basePosition: 0, [1000, 8000]);

        Assert.Equal(8000, index.CreateJoin(5000).Position);
    }

    [Fact]
    public void AJoinCarriesBothProgramTables()
    {
        // Without them the reader cannot map the elementary streams, whatever it joins at.
        var index = new StreamBootstrapIndex();
        index.Publish(Tables(generation: 0), basePosition: 0, [500]);

        var join = index.CreateJoin(0);

        Assert.True(index.HasProgramTables);
        Assert.Equal(2 * PacketLength, join.Tables.Length);
        Assert.Equal(0x47, join.Tables[0]);
        Assert.Equal(0x47, join.Tables[PacketLength]);
    }

    [Fact]
    public void AnAccessPointWithNoTablesToDescribeItIsNotRecorded()
    {
        // A position by itself is the state a tuner hands over. Storing it would let a reader be
        // sent into the middle of a picture with nothing to map the streams by.
        var index = new StreamBootstrapIndex();

        index.Publish(ProgramTableSnapshot.Empty, basePosition: 0, [1000]);

        Assert.False(index.HasProgramTables);
        Assert.Null(index.CreateJoin(0).Position);
    }

    [Fact]
    public void ANewProgramLayoutDiscardsThePositionsFoundUnderTheOldOne()
    {
        // The invariant the whole index exists for: whatever a reader is given, the tables and the
        // position it gets belong to the same programme.
        var index = new StreamBootstrapIndex();
        index.Publish(Tables(generation: 0), basePosition: 0, [1000, 2000]);

        index.Publish(Tables(generation: 1), basePosition: 3000, [500]);

        Assert.Equal(3500, index.CreateJoin(0).Position);
    }

    [Fact]
    public void ALayoutChangeWithNoAccessPointYetLeavesNothingToJoinAt()
    {
        // Between the change and the first access point under it there is genuinely nowhere to
        // send a reader, and saying so is better than sending it to the picture before.
        var index = new StreamBootstrapIndex();
        index.Publish(Tables(generation: 0), basePosition: 0, [1000]);

        index.Publish(Tables(generation: 1), basePosition: 3000, null);

        Assert.Null(index.CreateJoin(0).Position);
        Assert.True(index.HasProgramTables);
    }

    private static ProgramTableSnapshot Tables(int generation)
        => new([TablePacket(0x00)], [TablePacket(0x2c)], generation);

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
