using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Playback;

/// <summary>
/// Writes down what a client actually asked for when it asked how to play something.
/// </summary>
/// <remarks>
/// <para>
/// A diagnostic, and meant to be removed once it has done its work. Jellyfin logs the outcome of
/// every playback decision but not the request behind it, and the parameters that decide the
/// outcome -- whether direct play was permitted at all, which bitrate ceiling applies, which live
/// stream is meant -- travel in the body of a POST. Reading the server's log alone, three
/// identical-looking questions about one open stream give two different answers and there is no
/// way to see why.
/// </para>
/// <para>
/// So the request is read here, where this plugin already sits in the pipeline, and put in the
/// log next to the decision it produced. Only playback questions are touched, the body is
/// rewound, and nothing about the request is changed.
/// </para>
/// </remarks>
public sealed class PlaybackRequestTrace
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PlaybackRequestTrace> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackRequestTrace"/> class.
    /// </summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="logger">Where the requests are written.</param>
    public PlaybackRequestTrace(RequestDelegate next, ILogger<PlaybackRequestTrace> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Logs a playback question and lets it through untouched.
    /// </summary>
    /// <param name="context">The request in flight.</param>
    /// <returns>A task that completes when the pipeline has.</returns>
    public async Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.Contains("PlaybackInfo", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);

        _logger.LogInformation(
            "Playback question: {Method} {Path}{Query} body={Body}",
            context.Request.Method,
            path,
            context.Request.QueryString.Value,
            body);

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads what was asked, without the device profile, and leaves the body where the reader
    /// expects it.
    /// </summary>
    /// <remarks>
    /// The profile is the bulk of a playback question and none of what is in doubt here -- it is
    /// tens of kilobytes of codec tables, and Jellyfin already names it in the decision it logs.
    /// What is wanted is the handful of parameters around it, so the body is parsed and every
    /// top-level value except the profile is kept.
    /// </remarks>
    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        if (!request.Body.CanRead)
        {
            return "<unreadable>";
        }

        request.EnableBuffering();

        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body).ConfigureAwait(false);
        request.Body.Seek(0, SeekOrigin.Begin);

        if (body.Length == 0)
        {
            return "<empty>";
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var asked = new StringBuilder();

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("DeviceProfile"))
                {
                    continue;
                }

                if (asked.Length > 0)
                {
                    asked.Append(", ");
                }

                asked.Append(property.Name).Append('=').Append(property.Value.ToString());
            }

            return asked.Length == 0 ? "<profile only>" : asked.ToString();
        }
        catch (JsonException)
        {
            return $"<unparsed, {body.Length} bytes>";
        }
    }
}
