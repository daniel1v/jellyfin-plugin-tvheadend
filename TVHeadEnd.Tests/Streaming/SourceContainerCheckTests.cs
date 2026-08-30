using System;
using System.Linq;
using TVHeadEnd.Core.Media;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// Deciding what TVHeadend is sending, from however the bytes happen to arrive.
/// </summary>
/// <remarks>
/// The regression this guards: a read is not a message. One <c>ReadAsync</c> returns whatever has
/// arrived, and judging a transport stream on a short first read rejects a perfectly good channel
/// with an error blaming the server's profile configuration.
/// </remarks>
public class SourceContainerCheckTests
{
    private const int PacketLength = 188;

    [Fact]
    public void AShortFirstReadDecidesNothing()
    {
        // Fewer bytes than the proof needs. The honest answer is "not yet", not "no".
        var check = new SourceContainerCheck();

        Assert.Equal(SourceContainerVerdict.Undecided, check.Accept(TransportStream(2)));
    }

    [Fact]
    public void ATransportStreamArrivingInSmallReadsIsStillRecognised()
    {
        // The failure as it actually happened: enough bytes overall, never enough in one read.
        var check = new SourceContainerCheck();
        var stream = TransportStream(6);
        var verdict = SourceContainerVerdict.Undecided;

        for (var offset = 0; offset < stream.Length && verdict == SourceContainerVerdict.Undecided; offset += 100)
        {
            verdict = check.Accept(stream.AsSpan(offset, Math.Min(100, stream.Length - offset)));
        }

        Assert.Equal(SourceContainerVerdict.TransportStream, verdict);
    }

    [Fact]
    public void ATransportStreamIsRecognisedFromOneLargeRead()
    {
        var check = new SourceContainerCheck();

        Assert.Equal(SourceContainerVerdict.TransportStream, check.Accept(TransportStream(8)));
    }

    [Fact]
    public void SomethingElseIsRefusedOnceEnoughOfItHasArrived()
    {
        // Matroska, which is what a TVHeadend configured for one of the WebTV profiles serves.
        var check = new SourceContainerCheck();
        var other = new byte[SourceContainer.ConclusiveLength];
        other[0] = 0x1A;
        other[1] = 0x45;
        other[2] = 0xDF;
        other[3] = 0xA3;

        Assert.Equal(SourceContainerVerdict.NotTransportStream, check.Accept(other));
    }

    [Fact]
    public void AnAnswerOnceGivenDoesNotChange()
    {
        var check = new SourceContainerCheck();
        Assert.Equal(SourceContainerVerdict.TransportStream, check.Accept(TransportStream(8)));

        // Whatever follows, including the middle of a packet that happens not to look like one.
        Assert.Equal(SourceContainerVerdict.TransportStream, check.Accept(new byte[4096]));
    }

    [Fact]
    public void AStreamThatDoesNotBeginOnAPacketBoundaryIsStillRecognised()
    {
        // A tuner hands over at whatever byte is next, so the first sync byte can be anywhere in
        // the first packet.
        var check = new SourceContainerCheck();
        var stream = TransportStream(8);
        var offset = new byte[97].Concat(stream).ToArray();

        Assert.Equal(SourceContainerVerdict.TransportStream, check.Accept(offset));
    }

    private static byte[] TransportStream(int packets)
    {
        var stream = new byte[packets * PacketLength];
        for (var packet = 0; packet < packets; packet++)
        {
            stream[packet * PacketLength] = 0x47;
        }

        return stream;
    }
}
