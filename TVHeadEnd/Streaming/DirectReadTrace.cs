using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Records that someone read the live buffer directly, and how far they got.
/// </summary>
/// <remarks>
/// A diagnostic, and meant to be removed once it has answered one question: whether the Android
/// client ever fetches the transport stream it has just negotiated. The server logs the playback
/// decision but not the fetch, so a client that agrees to direct play and then quietly asks again
/// with direct play disabled looks, in the log, exactly like a client that tried and failed.
/// </remarks>
internal sealed class DirectReadTrace : Stream
{
    private readonly Stream _inner;
    private readonly string _channelId;
    private readonly ILogger _logger;
    private long _delivered;
    private bool _announcedFirstBytes;

    public DirectReadTrace(Stream inner, string channelId, ILogger logger)
    {
        _inner = inner;
        _channelId = channelId;
        _logger = logger;

        _logger.LogInformation("Live TV: direct read of channel {ChannelId} begins", _channelId);
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
        => Note(_inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => Note(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Note(int read)
    {
        _delivered += read;

        if (!_announcedFirstBytes && read > 0)
        {
            _announcedFirstBytes = true;
            _logger.LogInformation(
                "Live TV: direct read of channel {ChannelId} delivered its first {Count} bytes",
                _channelId,
                read);
        }

        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _logger.LogInformation(
                "Live TV: direct read of channel {ChannelId} ended after {Delivered} bytes",
                _channelId,
                _delivered);

            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
