using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Legacy;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Runs a live stream through FFmpeg on its way into the buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used for two jobs that differ only in their arguments: rewrapping a compatibility stream
    /// TVHeadend produced in the wrong container, and -- transitionally -- encoding one
    /// TVHeadend cannot produce at all.
    /// </para>
    /// <para>
    /// Everything above it -- the variant policy, the media descriptors, the buffer -- sees an
    /// ordinary MPEG-TS source and cannot tell that FFmpeg was involved. The stream it is fed is
    /// the subscription that is already open rather than a second one, so this never costs a
    /// tuner.
    /// </para>
    /// </remarks>
    internal sealed class FfmpegStreamPipe : IAsyncDisposable
    {
        private const int FeedBufferSize = 65536;

        private readonly TranscodeSession _session;
        private readonly Pipe _pipe = new();
        private readonly Task _feed;
        private readonly Stream _source;
        private bool _disposed;

        private FfmpegStreamPipe(
            TranscodeSession session,
            Stream source,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            _session = session;
            _source = source;
            _feed = Pump(logger, cancellationToken);
        }

        /// <summary>
        /// Gets the normalized transport stream, read exactly like an upstream HTTP body.
        /// </summary>
        public Stream Output { get; private set; } = null!;

        /// <summary>
        /// Starts FFmpeg around a source stream.
        /// </summary>
        /// <param name="source">The upstream body, which this takes ownership of reading.</param>
        /// <param name="ffmpegPath">The FFmpeg executable.</param>
        /// <param name="arguments">What FFmpeg is asked to do, one argument per element.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="label">What this session is for, for the log.</param>
        /// <param name="cancellationToken">Cancels the encoder and the feed.</param>
        /// <returns>The running normalizer.</returns>
        public static FfmpegStreamPipe Start(
            Stream source,
            string ffmpegPath,
            IReadOnlyList<string> arguments,
            ILogger logger,
            string label,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrEmpty(ffmpegPath);
            ArgumentNullException.ThrowIfNull(arguments);
            ArgumentNullException.ThrowIfNull(logger);

            Pipe? output = null;
            var session = TranscodeSession.Start(
                ffmpegPath,
                arguments,
                async (chunk, token) =>
                {
                    await output!.Writer.WriteAsync(chunk, token).ConfigureAwait(false);
                },
                logger,
                label,
                cancellationToken);

            var pipe = new FfmpegStreamPipe(session, source, logger, cancellationToken);
            output = pipe._pipe;
            pipe.Output = pipe._pipe.Reader.AsStream();
            return pipe;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                await _feed.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the last consumer goes away.
            }

            await _session.DisposeAsync().ConfigureAwait(false);
            await _source.DisposeAsync().ConfigureAwait(false);
        }

        private async Task Pump(ILogger logger, CancellationToken cancellationToken)
        {
            var buffer = new byte[FeedBufferSize];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    await _session.Input.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                logger.LogDebug(exception, "The transitional H.264 normalizer stopped feeding FFmpeg");
            }
            finally
            {
                // The broadcast has stopped arriving, so let FFmpeg drain what it holds and only
                // then end the output. Ending it here would truncate the last of the stream.
                await _session.CompleteInput().ConfigureAwait(false);

                try
                {
                    await _session.Completion.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The session was cancelled; there is nothing left to drain.
                }

                await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
            }
        }
    }
}
