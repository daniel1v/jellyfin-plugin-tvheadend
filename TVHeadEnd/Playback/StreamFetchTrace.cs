using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Playback;

/// <summary>
/// Records who fetches a live transport stream.
/// </summary>
/// <remarks>
/// A diagnostic, and meant to be removed once it has answered one question. Both readers of a live
/// buffer arrive the same way -- the local FFmpeg that feeds an HLS remux, and a client taking the
/// stream directly -- so a trace inside the stream itself cannot tell them apart. The request can:
/// the client names itself in its user agent.
/// </remarks>
public sealed class StreamFetchTrace
{
    private readonly RequestDelegate _next;
    private readonly ILogger<StreamFetchTrace> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamFetchTrace"/> class.
    /// </summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="logger">Where the fetches are written.</param>
    public StreamFetchTrace(RequestDelegate next, ILogger<StreamFetchTrace> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Logs a fetch of a live stream and lets it through untouched.
    /// </summary>
    /// <param name="context">The request in flight.</param>
    /// <returns>A task that completes when the pipeline has.</returns>
    public async Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path.Value ?? string.Empty;
        var interesting = path.Contains("LiveStreamFiles", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("stream.ts", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/videos/", StringComparison.OrdinalIgnoreCase);

        if (!interesting)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Stream fetch: {Method} {Path}{Query} by {Agent}",
            context.Request.Method,
            path,
            context.Request.QueryString.Value,
            context.Request.Headers.UserAgent.ToString());

        await _next(context).ConfigureAwait(false);

        _logger.LogInformation(
            "Stream fetch ended: {Path} status={Status}",
            path,
            context.Response.StatusCode);
    }
}
