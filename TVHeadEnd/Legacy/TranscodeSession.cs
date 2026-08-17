using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Legacy
{
    /// <summary>
    /// One running FFmpeg process, together with everything attached to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session owns the process, its three pipes and the tasks that service them. Nothing is
    /// started and forgotten: <see cref="DisposeAsync"/> cancels, closes the input so FFmpeg can
    /// flush, awaits every task it started, and kills the process if it has not gone by then.
    /// An earlier version started the output pump and the stderr monitor with discarded tasks,
    /// which meant a failure in either surfaced nowhere and a cancelled stream could leave the
    /// process behind.
    /// </para>
    /// <para>
    /// The process is always fed through a pipe rather than pointed at the source. For a live
    /// channel that avoids opening a second TVHeadend subscription for a channel already being
    /// received; for a recording it is what stops FFmpeg seeking back after its analysis, which
    /// TVHeadend answers by dropping the connection.
    /// </para>
    /// </remarks>
    internal sealed class TranscodeSession : IAsyncDisposable
    {
        private const int PumpBufferSize = 131072;
        private const int StderrTailLines = 12;

        private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

        private readonly Process _process;
        private readonly CancellationTokenSource _cancellation;
        private readonly ILogger _logger;
        private readonly string _label;
        private readonly Task _outputTask;
        private readonly Task _errorTask;
        private readonly Queue<string> _stderrTail = new(StderrTailLines);

        private bool _disposed;

        private TranscodeSession(
            Process process,
            CancellationTokenSource cancellation,
            ILogger logger,
            string label,
            Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeOutput)
        {
            _process = process;
            _cancellation = cancellation;
            _logger = logger;
            _label = label;

            _errorTask = ReadStandardError(cancellation.Token);
            _outputTask = PumpStandardOutput(writeOutput, cancellation.Token);
        }

        /// <summary>
        /// Gets the stream FFmpeg reads its input from.
        /// </summary>
        public Stream Input => _process.StandardInput.BaseStream;

        /// <summary>
        /// Gets a task that completes once FFmpeg has written its last output byte.
        /// </summary>
        /// <remarks>
        /// Lets a caller that turns this output into a stream close it at the right moment,
        /// rather than leaving a reader waiting on a process that has already finished.
        /// </remarks>
        public Task Completion => _outputTask;

        /// <summary>
        /// Gets a value indicating whether the process has ended.
        /// </summary>
        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// Gets the exit code, valid once <see cref="HasExited"/> is set.
        /// </summary>
        public int ExitCode => HasExited ? _process.ExitCode : 0;

        /// <summary>
        /// Starts FFmpeg and begins servicing its pipes.
        /// </summary>
        /// <param name="ffmpegPath">The FFmpeg executable.</param>
        /// <param name="arguments">The argument list, one argument per element.</param>
        /// <param name="writeOutput">Receives everything FFmpeg writes to standard output.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="label">What this session is for, for the log.</param>
        /// <param name="cancellationToken">Cancels the session.</param>
        /// <returns>The running session.</returns>
        public static TranscodeSession Start(
            string ffmpegPath,
            IReadOnlyList<string> arguments,
            Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeOutput,
            ILogger logger,
            string label,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(ffmpegPath);
            ArgumentNullException.ThrowIfNull(arguments);
            ArgumentNullException.ThrowIfNull(writeOutput);
            ArgumentNullException.ThrowIfNull(logger);

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException($"The FFmpeg process for {label} could not be started.");
            }

            logger.LogInformation("TVHeadend transcode {Label}: FFmpeg started, pid {ProcessId}", label, process.Id);

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return new TranscodeSession(process, cancellation, logger, label, writeOutput);
        }

        /// <summary>
        /// Closes the input so FFmpeg drains what it has, without waiting for it to finish.
        /// </summary>
        /// <returns>A task that completes once the input is closed.</returns>
        public async Task CompleteInput()
        {
            try
            {
                await Input.FlushAsync().ConfigureAwait(false);
                Input.Close();
            }
            catch (IOException)
            {
                // FFmpeg may already have gone away.
            }
            catch (ObjectDisposedException)
            {
                // Already closed.
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Closing the input first lets a healthy process flush and exit on its own.
            await CompleteInput().ConfigureAwait(false);

            try
            {
                await _process.WaitForExitAsync(new CancellationTokenSource(ShutdownGrace).Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill();
            }

            await _cancellation.CancelAsync().ConfigureAwait(false);

            // Both are owned here, so both are awaited here. Neither throws: each swallows what
            // its own end of a dying pipe produces.
            await Task.WhenAll(_outputTask, _errorTask).ConfigureAwait(false);

            Kill();
            ReportExit();

            _cancellation.Dispose();
            _process.Dispose();
        }

        private void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process ended between the check and the kill.
            }
            catch (Win32Exception)
            {
                // Not killable any more; it is already terminating.
            }
        }

        private void ReportExit()
        {
            string tail;
            lock (_stderrTail)
            {
                tail = string.Join(" | ", _stderrTail);
            }

            if (HasExited && ExitCode != 0)
            {
                _logger.LogWarning(
                    "TVHeadend transcode {Label}: FFmpeg ended with {ExitCode}: {StderrTail}",
                    _label,
                    ExitCode,
                    tail);
            }
            else
            {
                _logger.LogInformation("TVHeadend transcode {Label}: FFmpeg ended", _label);
            }
        }

        private async Task PumpStandardOutput(
            Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeOutput,
            CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(PumpBufferSize);
            try
            {
                var output = _process.StandardOutput.BaseStream;
                while (true)
                {
                    var read = await output.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await writeOutput(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the session is torn down.
            }
            catch (IOException)
            {
                // FFmpeg went away; the stderr tail reports why.
            }
            catch (ObjectDisposedException)
            {
                // The sink was released while the encoder was still draining.
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "TVHeadend transcode {Label}: carrying the output failed", _label);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task ReadStandardError(CancellationToken cancellationToken)
        {
            try
            {
                string? line;
                while ((line = await _process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                {
                    lock (_stderrTail)
                    {
                        if (_stderrTail.Count == StderrTailLines)
                        {
                            _stderrTail.Dequeue();
                        }

                        _stderrTail.Enqueue(line);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the session is torn down.
            }
            catch (IOException)
            {
                // The pipe closed with the process.
            }
            catch (ObjectDisposedException)
            {
                // Same.
            }
        }
    }
}
