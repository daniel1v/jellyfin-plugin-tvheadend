using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Re-encodes the video of a live broadcast so that it has IDR pictures to start on.
/// </summary>
/// <remarks>
/// <para>
/// For one measured defect and nothing else. Some DVB H.264 services never transmit an IDR
/// picture: their access points are I-frames marked by the random access indicator and a recovery
/// point message, which FFmpeg starts on happily and Android's MediaCodec does not start on at
/// all -- it takes the samples at full rate, emits no frame and raises no error. Cutting the video
/// and putting it back with closed GOPs whose keyframes are IDR is what makes those channels play
/// there; a differential measurement on the same capture showed the copy without IDRs never
/// rendering a frame and the re-encode rendering one in about a fifth of a second.
/// </para>
/// <para>
/// Everything above this sees an ordinary transport stream and cannot tell which side produced it.
/// The conditioner parses the tables the encoder emits, the description comes from those tables,
/// and the result goes into the same ring buffer as an untouched broadcast. Audio is copied, so
/// only the video is paid for.
/// </para>
/// <para>
/// The encoder is fed the subscription that is already open rather than a second one, which is
/// what keeps this from costing a tuner.
/// </para>
/// </remarks>
internal sealed class H264IdrNormalizer : IAsyncDisposable
{
    private const int FeedBufferSize = 65536;

    /// <summary>
    /// The PMT stream type of H.264. The only video the IDR question belongs to: the same
    /// bytes in MPEG-2 are a slice start code for picture row five.
    /// </summary>
    private const byte H264StreamType = 0x1B;

    private readonly Pipe _pipe = new();
    private readonly Stream _source;
    private readonly FfmpegSession _session;
    private readonly Task _feed;

    private bool _disposed;

    private H264IdrNormalizer(FfmpegSession session, Stream source, ILogger logger, CancellationToken cancellationToken)
    {
        _session = session;
        _source = source;
        _feed = Pump(logger, cancellationToken);
    }

    /// <summary>
    /// Gets the re-encoded transport stream, read exactly like an upstream HTTP body.
    /// </summary>
    public Stream Output { get; private set; } = null!;

    /// <summary>
    /// Reports whether a stream has to be re-encoded for the client asking for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three conditions, all of which have to hold, and the whole of the rule. The client's
    /// decoder needs an IDR picture to start; the video is H.264, which is the only syntax the
    /// word IDR belongs to; and the picture the broadcast offered as an access point was found
    /// not to contain one.
    /// </para>
    /// <para>
    /// Everything absent or unsettled answers no. A stream that has not been examined long enough
    /// to say, a client that did not identify itself, MPEG-2 or HEVC video: each takes the
    /// ordinary path, where the broadcast reaches the viewer as it was transmitted.
    /// </para>
    /// </remarks>
    /// <param name="clientNeedsIdr">Whether the asking client's decoder needs an IDR picture.</param>
    /// <param name="videoStreamType">The PMT stream type of the video.</param>
    /// <param name="startAccessUnitCarriesIdr">
    /// Whether the picture at the access point delivery starts on carries an IDR, or
    /// <see langword="null"/> where that could not be settled.
    /// </param>
    /// <returns>Whether the video has to be re-encoded.</returns>
    public static bool IsNeeded(bool clientNeedsIdr, byte videoStreamType, bool? startAccessUnitCarriesIdr)
        => clientNeedsIdr && videoStreamType == H264StreamType && startAccessUnitCarriesIdr == false;

    /// <summary>
    /// Builds the FFmpeg argument list that re-encodes the video to H.264 with genuine IDR
    /// access points while copying every audio track.
    /// </summary>
    /// <returns>The argument list, one argument per element.</returns>
    public static IReadOnlyList<string> BuildArguments() =>
    [
        "-hide_banner",
        "-loglevel", "warning",
        "-fflags", "+genpts",

        // FFmpeg would otherwise spend up to its five second default deciding what a transport
        // stream contains. The program map names every elementary stream within the first
        // packets, which is all the encoder needs.
        "-analyzeduration", "1000000",
        "-probesize", "4000000",

        "-f", "mpegts",
        "-i", "pipe:0",
        "-map", "0:v:0",
        "-map", "0:a?",
        "-dn", "-sn",
        "-c:a", "copy",
        "-c:v", "libx264",
        "-preset", "veryfast",
        "-crf", "21",
        "-maxrate", "10M",
        "-bufsize", "14M",

        // Closed GOPs whose keyframes are IDR: exactly the property the source lacks and the
        // device decoder will not start without. Two keyframes a second at 25 fps.
        "-x264-params", "keyint=50:min-keyint=25:scenecut=0",

        // Passes progressive frames through untouched and deinterlaces the rest, so interlaced
        // services do not come out combed.
        "-vf", "yadif=deint=interlaced",

        "-f", "mpegts",
        "pipe:1",
    ];

    /// <summary>
    /// Starts the encoder around a source stream.
    /// </summary>
    /// <param name="source">The broadcast, which this takes ownership of reading.</param>
    /// <param name="ffmpegPath">The FFmpeg executable.</param>
    /// <param name="label">What this session is for, for the log.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">Cancels the encoder and the feed.</param>
    /// <returns>The running normalizer.</returns>
    public static H264IdrNormalizer Start(
        Stream source,
        string ffmpegPath,
        string label,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(ffmpegPath);
        ArgumentNullException.ThrowIfNull(logger);

        Pipe? output = null;
        var session = FfmpegSession.Start(
            ffmpegPath,
            BuildArguments(),
            async (chunk, token) => await output!.Writer.WriteAsync(chunk, token).ConfigureAwait(false),
            logger,
            label,
            cancellationToken);

        var normalizer = new H264IdrNormalizer(session, source, logger, cancellationToken);
        output = normalizer._pipe;
        normalizer.Output = normalizer._pipe.Reader.AsStream();
        return normalizer;
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
            logger.LogDebug(exception, "Live TV: the IDR normalizer stopped feeding FFmpeg");
        }
        finally
        {
            // The broadcast has stopped arriving, so let FFmpeg drain what it holds and only then
            // end the output. Ending it here would truncate the last of the stream.
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
