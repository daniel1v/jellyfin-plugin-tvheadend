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
/// Stops Jellyfin copying the video of a live stream that was opened for a decoder which will not
/// start on it.
/// </summary>
/// <remarks>
/// <para>
/// Withdrawing direct play from the media source is enough to put Jellyfin on its transcoding
/// path, but not enough to make it re-encode: inside that path Jellyfin still copies an H.264
/// video stream when it can, and a copy of a broadcast with no IDR pictures is the same broadcast
/// with no IDR pictures. The one thing that has to change is the request's
/// <c>allowVideoStreamCopy</c>, which <c>EncodingHelper.TryStreamCopy</c> reads and which
/// Jellyfin's own parameter parsing leaves alone.
/// </para>
/// <para>
/// So this sets it, for one stream, on the requests that name it. The live stream identifier is
/// the whole of the correlation: it appears in every streaming URL Jellyfin builds for a source it
/// opened, and it resolves through <see cref="IMediaSourceManager.GetLiveStreamInfo(string)"/> to
/// the running stream, which already knows whether it was opened for such a decoder. There is no
/// client detection here, no channel list, no session state and no cache -- the decision was taken
/// when the stream was opened and is simply read back.
/// </para>
/// <para>
/// Anything else passes through untouched: a request with no live stream identifier, one naming a
/// stream this plugin did not open, and one naming a stream of ours that plays as delivered.
/// </para>
/// </remarks>
public sealed class ForcedVideoReencodeMiddleware
{
    private const string LiveStreamIdParameter = "LiveStreamId";
    private const string AllowVideoStreamCopyParameter = "allowVideoStreamCopy";

    private readonly RequestDelegate _next;
    private readonly ILogger<ForcedVideoReencodeMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ForcedVideoReencodeMiddleware"/> class.
    /// </summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="logger">The logger.</param>
    public ForcedVideoReencodeMiddleware(RequestDelegate next, ILogger<ForcedVideoReencodeMiddleware> logger)
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

        if (RequiresVideoReencode(context, mediaSourceManager))
        {
            RefuseVideoStreamCopy(context.Request);
        }

        return _next(context);
    }

    private static bool RequiresVideoReencode(HttpContext context, IMediaSourceManager mediaSourceManager)
    {
        var liveStreamId = context.Request.Query[LiveStreamIdParameter].ToString();
        if (string.IsNullOrEmpty(liveStreamId))
        {
            return false;
        }

        // Null for an identifier Jellyfin has no open stream for, and some other implementation
        // for every other plugin's. Both fail the pattern and the request goes on as it arrived.
        return mediaSourceManager.GetLiveStreamInfo(liveStreamId) is TvheadendLiveStream
        {
            RequiresVideoReencode: true,
        };
    }

    private void RefuseVideoStreamCopy(HttpRequest request)
    {
        // Rebuilt rather than appended to, so a request that already carries the parameter ends up
        // with one value rather than two that disagree. Everything else, the API key included, is
        // carried across as it was.
        var parameters = request.Query
            .Where(pair => !string.Equals(pair.Key, AllowVideoStreamCopyParameter, StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)))
            .ToList();

        parameters.Add(new KeyValuePair<string, string?>(AllowVideoStreamCopyParameter, "false"));

        request.QueryString = QueryString.Create(parameters);

        _logger.LogDebug(
            "Live TV: {Path} names a stream whose video Jellyfin has to re-encode; video stream copy refused",
            request.Path.Value);
    }
}
