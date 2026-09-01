using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TVHeadEnd.Infrastructure.LiveBuffer
{
    /// <summary>
    /// Delivers a fixed run of bytes before a stream, without copying the stream.
    /// </summary>
    /// <remarks>
    /// A reader that joins a channel already running has to be given the program tables before
    /// the buffer content, because the ones the stream opened with have long since scrolled out
    /// of the window.
    /// </remarks>
    internal sealed class PrefixedStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _inner;

        private int _prefixPosition;

        /// <summary>
        /// Initializes a new instance of the <see cref="PrefixedStream"/> class.
        /// </summary>
        /// <param name="prefix">The bytes to deliver first.</param>
        /// <param name="inner">The stream to continue with.</param>
        public PrefixedStream(byte[] prefix, Stream inner)
        {
            ArgumentNullException.ThrowIfNull(prefix);
            ArgumentNullException.ThrowIfNull(inner);

            _prefix = prefix;
            _inner = inner;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => _prefixPosition + _inner.Position;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixPosition < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _prefixPosition);
                _prefix.AsMemory(_prefixPosition, count).CopyTo(buffer);
                _prefixPosition += count;
                return count;
            }

            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
