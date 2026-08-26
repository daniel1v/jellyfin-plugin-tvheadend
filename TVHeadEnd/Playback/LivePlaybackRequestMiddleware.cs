using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Streaming;

namespace TVHeadEnd.Playback;

/// <summary>
/// Adjusts the streaming requests Jellyfin makes for a live stream this plugin opened.
/// </summary>
/// <remarks>
/// <para>
/// One step in Jellyfin's request pipeline, and its whole risk is acting on a request that is not
/// ours. The live stream identifier is the entire correlation: it appears in every streaming URL
/// Jellyfin builds for a source it opened, and it resolves through
/// <see cref="IMediaSourceManager.GetLiveStreamInfo(string)"/> to the running stream. A request
/// with no identifier, one naming a stream Jellyfin no longer has, and one naming another
/// implementation's stream all pass through exactly as they arrived. There is no client detection
/// here, no user agent, no channel list, no session state and no cache.
/// </para>
/// <para>
/// Two rules, on two different questions.
/// </para>
/// <para>
/// <b>Video stream copy.</b> Withdrawing direct play from the media source is enough to put
/// Jellyfin on its transcoding path, but not enough to make it re-encode: inside that path it
/// still copies an H.264 video stream when it can, and a copy of a broadcast with no IDR pictures
/// is the same broadcast with no IDR pictures. So <c>allowVideoStreamCopy</c> is set to false for
/// a stream that was opened for a decoder which will not start on what it carries. That decision
/// was taken when the stream opened and is only read back here.
/// </para>
/// <para>
/// <b>How much has to exist before playback may start.</b> Jellyfin holds an HLS playlist back
/// until a minimum number of segments have been written, and for a segmented live stream being
/// copied its defaults are three segments of three seconds -- nine seconds of broadcast before a
/// viewer sees anything, which is what a cold start on this plugin measured. Both are ordinary
/// query parameters of Jellyfin's own HLS controller, so a request that does not carry them is
/// given the shortest values instead of falling into those defaults.
/// </para>
/// <para>
/// Only where the client said nothing. A client that asks for particular segmentation is asking
/// for a reason -- Apple devices are given six-second segments by Jellyfin's own rules, and
/// overriding that would trade one client's startup for another's playback. So an explicit value
/// is left exactly as it came.
/// </para>
/// </remarks>
public sealed class LivePlaybackRequestMiddleware
{
    private const string LiveStreamIdParameter = "LiveStreamId";
    private const string AllowVideoStreamCopyParameter = "allowVideoStreamCopy";
    private const string MinSegmentsParameter = "MinSegments";
    private const string SegmentLengthParameter = "SegmentLength";

    /// <summary>
    /// The playlists Jellyfin holds back until enough segments exist. Segment requests are not
    /// among them: by the time one is fetched the playlist naming it has already been released.
    /// </summary>
    private static readonly string[] _hlsPlaylists = ["/master.m3u8", "/main.m3u8", "/live.m3u8"];

    private readonly RequestDelegate _next;
    private readonly ILogger<LivePlaybackRequestMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LivePlaybackRequestMiddleware"/> class.
    /// </summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="logger">The logger.</param>
    public LivePlaybackRequestMiddleware(RequestDelegate next, ILogger<LivePlaybackRequestMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Handles one request.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="mediaSourceManager">Jellyfin's register of open live streams.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public Task Invoke(HttpContext context, IMediaSourceManager mediaSourceManager)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mediaSourceManager);

        if (ResolveOwnStream(context.Request, mediaSourceManager) is { } stream)
        {
            Adjust(context.Request, stream);
        }

        return _next(context);
    }

    /// <summary>
    /// The live stream this request names, if this plugin opened it.
    /// </summary>
    private static TvheadendLiveStream? ResolveOwnStream(HttpRequest request, IMediaSourceManager mediaSourceManager)
    {
        var liveStreamId = request.Query[LiveStreamIdParameter].ToString();
        if (string.IsNullOrEmpty(liveStreamId))
        {
            return null;
        }

        // Null for an identifier Jellyfin has no open stream for, and some other implementation
        // for every other plugin's. Both fail the pattern and the request goes on as it arrived.
        return mediaSourceManager.GetLiveStreamInfo(liveStreamId) as TvheadendLiveStream;
    }

    private static bool IsHlsPlaylistRequest(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Jellyfin routes these under Videos and Audio alike. Only the video ones are touched,
        // because the nine seconds being cut is the wait for video segments; a radio channel takes
        // the audio route and is left to Jellyfin's own judgement.
        return value.Contains("/videos/", StringComparison.OrdinalIgnoreCase)
            && _hlsPlaylists.Any(playlist => value.EndsWith(playlist, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the client stated this parameter itself.
    /// </summary>
    private static bool StatedByClient(HttpRequest request, string parameter)
        => !string.IsNullOrEmpty(request.Query[parameter].ToString());

    private void Adjust(HttpRequest request, TvheadendLiveStream stream)
    {
        // Collected before anything is rewritten, so the query is rebuilt once however many rules
        // apply -- and so a request that needs both a re-encode and shorter segments gets both.
        var replace = new List<KeyValuePair<string, string?>>();

        if (stream.RequiresVideoReencode)
        {
            replace.Add(new KeyValuePair<string, string?>(AllowVideoStreamCopyParameter, "false"));
        }

        if (IsHlsPlaylistRequest(request.Path))
        {
            if (!StatedByClient(request, MinSegmentsParameter))
            {
                replace.Add(new KeyValuePair<string, string?>(MinSegmentsParameter, "1"));
            }

            if (!StatedByClient(request, SegmentLengthParameter))
            {
                replace.Add(new KeyValuePair<string, string?>(SegmentLengthParameter, "1"));
            }
        }

        if (replace.Count == 0)
        {
            return;
        }

        // Rebuilt rather than appended to, so a request that already carries one of these ends up
        // with one value rather than two that disagree. Everything else, the API key included, is
        // carried across as it was.
        var parameters = request.Query
            .Where(pair => !replace.Any(added => string.Equals(added.Key, pair.Key, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)))
            .ToList();

        parameters.AddRange(replace);
        request.QueryString = QueryString.Create(parameters);

        _logger.LogDebug(
            "Live TV: {Path} names a stream of ours; set {Parameters}",
            request.Path.Value,
            string.Join(", ", replace.Select(pair => pair.Key + "=" + pair.Value)));
    }
}
