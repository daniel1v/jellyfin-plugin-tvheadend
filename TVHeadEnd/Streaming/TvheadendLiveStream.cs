using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Streaming;

/// <summary>
/// One live channel: the transport stream TVHeadend delivers over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// The media path is TVHeadend's <c>pass</c> profile and nothing else -- the broadcast forwarded
/// untouched, with its own PCR, program tables and random access points intact. What the stream
/// contains is read from that same stream, by the conditioner, as it goes past.
/// </para>
/// <para>
/// The buffer is a fixed-size ring, so a channel left running costs the same disk space after
/// eight hours as after one minute, and every reader joins it at a point a decoder can start
/// from.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "This is a Jellyfin ILiveStream, and 'stream' is what both Jellyfin and the domain call it.")]
public sealed class TvheadendLiveStream : ILiveStream, IDirectStreamProvider, IAsyncDisposable
{
    private const int StreamBufferSize = 131072;

    /// <summary>
    /// The PMT stream type of H.264, the only video the IDR question applies to.
    /// </summary>
    private const byte H264StreamType = 0x1B;

    /// <summary>
    /// How much of the broadcast may be read while establishing whether its access points carry
    /// IDR pictures.
    /// </summary>
    private const int InspectionByteLimit = 4 * 1024 * 1024;

    /// <summary>
    /// How long a connected stream has to produce something playable before the open fails.
    /// </summary>
    /// <remarks>
    /// Connecting proves only that TVHeadend accepted the subscription. A stream that never
    /// produces a usable entry point would otherwise leave the caller waiting on a task that
    /// never completes, which reaches the client as a spinner that never resolves. Failing is
    /// recoverable; hanging is not.
    /// </remarks>
    private static readonly TimeSpan StartupTimeLimit = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How much of the broadcast may be read while establishing whether its access points carry
    /// IDR pictures. Bounded twice, because either bound alone fails on some service: two seconds
    /// of a high bitrate channel is several megabytes, and four megabytes of a radio-rate one is
    /// minutes.
    /// </summary>
    private static readonly TimeSpan InspectionTimeLimit = TimeSpan.FromSeconds(3);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _upstreamUrl;
    private readonly IReadOnlyDictionary<string, string> _upstreamHeaders;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StreamBootstrapIndex _bootstrap = new();
    private readonly string _bufferPath;
    private readonly int _bufferSizeMegabytes;
    private readonly TimeSpan _startupTimeLimit;
    private readonly bool _mayNormalizeH264;
    private readonly string? _ffmpegPath;

    private TransportStreamConditioner? _conditioner;
    private H264IdrNormalizer? _normalizer;
    private Task? _feedTask;
    private bool _closed;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvheadendLiveStream"/> class.
    /// </summary>
    /// <param name="channelId">The TVHeadend channel identifier.</param>
    /// <param name="channelName">The channel name, for the log.</param>
    /// <param name="upstreamUrl">The TVHeadend stream URL, including the pass profile.</param>
    /// <param name="upstreamHeaders">The headers the request needs.</param>
    /// <param name="mediaSource">The media source this stream backs.</param>
    /// <param name="bufferPath">Where to write the buffer.</param>
    /// <param name="bufferSizeMegabytes">The configured buffer window.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="mayNormalizeH264">
    /// Whether this viewer's decoder needs IDR pictures to start. Only then is the beginning of
    /// the broadcast examined, and only then may it be re-encoded.
    /// </param>
    /// <param name="ffmpegPath">The FFmpeg executable, without which nothing is re-encoded.</param>
    /// <param name="startupTimeLimit">
    /// How long a connected stream has to produce something playable. Defaults to the production
    /// bound; a test sets it shorter.
    /// </param>
    public TvheadendLiveStream(
        string channelId,
        string? channelName,
        string upstreamUrl,
        IReadOnlyDictionary<string, string> upstreamHeaders,
        MediaSourceInfo mediaSource,
        string bufferPath,
        int bufferSizeMegabytes,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        bool mayNormalizeH264 = false,
        string? ffmpegPath = null,
        TimeSpan? startupTimeLimit = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ArgumentException.ThrowIfNullOrEmpty(upstreamUrl);
        ArgumentNullException.ThrowIfNull(upstreamHeaders);
        ArgumentNullException.ThrowIfNull(mediaSource);
        ArgumentException.ThrowIfNullOrEmpty(bufferPath);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        ChannelId = channelId;
        ChannelName = channelName;
        _upstreamUrl = upstreamUrl;
        _upstreamHeaders = upstreamHeaders;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _startupTimeLimit = startupTimeLimit ?? StartupTimeLimit;
        _mayNormalizeH264 = mayNormalizeH264;
        _ffmpegPath = ffmpegPath;

        UniqueId = Guid.NewGuid().ToString("N");
        _bufferPath = bufferPath;
        _bufferSizeMegabytes = bufferSizeMegabytes;

        MediaSource = mediaSource;
        ConsumerCount = 1;
        EnableStreamSharing = true;
        OriginalStreamId = string.Empty;
        TunerHostId = string.Empty;
    }

