using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.Tvheadend;
using BroadcastGenres = TVHeadEnd.Core.Broadcast.BroadcastGenres;
using BroadcastProductionYear = TVHeadEnd.Core.Broadcast.BroadcastProductionYear;
using BroadcastStarRating = TVHeadEnd.Core.Broadcast.BroadcastStarRating;
using DvbContentType = TVHeadEnd.Core.Broadcast.DvbContentType;

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
    private readonly TVHeadEnd.Api.TvheadendArtwork _artwork;
    private readonly IServerConfigurationManager _configuration;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvheadendGuide"/> class.
    /// </summary>
    /// <param name="connection">The TVHeadend connection.</param>
    /// <param name="artwork">How an image reference becomes an address Jellyfin can fetch.</param>
    /// <param name="configuration">
    /// Jellyfin's server configuration, read for the language the viewer wants metadata in.
    /// </param>
    /// <param name="logger">The logger.</param>
    public TvheadendGuide(
        TvheadendConnection connection,
        TVHeadEnd.Api.TvheadendArtwork artwork,
        IServerConfigurationManager configuration,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(artwork);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _artwork = artwork;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Builds the request that asks TVHeadend for one channel's programmes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The language is the one thing here that is Jellyfin's opinion rather than a fact about the
    /// channel. TVHeadend holds a broadcast's title, summary and description once per language it
    /// was given them in, and picks between them per request: <c>language</c> is a comma-separated
    /// list of ISO 639 codes, and it resolves two-letter codes and strips a region, so Jellyfin's
    /// "de" and "pt-BR" both arrive as something the server understands.
    /// </para>
    /// <para>
    /// Omitted rather than guessed at when the server has no preference configured. TVHeadend then
    /// falls back to the language the connection authenticated with, which is a better answer than
    /// any this could invent -- and inventing "und" would ask for the one language that means
    /// "undetermined" and match almost nothing.
    /// </para>
    /// </remarks>
    /// <param name="channelId">The HTSP channel identifier.</param>
    /// <param name="endUtc">The far end of the window wanted.</param>
    /// <param name="preferredLanguage">The language Jellyfin prefers metadata in, if any.</param>
    /// <returns>The <c>getEvents</c> request.</returns>
    internal static HtspMessage BuildEventsRequest(int channelId, DateTime endUtc, string? preferredLanguage)
    {
        var request = HtspMessage.Create("getEvents")
            .Set("channelId", channelId)
            .Set("maxTime", ((DateTimeOffset)DateTime.SpecifyKind(endUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());

        if (!string.IsNullOrWhiteSpace(preferredLanguage))
        {
            request.Set("language", preferredLanguage.Trim());
        }

        return request;
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

        var request = BuildEventsRequest(numericId, endUtc, _configuration.Configuration.PreferredMetadataLanguage);

        var reply = await _connection.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var events = reply.GetMapList("events");

        var endpoint = _connection.HttpEndpoint;

        // Read once for the whole window rather than per entry: every programme on this channel
        // carries the same logo, and the catalog lookup is not free.
        var icon = Plugin.Instance.Configuration.UseChannelLogoWhereArtworkIsMissing
            ? _connection.Channels.Get(channelId)?.Icon
            : null;

        var programs = new List<ProgramInfo>(events.Count);
        foreach (var entry in events)
        {
            var program = Describe(entry, endpoint, icon);
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

    private ProgramInfo? Describe(HtspMessage entry, TvheadendHttpEndpoint endpoint, string? icon)
    {
        var program = ReadProgram(entry);
        if (program is null)
        {
            return null;
        }

        // Through this plugin where it points at TVHeadend, because Jellyfin fetches an image URL
        // with a client that carries no TVHeadend credentials and would be answered with 401.
        if (entry.GetString("image") is { Length: > 0 } image)
        {
            program.ImageUrl = _artwork.AddressFor(image, endpoint);
        }

        // Otherwise the channel's logo, padded, in both slots a card might ask for. Jellyfin's
        // live TV cards are built with "preferThumb", so a programme with only a primary image
        // still shows the placeholder in galleries like "On Now" -- which is exactly what
        // happened when only the logo slot was filled. The logo slot is deliberately not used:
        // it is where a programme's own logo belongs, and the channel's is not that.
        if (string.IsNullOrEmpty(program.ImageUrl))
        {
            var padded = _artwork.PaddedAddressFor(icon, null, endpoint);

            program.ImageUrl = padded;
            program.ThumbImageUrl = padded;
        }

        program.HasImage = !string.IsNullOrEmpty(program.ImageUrl);

        return program;
    }

    /// <summary>
    /// Reads everything one event says about itself, artwork aside.
    /// </summary>
    /// <remarks>
    /// Separate from the artwork because this part is a straight reading of what the broadcast
    /// carried, and the artwork is an address that only exists once this server's own endpoint and
    /// secret are known. Splitting them keeps the reading answerable on its own.
    /// </remarks>
    /// <param name="entry">One entry of the <c>getEvents</c> reply.</param>
    /// <returns>The programme, or <see langword="null"/> when it has no times.</returns>
    internal static ProgramInfo? ReadProgram(HtspMessage entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

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

            // Two identities, and they answer different questions. The series link says which
            // broadcasts belong together, which is what a series recording is bound to; the
            // episode link names this one programme, which is what lets a repeat be recognised as
            // the same thing. Neither stands in for the other, and nothing is invented where
            // TVHeadend sends neither.
            SeriesId = entry.GetString("serieslinkUri"),
            ShowId = entry.GetString("episodeUri"),

            EpisodeNumber = entry.GetInt32("episodeNumber"),
            SeasonNumber = entry.GetInt32("seasonNumber"),
            ProductionYear = BroadcastProductionYear.FromCopyrightYear(entry.GetInt32("copyrightYear")),
            OfficialRating = entry.GetString("ratingLabel"),
            CommunityRating = BroadcastStarRating.ToCommunityRating(entry.GetInt64("starRating")),
            IsPremiere = entry.GetBoolean("isNew"),

            // Up to HTSP v31 the server collapses description, summary and subtitle into
            // "description" when the richer field is missing. From v32 on all three are sent
            // independently, so "description" can be absent even though the event has a summary.
            Overview = entry.GetString("description")
                ?? entry.GetString("summary")
                ?? entry.GetString("subtitle"),
        };

        ApplySeriesFacts(program, entry.GetString("subtitle"));

        if (entry.GetInt64("firstAired") is { } firstAired)
        {
            program.OriginalAirDate = UnixEpochUtc.AddSeconds(firstAired);
        }

        // The broadcaster's own words first, then the fixed DVB table. Both are true of the same
        // programme and neither is a translation of the other -- see BroadcastGenres.
        var categories = entry.GetStringList("category");

        if (entry.GetInt32("contentType") is { } contentType)
        {
            var described = DvbContentType.Describe(contentType);
            program.Genres = [.. BroadcastGenres.Combine(categories, described.Genres)];
            program.IsMovie = described.IsMovie;
            program.IsSports = described.IsSports;
            program.IsNews = described.IsNews;
            program.IsKids = described.IsKids;
        }
        else
        {
            program.Genres = [.. BroadcastGenres.Combine(categories)];
        }

        return program;
    }

    /// <summary>
    /// Says whether a programme belongs to a series, and what it is called within it.
    /// </summary>
    /// <remarks>
    /// Two independent facts, and neither stands in for the other. This used to make a programme
    /// a series only when it carried an episode title, so an episode without one -- which on DVB
    /// is common -- was offered as a one-off, and the series link the server had already sent went
    /// unused. The link is TVHeadend saying in its own words that these broadcasts belong
    /// together, and it is what a series recording is bound to.
    /// </remarks>
    /// <param name="program">The programme, with its series identifier already read.</param>
    /// <param name="subtitle">The episode title the broadcast carried, if any.</param>
    internal static void ApplySeriesFacts(ProgramInfo program, string? subtitle)
    {
        ArgumentNullException.ThrowIfNull(program);

        if (!string.IsNullOrEmpty(subtitle))
        {
            program.EpisodeTitle = subtitle;
            program.IsSeries = true;
        }

        if (!string.IsNullOrEmpty(program.SeriesId))
        {
            program.IsSeries = true;
        }
    }
}
