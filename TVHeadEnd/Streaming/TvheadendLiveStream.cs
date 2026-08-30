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
using TVHeadEnd.Core.Media;
using TVHeadEnd.Playback;

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
    /// How long a connected stream has to produce something playable before the open fails.
    /// </summary>
    /// <remarks>
    /// Connecting proves only that TVHeadend accepted the subscription. A stream that never
    /// produces a usable entry point would otherwise leave the caller waiting on a task that
    /// never completes, which reaches the client as a spinner that never resolves. Failing is
    /// recoverable; hanging is not.
    /// </remarks>
    private static readonly TimeSpan StartupTimeLimit = TimeSpan.FromSeconds(20);

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
    private readonly bool _clientNeedsIdr;

    private TransportStreamConditioner? _conditioner;
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
    /// <param name="clientNeedsIdr">
    /// Whether the viewer this stream is being opened for has a decoder that will not start
    /// without an IDR picture. The one thing about the caller that reaches this layer.
    /// </param>
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
        bool clientNeedsIdr = false,
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
        _clientNeedsIdr = clientNeedsIdr;

        UniqueId = Guid.NewGuid().ToString("N");
        _bufferPath = bufferPath;
        _bufferSizeMegabytes = bufferSizeMegabytes;

        MediaSource = mediaSource;
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
    /// Gets a value indicating whether a decoder that will not start without an IDR picture can be
    /// handed this stream.
    /// </summary>
    /// <remarks>
    /// Two ways to be true and they say the same thing: the video is not H.264, so the question
    /// does not arise, or this stream is publishing entry points that were read and found to open
    /// on an IDR. Undetermined counts as no.
    /// </remarks>
    public bool OffersIdrJoins
        => _conditioner is { } conditioner
        && (conditioner.VideoStreamType != H264StreamType
            || JoinGuarantee == RandomAccessGuarantee.Idr);

    /// <summary>
    /// Gets the guarantee this running stream offers the readers it hands out.
    /// </summary>
    /// <remarks>
    /// The contract, not the history. It is <see cref="RandomAccessGuarantee.Idr"/> only while this
    /// stream is genuinely publishing IDR-safe entry points, which is what a later reader or one
    /// the writer has lapped will be given -- the fact that the first access point happened to open
    /// on an IDR says nothing about where the next reader lands.
    /// </remarks>
    public RandomAccessGuarantee JoinGuarantee
        => _conditioner?.HasIdrEntryPoint == true
            ? RandomAccessGuarantee.Idr
            : RandomAccessGuarantee.DvbRandomAccess;

    /// <summary>
    /// Gets a value indicating whether Jellyfin has to re-encode this stream's video for the
    /// viewer it was opened for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One statement and nothing more: this stream was opened for a client whose decoder needs an
    /// IDR picture, and the H.264 access point it starts on was found to carry none. The buffer
    /// holds the broadcast exactly as TVHeadend delivered it either way -- this changes what the
    /// media source offers, not what is in the ring.
    /// </para>
    /// <para>
    /// Settled before <see cref="Open"/> returns, so the media source built from it is built from
    /// a final answer. The feed is the only thing that sets it; the setter is reachable inside the
    /// assembly so a test can stand a stream up without running one.
    /// </para>
    /// </remarks>
    public bool RequiresVideoReencode { get; internal set; }

    /// <inheritdoc />
    /// <remarks>
    /// Backed by the viewers the stream is actually being held open for, rather than by a tally
    /// of the times Jellyfin was asked to open it. Jellyfin only ever assigns a lower value here,
    /// to say a viewer has gone, and it does not say which; see <see cref="LiveStreamConsumers"/>
    /// for what is done with that.
    /// </remarks>
    public int ConsumerCount
    {
        get => Consumers.Count;

        set
        {
            // Defensive about a contract this does not own. A close that arrives twice, or after
            // the last viewer has already gone, assigns a negative value; taking that literally
            // would spin here for ever. Nothing is invented in the other direction either -- a
            // higher value would be asking for viewers there are no names for.
            var target = Math.Max(0, value);

            while (Consumers.Count > target && Consumers.ReleaseOne() > target)
            {
            }
        }
    }

    /// <summary>
    /// Gets the viewers this stream is being kept open for.
    /// </summary>
    public LiveStreamConsumers Consumers { get; } = new();

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
        var upstream = await response.Content.ReadAsStreamAsync(_lifetime.Token).ConfigureAwait(false);

        Buffer = new LiveStreamBuffer(_bufferPath + ".ts", _bufferSizeMegabytes) { Bootstrap = _bootstrap };
        _feedTask = Feed(upstream, [client, response], _lifetime.Token);

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

        // Read fresh each time. A stream that can offer IDR-safe entry points offers them to every
        // reader, whoever opened it: a decoder that does not need them loses nothing by being given
        // one, and a reader that joins later -- or after the writer laps it -- must not be able to
        // land on a weaker point than the stream is capable of.
        Buffer.RequiredGuarantee = JoinGuarantee;
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

        if (Buffer is not null)
        {
            await Buffer.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();

        _logger.LogDebug("Live TV: stream closed for channel {ChannelId} ({ChannelName})", ChannelId, ChannelName ?? "<unknown>");
    }

    /// <summary>
    /// Reports whether everything the media source has to state about this stream is known.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One question, asked of one kind of viewer. A client whose decoder needs an IDR picture and
    /// a broadcast that started on a signalled H.264 access point: until the picture at that point
    /// has been read to the end there is no answer, and publishing a media source before then
    /// would publish a guess.
    /// </para>
    /// <para>
    /// It settles as soon as the answer exists -- immediately when an IDR turns up in the access
    /// unit, and at the start of the next one when none did. Nothing waits on a timer. Everyone
    /// else is ready straight away: a viewer that does not need IDR pictures, MPEG-2 or HEVC
    /// video, a radio service, and a stream that began somewhere other than a signalled access
    /// point, where the question has no answer to wait for.
    /// </para>
    /// </remarks>
    private bool IsPlaybackSettled(TransportStreamConditioner conditioner)
        => !_clientNeedsIdr
        || conditioner.VideoStreamType != H264StreamType
        || !conditioner.StartedOnRandomAccessPoint
        || conditioner.HasIdrEntryPoint is not null;

    private void LogPlayback(TransportStreamConditioner conditioner)
    {
        if (!RequiresVideoReencode)
        {
            _logger.LogDebug(
                "Live TV: channel {ChannelId} ({ChannelName}) plays as delivered; "
                + "videoStreamType=0x{VideoStreamType:X2} idrEntryPoint={HasIdr} joinGuarantee={Guarantee} clientNeedsIdr={ClientNeedsIdr}",
                ChannelId,
                ChannelName ?? "<unknown>",
                conditioner.VideoStreamType,
                conditioner.HasIdrEntryPoint?.ToString() ?? "n/a",
                JoinGuarantee,
                _clientNeedsIdr);
            return;
        }

        _logger.LogInformation(
            "Live TV: channel {ChannelId} ({ChannelName}) signals random access without an IDR picture and this "
            + "client's decoder needs one in the first few it offered; the broadcast is buffered untouched "
            + "and Jellyfin is asked to re-encode the video rather than copy it",
            ChannelId,
            ChannelName ?? "<unknown>");
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

                var conditioner = new TransportStreamConditioner(TransportStreamConditioner.EventInformationTablePid);
                _conditioner = conditioner;
                var container = new SourceContainerCheck();

                while (true)
                {
                    var read = await upstream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    // The pass profile is requested explicitly, so anything else means the server
                    // substituted a profile of its own. Said plainly here rather than left to
                    // surface as a startup timeout with no explanation -- but only once enough of
                    // the opening has arrived for the answer to be worth anything, because a read
                    // returns whatever is there and a short one proves nothing either way.
                    if (container.Accept(readBuffer.AsSpan(0, read)) == SourceContainerVerdict.NotTransportStream)
                    {
                        throw new InvalidOperationException(FormattableString.Invariant(
                            $"TVHeadend did not deliver channel {ChannelId} as MPEG-TS. The plugin requests the 'pass' profile, so the server has substituted another one; check that 'pass' exists and that this user may use it."));
                    }

                    var conditioned = conditioner.Condition(readBuffer.AsSpan(0, read), conditionedBuffer);
                    if (conditioned == 0)
                    {
                        continue;
                    }

                    if (conditioner.ProgramLayoutChanged)
                    {
                        conditioner.AcknowledgeProgramLayoutChange();

                        // Discarding the entry points found under the old layout is the bootstrap
                        // index's own business: the tables it is about to be given carry which
                        // layout they describe, and it drops everything belonging to the one
                        // before. Doing it from here as well would be a second place that has to
                        // stay right, and it could not reach the points found earlier in this
                        // same chunk in any case.
                        //
                        // What does belong here is the sharing decision. Everyone already
                        // watching carries on: their decoder has followed the change packet by
                        // packet, which is what a broadcast expects of it. What must not happen
                        // is a new viewer joining, because the description Jellyfin negotiated
                        // with belongs to the programme before.
                        EnableStreamSharing = false;

                        _logger.LogInformation(
                            "Live TV: channel {ChannelId} changed its program layout; the stream is withdrawn from "
                            + "sharing, so a new viewer is described from the tables now on air",
                            ChannelId);
                    }

                    // The tables are taken before the write and published with the access points
                    // found in the same chunk, so a reader never sees one without the other.
                    await Buffer.Write(
                        conditionedBuffer.AsMemory(0, conditioned),
                        conditioner.AccessPoints,
                        conditioner.TakeProgramTables(),
                        cancellationToken).ConfigureAwait(false);

                    // Ready as soon as there is something a reader can actually start on and the
                    // one playback property of this stream is settled. The conditioner only
                    // forwards once it holds both program tables and has chosen its entry point,
                    // so the first bytes in the buffer are already that; waiting for a fixed
                    // number of bytes on top would delay a radio service, whose whole bitrate is
                    // a fraction of a television channel's, for no gain.
                    if (!_ready.Task.IsCompleted && Buffer.WritePosition > 0 && IsPlaybackSettled(conditioner))
                    {
                        // The same rule the recordings path applies, from the same place. A
                        // stream that is not H.264 never reaches the classifier at all, so it
                        // carries no evidence and needs no separate exemption here.
                        RequiresVideoReencode = PlaybackCompatibilityPolicy.RequiresVideoReencode(
                            _clientNeedsIdr,
                            conditioner.EntryPointEvidence);

                        LogPlayback(conditioner);
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
