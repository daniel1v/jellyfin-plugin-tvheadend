using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.IO;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// A fixed-size file the live stream is written into in a circle, so a channel left running
    /// costs the same disk space after eight hours as after one minute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Positions are logical: they count every byte ever written and never restart, while the
    /// file offset behind them wraps. Readers therefore address the stream exactly as they would
    /// a growing file, and only the bytes older than the window are gone.
    /// </para>
    /// <para>
    /// This is only sound because Jellyfin serves every consumer through
    /// <see cref="System.IO.Stream"/>s obtained from the live stream -- both direct play and the
    /// LiveStreamFiles endpoint that FFmpeg reads from wrap the result of GetStream in a
    /// ProgressiveFileStream. Nothing opens the buffer by path, so nothing observes the wrap.
    /// </para>
    /// </remarks>
    internal sealed class LiveRingBuffer : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Reading across the wrap in one go would need two file reads; readers simply stop at
        /// the seam and continue on the next call, which costs nothing because
        /// ProgressiveFileStream reads in a loop anyway.
        /// </summary>
        private const int TransportStreamPacketSize = 188;

        /// <summary>
        /// A FileStream buffer size of one disables buffering, which a circular file requires on
        /// both sides: see the writer and the reader for why.
        /// </summary>
        private const int UnbufferedFileStream = 1;

        private readonly string _path;
        private readonly long _capacity;
        private readonly FileStream _writer;

        private long _writePosition;

        internal LiveRingBuffer(string path, long capacity)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, TransportStreamPacketSize);

            _path = path;
            _capacity = capacity - (capacity % TransportStreamPacketSize);

            // Unbuffered on purpose. A buffered FileStream caches file contents around the
            // current offset, and in a ring that cache goes stale the moment the writer laps
            // it -- readers would be served bytes that have since been overwritten.
            _writer = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                UnbufferedFileStream,
                FileOptions.Asynchronous);
        }

        /// <summary>
        /// Gets the number of bytes written since the buffer was created. Never decreases, and
        /// keeps counting past the capacity.
        /// </summary>
        internal long WritePosition => Volatile.Read(ref _writePosition);

        /// <summary>
        /// Gets the size of the window, which is what the buffer costs on disk once filled.
        /// </summary>
        internal long Capacity => _capacity;

        /// <summary>
        /// Gets the oldest position still readable. Everything before it has been overwritten.
        /// </summary>
        internal long OldestPosition => Math.Max(0, WritePosition - _capacity);

        public void Dispose()
        {
            _writer.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        internal async Task WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            // A single write larger than the whole window would lap itself; only the tail of it
            // could survive, so that is all that is written. In practice the pump writes chunks
            // far smaller than the capacity and this never triggers.
            if (source.Length > _capacity)
            {
                var skipped = source.Length - (int)_capacity;
                Volatile.Write(ref _writePosition, _writePosition + skipped);
                source = source[skipped..];
            }

            var offset = _writePosition % _capacity;
            var untilWrap = (int)Math.Min(source.Length, _capacity - offset);

            _writer.Seek(offset, SeekOrigin.Begin);
            await _writer.WriteAsync(source[..untilWrap], cancellationToken).ConfigureAwait(false);

            if (untilWrap < source.Length)
            {
                _writer.Seek(0, SeekOrigin.Begin);
                await _writer.WriteAsync(source[untilWrap..], cancellationToken).ConfigureAwait(false);
            }

            // No flush needed: the stream is unbuffered, so the writes above have already
            // reached the operating system and are visible to the readers' own handles.
            // Published last: a reader must never be told about bytes that are not yet there.
            Volatile.Write(ref _writePosition, _writePosition + source.Length);
        }

        /// <summary>
        /// Opens a reader at the very beginning of what the buffer still holds, for a consumer
        /// that has to see the program tables the stream opened with.
        /// </summary>
        /// <param name="bootstrap">
        /// What a reader that falls out of the window is re-joined with, or <see langword="null"/>
        /// to move it to the oldest bytes still present.
        /// </param>
        /// <returns>A stream over the buffer.</returns>
        internal Stream OpenReaderFromStart(StreamBootstrapIndex? bootstrap)
        {
            return new RingReader(_path, this, AlignToPacket(OldestPosition), bootstrap);
        }

        /// <summary>
        /// Opens a reader at a specific logical position, which
        /// <see cref="StreamBootstrapIndex"/> established is a place a decoder may start.
        /// </summary>
        /// <param name="position">The logical position to start reading at.</param>
        /// <param name="bootstrap">
        /// The index the reader re-joins through if the writer laps it.
        /// </param>
        /// <returns>A stream over the buffer.</returns>
        internal Stream OpenReaderAt(long position, StreamBootstrapIndex bootstrap)
        {
            ArgumentNullException.ThrowIfNull(bootstrap);

            var clamped = Math.Clamp(position, OldestPosition, WritePosition);
            return new RingReader(_path, this, AlignToPacket(clamped), bootstrap);
        }

        private static long AlignToPacket(long position) => position - (position % TransportStreamPacketSize);

        /// <summary>
        /// Reads the circle back as if it were an ordinary growing file.
        /// </summary>
        private sealed class RingReader : Stream
        {
            private readonly LiveRingBuffer _buffer;
            private readonly FileStream _file;
            private readonly StreamBootstrapIndex? _bootstrap;

            private long _position;
            private byte[]? _pendingPrefix;
            private int _pendingPrefixPosition;

            internal RingReader(string path, LiveRingBuffer buffer, long start, StreamBootstrapIndex? bootstrap)
            {
                _buffer = buffer;
                _position = start;
                _bootstrap = bootstrap;
                // Unbuffered: a read-ahead buffer would keep serving bytes the writer has
                // already overwritten once it laps this reader.
                _file = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    UnbufferedFileStream,
                    FileOptions.Asynchronous);
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
                => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                // Whatever a re-join queued up goes out before buffer content resumes.
                if (TryServePrefix(buffer.Span, out var served))
                {
                    return served;
                }

                var writePosition = _buffer.WritePosition;

                // Caught up with the writer. Returning zero rather than blocking is what
                // ProgressiveFileStream expects: it waits 50 ms and asks again.
                if (_position >= writePosition)
                {
                    return 0;
                }

                var oldest = _buffer.OldestPosition;
                if (_position < oldest)
                {
                    ReJoin(oldest);

                    // Checked again straight away, so the tables reach the decoder before the
                    // bytes they describe rather than one read behind them.
                    if (TryServePrefix(buffer.Span, out served))
                    {
                        return served;
                    }
                }

                var available = writePosition - _position;
                var fileOffset = _position % _buffer.Capacity;
                var count = (int)Math.Min(
                    Math.Min(buffer.Length, available),
                    _buffer.Capacity - fileOffset);

                _file.Seek(fileOffset, SeekOrigin.Begin);
                var read = await _file.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
                _position += read;
                return read;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            /// <summary>
            /// Puts a reader the writer has lapped back onto a place a decoder can continue from.
            /// </summary>
            /// <remarks>
            /// The same treatment a reader gets when it first joins, and for the same reason: the
            /// oldest surviving byte is wherever the ring happened to wrap, which is the middle of
            /// a picture with no tables in front of it. Moving there and carrying on is how a
            /// client that paused too long came back to a decoder that never recovered.
            /// </remarks>
            /// <summary>
            /// Hands over as much of a queued prefix as fits.
            /// </summary>
            /// <param name="destination">The caller's buffer.</param>
            /// <param name="served">How many bytes were written.</param>
            /// <returns>Whether a prefix was being delivered.</returns>
            private bool TryServePrefix(Span<byte> destination, out int served)
            {
                served = 0;
                if (_pendingPrefix is not { } prefix || destination.IsEmpty)
                {
                    return false;
                }

                served = Math.Min(destination.Length, prefix.Length - _pendingPrefixPosition);
                prefix.AsSpan(_pendingPrefixPosition, served).CopyTo(destination);
                _pendingPrefixPosition += served;

                if (_pendingPrefixPosition >= prefix.Length)
                {
                    _pendingPrefix = null;
                    _pendingPrefixPosition = 0;
                }

                return true;
            }

            /// <remarks>
            /// The reader has been lapped by the writer, which is the same problem as joining for
            /// the first time and is answered the same way: one state, taken once. Where no access
            /// point survives in the window, reading on from the oldest bytes is all that is left,
            /// and the tables still go in front so the decoder can map the streams once it
            /// resynchronises.
            /// </remarks>
            /// <param name="oldest">The oldest position the buffer still holds.</param>
            private void ReJoin(long oldest)
            {
                if (_bootstrap is null)
                {
                    _position = AlignToPacket(oldest);
                    return;
                }

                var join = _bootstrap.CreateJoin(oldest);
                _position = AlignToPacket(join.Position ?? oldest);

                if (join.Tables.Length > 0)
                {
                    _pendingPrefix = join.Tables;
                    _pendingPrefixPosition = 0;
                }
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _file.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