    /// <summary>
    /// Gets the TVHeadend channel identifier.
    /// </summary>
    public string ChannelId { get; }

    /// <summary>
    /// Gets the channel name.
    /// </summary>
    public string? ChannelName { get; }

    /// <summary>
    /// Gets the buffer this stream fills.
    /// </summary>
    public LiveStreamBuffer Buffer { get; private set; } = null!;

    /// <summary>
    /// Gets a value indicating whether the buffer still exists.
    /// </summary>
    /// <remarks>
    /// A source whose buffer has gone is worse than no source: the client keeps asking for
    /// something that answers 404 instead of opening a fresh stream.
    /// </remarks>
    public bool HasBuffer => Buffer?.Exists == true;

    /// <summary>
    /// Gets the file the stream can be read as.
    /// </summary>
    public string MediaPath => Buffer?.Path ?? string.Empty;

    /// <summary>
    /// Gets the program map of the transport stream actually arriving, or <see langword="null"/>
    /// until one has been reassembled.
    /// </summary>
    public ProgramMapTable? ProgramMap => _conditioner?.ProgramMap;

    /// <summary>
    /// Gets a value indicating whether delivery began at a point the broadcast marked as a random
    /// access point, rather than at a picture start accepted because the search ran out of time.
    /// </summary>
    /// <remarks>
    /// Diagnostic only. Either way the stream is delivered; the difference is whether the opening
    /// frames are guaranteed to decode, and a broadcaster that never sets the indicator is a
    /// thing worth being able to see in a log.
    /// </remarks>
    public bool StartedOnConfirmedRandomAccessPoint => _conditioner?.StartedOnRandomAccessPoint ?? false;

    /// <summary>
    /// Gets a value indicating whether a decoder that will not start without an IDR picture can
    /// be given this stream.
    /// </summary>
    /// <remarks>
    /// True three ways, and they are all the same statement: the video was re-encoded to carry
    /// IDR pictures, or it is not H.264 and the question does not arise, or the broadcast turned
    /// out to carry them by itself. Undetermined counts as no, so such a viewer opens its own
    /// stream rather than joining one that might never start for it.
    /// </remarks>
    public bool SuitsDecodersNeedingIdr
        => NormalizedForIdr
        || (_conditioner is { } conditioner
            && (conditioner.VideoStreamType != H264StreamType || conditioner.StartAccessUnitCarriesIdr == true));

    /// <summary>
    /// Gets a value indicating whether the video of this stream was re-encoded to give it IDR
    /// pictures to start on.
    /// </summary>
    /// <remarks>
    /// Diagnostic, and the reason <see cref="SuitsDecodersNeedingIdr"/> can answer yes.
    /// </remarks>
    public bool NormalizedForIdr { get; private set; }

    /// <inheritdoc />
    public int ConsumerCount { get; set; }

    /// <inheritdoc />
    public string OriginalStreamId { get; set; }

    /// <inheritdoc />
    public string TunerHostId { get; }

