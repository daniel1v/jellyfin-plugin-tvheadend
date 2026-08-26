using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.LiveTv;

/// <summary>
/// Reads the programme guide for one channel at a time.
/// </summary>
/// <remarks>
/// Fetched on demand rather than subscribed to. TVHeadend will push the whole guide over the
/// metadata feed, but Jellyfin asks for one channel and one window at a time and stores what it
/// gets, so pushing everything would move a great deal of data that nothing reads.
/// </remarks>
public sealed class TvheadendGuide
{
    private static readonly DateTime UnixEpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TvheadendConnection _connection;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvheadendGuide"/> class.
    /// </summary>
    /// <param name="connection">The TVHeadend connection.</param>
    /// <param name="logger">The logger.</param>
    public TvheadendGuide(TvheadendConnection connection, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _logger = logger;
    }

    /// <summary>
    /// Gets the programmes of one channel within a window.
    /// </summary>
    /// <param name="channelId">The HTSP channel identifier.</param>
    /// <param name="startUtc">The start of the window.</param>
    /// <param name="endUtc">The end of the window.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The programmes.</returns>
    public async Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(
        string channelId,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(channelId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
        {
            return [];
        }

        var request = HtspMessage.Create("getEvents")
            .Set("channelId", numericId)
            .Set("maxTime", ((DateTimeOffset)DateTime.SpecifyKind(endUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());

        var reply = await _connection.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var events = reply.GetMapList("events");

        var endpoint = _connection.HttpEndpoint;
        var programs = new List<ProgramInfo>(events.Count);
        foreach (var entry in events)
        {
            var program = Describe(entry, endpoint);
            if (program is null)
            {
                continue;
            }

            // The window is a filter rather than something the server guarantees: getEvents is
            // bounded at the far end only.
            if (program.StartDate > endUtc || program.EndDate < startUtc)
            {
                continue;
            }

            programs.Add(program);
        }

        _logger.LogDebug(
            "TVHeadend returned {Count} programmes for channel {ChannelId}",
            programs.Count,
            channelId);

        return programs;
    }

    private static ProgramInfo? Describe(HtspMessage entry, TvheadendHttpEndpoint endpoint)
    {
        var start = entry.GetInt64("start");
        var stop = entry.GetInt64("stop");
        if (start is null || stop is null)
        {
            return null;
        }

        var program = new ProgramInfo
        {
            Id = entry.GetInt32("eventId")?.ToString(CultureInfo.InvariantCulture),
            ChannelId = entry.GetInt32("channelId")?.ToString(CultureInfo.InvariantCulture),
            StartDate = UnixEpochUtc.AddSeconds(start.Value),
            EndDate = UnixEpochUtc.AddSeconds(stop.Value),
            Name = entry.GetString("title"),
            SeriesId = entry.GetString("serieslinkUri"),
            EpisodeNumber = entry.GetInt32("episodeNumber"),
            SeasonNumber = entry.GetInt32("seasonNumber"),

            // Up to HTSP v31 the server collapses description, summary and subtitle into
            // "description" when the richer field is missing. From v32 on all three are sent
            // independently, so "description" can be absent even though the event has a summary.
            Overview = entry.GetString("description")
                ?? entry.GetString("summary")
                ?? entry.GetString("subtitle"),
        };

        if (entry.GetString("subtitle") is { Length: > 0 } episodeTitle)
        {
            program.EpisodeTitle = episodeTitle;
            program.IsSeries = true;
        }

        if (entry.GetInt64("firstAired") is { } firstAired)
        {
            program.OriginalAirDate = UnixEpochUtc.AddSeconds(firstAired);
        }

        if (entry.GetString("image") is { Length: > 0 } image)
        {
            program.ImageUrl = endpoint.ResolveImageUrl(image);
            program.HasImage = !string.IsNullOrEmpty(program.ImageUrl);
        }

        if (entry.GetInt32("contentType") is { } contentType)
        {
            var described = DvbContentType.Describe(contentType);
            program.Genres = [.. described.Genres];
            program.IsMovie = described.IsMovie;
            program.IsSports = described.IsSports;
            program.IsNews = described.IsNews;
            program.IsKids = described.IsKids;
        }

        return program;
    }
}
