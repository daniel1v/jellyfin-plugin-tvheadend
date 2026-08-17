using System;
using System.IO;
using System.Text;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

public sealed class IdrScanTests : IDisposable
{
    private const int PacketLength = 188;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"idrscan-{Guid.NewGuid():N}.ts");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SomethingThatIsNotATransportStreamRaisesNoObjection()
    {
        // The question is about H.264 in a transport stream. A Matroska recording -- which a
        // TVHeadend server running a WebTV profile produces -- says nothing about it.
        File.WriteAllBytes(_path, Encoding.ASCII.GetBytes(new string('x', 4096)));

        Assert.NotEqual(H264RandomAccessKind.RecoveryOpenGop, SourceDescriber.ScanRandomAccess(_path));
    }

    [Fact]
    public void ATransportStreamWithoutVideoTerminatesAndRaisesNoObjection()
    {
        // The case that hung an earlier attempt: it bounded the scan on the scanner's own byte
        // counter, which only advances for H.264 video packets, so on a recording carrying none
        // it never reached its limit and read the whole file. Bounded by the sample instead,
        // this ends whatever the sample holds.
        var stream = new byte[64 * PacketLength];
        for (var packet = 0; packet < 64; packet++)
        {
            var offset = packet * PacketLength;
            stream[offset] = 0x47;

            // A PID this conditioner never treats as video, and no program tables at all.
            stream[offset + 1] = 0x10;
            stream[offset + 2] = 0x64;
            stream[offset + 3] = 0x10;
        }

        File.WriteAllBytes(_path, stream);

        Assert.NotEqual(H264RandomAccessKind.RecoveryOpenGop, SourceDescriber.ScanRandomAccess(_path));
    }

    [Fact]
    public void AnEmptySampleRaisesNoObjection()
    {
        File.WriteAllBytes(_path, []);

        Assert.NotEqual(H264RandomAccessKind.RecoveryOpenGop, SourceDescriber.ScanRandomAccess(_path));
    }

    [Fact]
    public void AMissingSamplePathIsRefused()
    {
        Assert.ThrowsAny<ArgumentException>(() => SourceDescriber.ScanRandomAccess(string.Empty));
    }
}