    /// <inheritdoc />
    public bool EnableStreamSharing { get; private set; }

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; }

    /// <inheritdoc />
    public async Task Open(CancellationToken openCancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Directory.CreateDirectory(Path.GetDirectoryName(_bufferPath)
            ?? throw new InvalidOperationException("The live TV buffer path has no parent directory."));

        var stopwatch = Stopwatch.StartNew();
        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, _upstreamUrl);
        foreach (var header in _upstreamHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        HttpResponseMessage response;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(openCancellationToken, _lifetime.Token);
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            request.Dispose();
            client.Dispose();
            throw;
        }

        request.Dispose();
        Stream source = await response.Content.ReadAsStreamAsync(_lifetime.Token).ConfigureAwait(false);

        // The one client-dependent decision in the live path, and it is taken here because it is
        // the only point where both the caller and the bytes are in hand.
        if (_mayNormalizeH264 && !string.IsNullOrEmpty(_ffmpegPath))
        {
            source = await PrepareForIdrDecoder(source, openCancellationToken).ConfigureAwait(false);
        }

        Buffer = new LiveStreamBuffer(_bufferPath + ".ts", _bufferSizeMegabytes) { Bootstrap = _bootstrap };
        _feedTask = Feed(source, [client, response], _lifetime.Token);

        try
        {
            await _ready.Task.WaitAsync(_startupTimeLimit, openCancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            throw new TimeoutException(FormattableString.Invariant(
                $"TVHeadend accepted the subscription for channel {ChannelId} but produced nothing playable within {_startupTimeLimit.TotalSeconds:0} seconds."));
        }
        catch
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            throw;
        }

        _logger.LogDebug(
            "Live TV: HTTP pass opened for channel {ChannelId} ({ChannelName}); playable after {ElapsedMilliseconds} ms",
            ChannelId,
            ChannelName ?? "<unknown>",
            stopwatch.ElapsedMilliseconds);
    }

    /// <inheritdoc />
    public async Task Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        EnableStreamSharing = false;
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Buffer.OpenReader();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Jellyfin's <see cref="ILiveStream"/> is <see cref="IDisposable"/>, so this bridge has to
    /// exist. It cannot deadlock: everything <see cref="DisposeAsync"/> awaits is configured not
    /// to resume on the caller's synchronization context.
    /// </remarks>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EnableStreamSharing = false;

        await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_feedTask is not null)
        {
            try
            {
                await _feedTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the last consumer goes away.
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogDebug(exception, "Live TV stream {UniqueId}: the feed ended with an error", UniqueId);
            }
        }

        if (_normalizer is not null)
        {
            await _normalizer.DisposeAsync().ConfigureAwait(false);
        }

        if (Buffer is not null)
        {
            await Buffer.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();

        _logger.LogDebug("Live TV: stream closed for channel {ChannelId} ({ChannelName})", ChannelId, ChannelName ?? "<unknown>");
    }

    /// <summary>
    /// Reads the beginning of the broadcast to find out whether the picture a decoder would start
    /// on is one this viewer's decoder can actually start on, and re-encodes if it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three conditions, all of which have to hold: the video is H.264, the broadcast signalled a
    /// random access point, and the picture at that point contains no IDR. Anything else -- MPEG-2,
    /// HEVC, an IDR actually present, no access point found inside the bounds -- takes the ordinary
    /// path untouched, because for those the re-encode would cost a processor core to fix nothing.
    /// </para>
    /// <para>
    /// The bytes read here are not thrown away. They are handed back in front of the rest of the
    /// stream, so the decision costs a fraction of a second of latency and no data at all.
    /// </para>
    /// </remarks>
    /// <param name="upstream">The broadcast as TVHeadend is delivering it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stream to feed the buffer from, re-encoded or not.</returns>
    private async Task<Stream> PrepareForIdrDecoder(Stream upstream, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var inspector = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
        var retained = new MemoryStream();
        var readBuffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        var scratch = ArrayPool<byte>.Shared.Rent(
            TransportStreamConditioner.GetMaximumConditionedLength(StreamBufferSize));

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);

            while (inspector.StartAccessUnitCarriesIdr is null
                && retained.Length < InspectionByteLimit
                && stopwatch.Elapsed < InspectionTimeLimit)
            {
                var read = await upstream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), linked.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (retained.Length == 0 && !SourceContainer.IsTransportStream(readBuffer.AsSpan(0, read)))
                {
                    throw new InvalidOperationException(FormattableString.Invariant(
                        $"TVHeadend did not deliver channel {ChannelId} as MPEG-TS. The plugin requests the 'pass' profile, so the server has substituted another one; check that 'pass' exists and that this user may use it."));
                }

                retained.Write(readBuffer.AsSpan(0, read));
                inspector.Condition(readBuffer.AsSpan(0, read), scratch);

                // Nothing more to learn: the video is not H.264, so the IDR question is about a
                // syntax this stream does not use.
                if (inspector.HasStarted && inspector.VideoStreamType != H264StreamType)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(scratch);
        }

        Stream source = new PrefixedStream(retained.ToArray(), upstream);

        if (!H264IdrNormalizer.IsNeeded(true, inspector.VideoStreamType, inspector.StartAccessUnitCarriesIdr))
        {
            _logger.LogDebug(
                "Live TV: channel {ChannelId} ({ChannelName}) goes to this client untouched; "
                + "videoStreamType=0x{VideoStreamType:X2} startAccessUnitCarriesIdr={CarriesIdr} after {ElapsedMilliseconds} ms",
                ChannelId,
                ChannelName ?? "<unknown>",
                inspector.VideoStreamType,
                inspector.StartAccessUnitCarriesIdr?.ToString() ?? "undetermined",
                stopwatch.ElapsedMilliseconds);
            return source;
        }

        _logger.LogInformation(
            "Live TV: channel {ChannelId} ({ChannelName}) signals random access without an IDR picture, "
            + "and this client's decoder needs one; re-encoding the video after {ElapsedMilliseconds} ms",
            ChannelId,
            ChannelName ?? "<unknown>",
            stopwatch.ElapsedMilliseconds);

        NormalizedForIdr = true;
        _normalizer = H264IdrNormalizer.Start(
            source,
            _ffmpegPath!,
            FormattableString.Invariant($"channel {ChannelId}"),
            _logger,
            _lifetime.Token);

        return _normalizer.Output;
    }

    private async Task Feed(
        Stream upstream,
        IReadOnlyList<IDisposable> owned,
        CancellationToken cancellationToken)
    {
        byte[]? readBuffer = null;
        byte[]? conditionedBuffer = null;

        try
        {
            await using (upstream.ConfigureAwait(false))
            {
                readBuffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
                conditionedBuffer = ArrayPool<byte>.Shared.Rent(
                    TransportStreamConditioner.GetMaximumConditionedLength(StreamBufferSize));

                var conditioner = new TransportStreamConditioner(
                    TransportStreamConditioner.EventInformationTablePid,
                    requireIdrAtAccessPoints: NormalizedForIdr);
                _conditioner = conditioner;
                var checkedContainer = false;

                while (true)
                {
                    var read = await upstream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (!checkedContainer)
                    {
                        checkedContainer = true;

                        // The pass profile is requested explicitly, so anything else means the
                        // server substituted a profile of its own. Said plainly here rather than
                        // left to surface as a startup timeout with no explanation.
                        if (!SourceContainer.IsTransportStream(readBuffer.AsSpan(0, read)))
                        {
                            throw new InvalidOperationException(FormattableString.Invariant(
                                $"TVHeadend did not deliver channel {ChannelId} as MPEG-TS. The plugin requests the 'pass' profile, so the server has substituted another one; check that 'pass' exists and that this user may use it."));
                        }
                    }

                    var conditioned = conditioner.Condition(readBuffer.AsSpan(0, read), conditionedBuffer);
                    if (conditioned == 0)
                    {
                        continue;
                    }

                    // A broadcaster that changes the program layout invalidates every entry point
                    // found under the old one: a reader sent there would get the new tables and
                    // the old picture. Discarded before this chunk's own access points are
                    // recorded, so the first point under the new layout survives.
                    if (conditioner.ProgramLayoutChanged)
                    {
                        conditioner.AcknowledgeProgramLayoutChange();
                        _bootstrap.Reset();

                        // Everyone already watching carries on: their decoder has followed the
                        // change packet by packet, which is what a broadcast expects of it. What
                        // must not happen is a new viewer joining here, because the description
                        // Jellyfin negotiated with belongs to the programme before. Refusing to
                        // be shared sends the next one to a stream of its own, opened against
                        // the table that is now on air.
                        EnableStreamSharing = false;

                        _logger.LogInformation(
                            "Live TV: channel {ChannelId} changed its program layout; earlier join points discarded "
                            + "and the stream withdrawn from sharing, so a new viewer is described from the new tables",
                            ChannelId);
                    }

                    // The tables are taken before the write and published with the access points
                    // found in the same chunk, so a reader never sees one without the other.
                    await Buffer.Write(
                        conditionedBuffer.AsMemory(0, conditioned),
                        conditioner.RandomAccessOffsets,
                        conditioner.TakeProgramTables(),
                        cancellationToken).ConfigureAwait(false);

                    // Ready as soon as there is something a reader can actually start on: the
                    // conditioner only forwards once it holds both program tables and has chosen
                    // its entry point, so the first bytes in the buffer are already that. Waiting
                    // for a fixed number of bytes on top would delay a radio service, whose whole
                    // bitrate is a fraction of a television channel's, for no gain.
                    if (!_ready.Task.IsCompleted && Buffer.WritePosition > 0)
                    {
                        _ready.TrySetResult();
                    }
                }

                if (!_ready.Task.IsCompleted)
                {
                    _ready.TrySetException(new EndOfStreamException(
                        "TVHeadend closed the live stream before sending enough data."));
                }
            }
        }
        catch (OperationCanceledException exception)
        {
            _ready.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ready.TrySetException(exception);
            _logger.LogError(exception, "Live TV: the stream for channel {ChannelId} stopped unexpectedly", ChannelId);
        }
        finally
        {
            EnableStreamSharing = false;

            if (readBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(readBuffer);
            }

            if (conditionedBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(conditionedBuffer);
            }

            foreach (var resource in owned)
            {
                resource.Dispose();
            }
        }
    }
}
