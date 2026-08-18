using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Holds one playback session's stream on disk, from its first byte, and hands out readers
    /// that follow it as it grows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a ring buffer. A ring exists so a viewer can join a broadcast that has
    /// been running for an hour, which is worth the machinery for the shared native stream and
    /// worth nothing here: a compatibility stream is a rendering made for one session, it starts
    /// when that session starts, and it ends with it. What matters instead is that the beginning
    /// is still there -- the container header is written once, and a reader that never sees it
    /// has nothing it can decode.
    /// </para>
    /// <para>
    /// Because every reader starts at byte zero, the stream can be inspected during startup and
    /// played afterwards without the inspection consuming what the player needs, and
    /// <c>GetStream</c> may be called more than once without the second caller receiving a
    /// headerless fragment.
    /// </para>
    /// </remarks>
    internal sealed class SessionSpool : IAsyncDisposable
    {
        private const int BufferSize = 65536;

        private readonly string _path;
        private readonly FileStream _writer;

        private long _length;
        private bool _completed;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionSpool"/> class.
        /// </summary>
        /// <param name="path">Where to write it. Removed again when the session ends.</param>
        public SessionSpool(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);

            _path = path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("The spool path has no parent directory."));

            _writer = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous);
        }

        /// <summary>
        /// Gets where the session is spooled, for anything that inspects it as a file.
        /// </summary>
        public string Path => _path;

        /// <summary>
        /// Gets how much has been written.
        /// </summary>
        public long Length => Volatile.Read(ref _length);

        /// <summary>
        /// Gets a value indicating whether the source has ended, so a reader that has caught up
        /// is at the end rather than merely waiting.
        /// </summary>
        public bool Completed => Volatile.Read(ref _completed);

        /// <summary>
        /// Appends what arrived from the source.
        /// </summary>
        /// <param name="data">The bytes.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>A task that completes once the bytes are readable.</returns>
        public async ValueTask Append(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (data.IsEmpty)
            {
                return;
            }

            await _writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Published only once the bytes are on disk, so a reader can never be told about
            // content it would then fail to read.
            Volatile.Write(ref _length, _length + data.Length);
        }

        /// <summary>
        /// Records that the source has ended and no more will arrive.
        /// </summary>
        public void Complete() => Volatile.Write(ref _completed, true);

        /// <summary>
        /// Opens a reader over the whole session, which follows it as it grows.
        /// </summary>
        /// <returns>The reader.</returns>
        public Stream OpenReader()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new SpoolReader(_path, this);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Complete();

            await _writer.DisposeAsync().ConfigureAwait(false);

            try
            {
                File.Delete(_path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A reader still holds it open. It was opened with FileShare.Delete, so on
                // Windows the name goes now and the content when the last handle closes.
                _ = exception;
            }
        }

        private sealed class SpoolReader : Stream
        {
            private readonly SessionSpool _spool;
            private readonly FileStream _file;

            private long _position;

            internal SpoolReader(string path, SessionSpool spool)
            {
                _spool = spool;
                _file = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
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

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                var available = _spool.Length - _position;
                if (available <= 0)
                {
                    // Caught up with the writer. Returning zero rather than blocking is what
                    // Jellyfin's ProgressiveFileStream expects: it waits briefly and asks again.
                    // Once the source has ended this is the genuine end, and the same zero says so.
                    return 0;
                }

                var count = (int)Math.Min(buffer.Length, available);
                var read = await _file.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
                _position += read;
                return read;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override void Flush()
            {
            }

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
