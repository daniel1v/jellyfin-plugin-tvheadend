using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// The buffer one live stream is written into, together with the places a decoder may start
    /// in it.
    /// </summary>
    /// <remarks>
    /// Shared by every kind of live stream so that joining one is the same operation whatever
    /// produced it.
    /// </remarks>
    public sealed class LiveStreamBuffer : IAsyncDisposable
    {
        /// <summary>
        /// Below this the window would be shorter than the lag a client can build up while its
        /// decoder starts, and it would read over its own tail.
        /// </summary>
        public const int MinimumSizeMegabytes = 32;

        private const int RetryDeleteCount = 10;

        private readonly LiveRingBuffer _ring;
        private readonly string _path;

        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="LiveStreamBuffer"/> class.
        /// </summary>
        /// <param name="path">The buffer file.</param>
        /// <param name="sizeMegabytes">The configured buffer window.</param>
        public LiveStreamBuffer(string path, int sizeMegabytes)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);

            _path = path;
            _ring = new LiveRingBuffer(path, Math.Max(MinimumSizeMegabytes, sizeMegabytes) * 1024L * 1024L);
        }

        /// <summary>
        /// Gets the buffer file, which is what Jellyfin hands clients as a local media source.
        /// </summary>
        public string Path => _path;

        /// <summary>
        /// Gets or sets where a decoder may start.
        /// </summary>
        /// <remarks>
        /// Assigned once the container is known. Until then nothing is indexed, which is correct:
        /// a reader arriving that early is served from the beginning anyway.
        /// </remarks>
        public StreamBootstrapIndex? Bootstrap { get; set; }

        /// <summary>
        /// Gets how many bytes have been written.
        /// </summary>
        public long WritePosition => _ring.WritePosition;

        /// <summary>
        /// Gets a value indicating whether the buffer file still exists. A source whose buffer
        /// has gone is worse than no source at all: the client keeps requesting something that
        /// answers 404 instead of opening a fresh stream.
        /// </summary>
        public bool Exists => File.Exists(_path);

        /// <summary>
        /// Appends to the buffer, recording the access points the chunk contains.
        /// </summary>
        /// <param name="data">The bytes to append.</param>
        /// <param name="randomAccessOffsets">Where inside the chunk a decoder may start.</param>
        /// <param name="tables">The program tables valid for this chunk.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes once the bytes are readable.</returns>
        public async ValueTask Write(
            ReadOnlyMemory<byte> data,
            IReadOnlyList<int>? randomAccessOffsets,
            ProgramTableSnapshot tables,
            CancellationToken cancellationToken)
        {
            if (data.IsEmpty)
            {
                return;
            }

            // Taken before the write: positions are relative to the start of this chunk.
            var basePosition = _ring.WritePosition;
            await _ring.WriteAsync(data, cancellationToken).ConfigureAwait(false);

            // Published only once the bytes are readable, and as one pair, so a reader is never
            // sent to a position whose tables it has not been given.
            Bootstrap?.Publish(tables, basePosition, randomAccessOffsets);
        }

        /// <summary>
        /// Opens a reader for a consumer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every consumer is placed at the most recent entry point still inside the window,
        /// preceded by whatever that container needs there. Placing it a fixed distance behind
        /// the live edge instead -- which is what an earlier version did -- lands it in the
        /// middle of a picture with no tables, which is exactly the state a tuner hands over and
        /// which no decoder recovers from.
        /// </para>
        /// <para>
        /// Including the very first consumer, which is why there is no shortcut for a buffer that
        /// has not wrapped yet. A container writes what it likes before the first video keyframe:
        /// measured on a Matroska stream, reading from the first byte gave audio starting at
        /// 0.08 s and video only at 3.28 s, and a player with three seconds of sound and no
        /// picture sits in its buffering state exactly as if the stream were broken.
        /// </para>
        /// </remarks>
        /// <returns>A stream over the buffer.</returns>
        public Stream OpenReader()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (Bootstrap is null)
            {
                return _ring.OpenReaderFromStart(null);
            }

            // Taken as one state. Wherever the reader ends up it is given that state's program
            // tables first: duplicating the ones already in the buffer costs a decoder nothing,
            // and arriving without them is what it cannot recover from.
            var join = Bootstrap.CreateJoin(_ring.OldestPosition);

            // NotYet included: the reader is placed at the live edge and waits there. Every byte
            // behind it belongs to a programme the current tables do not describe, and the first
            // access point of the current one is what it is waiting for.
            var reader = join.Kind == StreamJoinKind.AtPosition
                ? _ring.OpenReaderAt(join.Position, Bootstrap)
                : _ring.OpenReaderFromStart(Bootstrap, join.Kind == StreamJoinKind.NotYet);

            return join.Tables.Length > 0 ? new PrefixedStream(join.Tables, reader) : reader;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _ring.DisposeAsync().ConfigureAwait(false);
            await DeleteBufferFile().ConfigureAwait(false);
        }

        private async Task DeleteBufferFile()
        {
            for (var attempt = 0; attempt <= RetryDeleteCount; attempt++)
            {
                try
                {
                    File.Delete(_path);
                    return;
                }
                catch (IOException) when (attempt < RetryDeleteCount)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }
        }
    }
}
