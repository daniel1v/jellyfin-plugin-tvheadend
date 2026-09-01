using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Playback;

namespace TVHeadEnd.Compatibility.Jellyfin12;

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
    private const string MediaSourceIdParameter = "MediaSourceId";
    private const string DeviceIdParameter = "DeviceId";
    private const string StaticParameter = "static";

    /// <summary>
    /// The playlists Jellyfin holds back until enough segments exist. Segment requests are not
    /// among them: by the time one is fetched the playlist naming it has already been released.
    /// </summary>
    private static readonly string[] _hlsPlaylists = ["/master.m3u8", "/main.m3u8", "/live.m3u8"];

    private readonly OpenLiveStreams _openStreams;
    private readonly RequestDelegate _next;
    private readonly ILogger<LivePlaybackRequestMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LivePlaybackRequestMiddleware"/> class.
    /// </summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="openStreams">Which live stream a media source identifier stands for.</param>
    public LivePlaybackRequestMiddleware(
        RequestDelegate next,
        ILogger<LivePlaybackRequestMiddleware> logger,
        OpenLiveStreams openStreams)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        ArgumentNullException.ThrowIfNull(openStreams);

        _next = next;
        _logger = logger;
        _openStreams = openStreams;
    }

    /// <summary>
    /// Handles one request.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="mediaSourceManager">Jellyfin's register of open live streams.</param>
    /// <param name="libraryManager">Jellyfin's library, which answers whose an item is.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public async Task Invoke(HttpContext context, IMediaSourceManager mediaSourceManager, ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mediaSourceManager);
        ArgumentNullException.ThrowIfNull(libraryManager);

        await WidenTransportStreamCapabilities(context, libraryManager).ConfigureAwait(false);

        SupplyLiveStreamId(context.Request, mediaSourceManager);

        if (ResolveOwnStream(context.Request, mediaSourceManager) is { } stream)
        {
            Adjust(context.Request, stream);
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Lets a client that spells MPEG-TS the other way match this plugin's live sources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The media source names one container and Jellyfin compares literally, so a profile that
    /// lists only the other spelling never matches and never direct plays. Rather than making the
    /// source claim two containers -- which it is not, and which reached FFmpeg once as
    /// "-f mpegts,ts" and played nothing -- the profile the client sent is widened to say both.
    /// </para>
    /// <para>
    /// Only the capabilities that describe what the client can take: its direct play profiles, its
    /// container profiles and the container-bound codec profiles. Transcoding profiles are left
    /// alone, because there the container names what the client wants produced, not what it can
    /// read. And only for this plugin's own items -- everything else in the library passes through
    /// with the profile exactly as it was sent.
    /// </para>
    /// <para>
    /// Both routes that weigh a profile against a source. <c>PlaybackInfo</c> is where a client
    /// asks how it may play something, and <c>LiveStreams/Open</c> weighs the profile a second
    /// time against the source the open produced -- <c>MediaInfoHelper.OpenMediaSource</c> calls
    /// <c>SetDeviceSpecificData</c> whenever the request carried one. Widening only the first
    /// would let a client be told it may direct play and then, on opening, be sent to a transcode.
    /// </para>
    /// </remarks>
    private async Task WidenTransportStreamCapabilities(HttpContext context, ILibraryManager libraryManager)
    {
        var request = context.Request;
        if (!request.Path.HasValue || !CarriesADeviceProfile(request.Path.Value))
        {
            return;
        }

        request.EnableBuffering();
        request.Body.Position = 0;

        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(request.Body).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Not something this understands, so not something it may rewrite.
            request.Body.Position = 0;
            return;
        }

        request.Body.Position = 0;

        if (!TvheadendItems.IsOurs(libraryManager, ItemIdOf(request, body)))
        {
            return;
        }

        if (body?["DeviceProfile"] is not JsonObject profile || !Widen(profile))
        {
            return;
        }

        var rewritten = Encoding.UTF8.GetBytes(body.ToJsonString());
        request.Body = new MemoryStream(rewritten);
        request.ContentLength = rewritten.Length;

        _logger.LogDebug(
            "Live TV: {Path} named one of ours; both spellings of the transport stream accepted",
            request.Path.Value);
    }

    /// <summary>
    /// Whether a request on this route is one whose device profile is weighed against a source.
    /// </summary>
    private static bool CarriesADeviceProfile(string path)
        => path.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/LiveStreams/Open", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The item a playback question is about, wherever that route states it.
    /// </summary>
    /// <remarks>
    /// <c>PlaybackInfo</c> names it in the route. <c>LiveStreams/Open</c> takes it from the query
    /// or the body, in that order, which is the order Jellyfin's own controller resolves it in:
    /// <c>ItemId = itemId ?? openLiveStreamDto?.ItemId ?? Guid.Empty</c>. Reading it any other way
    /// would answer a different question than the one the server is about to answer.
    /// </remarks>
    private static Guid ItemIdOf(HttpRequest request, JsonNode? body)
    {
        var path = request.Path.Value ?? string.Empty;

        if (path.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // .../Items/{itemId}/PlaybackInfo
            return segments.Length >= 2 && Guid.TryParse(segments[^2], out var fromRoute) ? fromRoute : default;
        }

        if (Guid.TryParse(request.Query["itemId"].ToString(), out var fromQuery))
        {
            return fromQuery;
        }

        return body?["ItemId"] is JsonValue stated && Guid.TryParse(stated.GetValue<string?>(), out var fromBody)
            ? fromBody
            : default;
    }

    /// <summary>
    /// Widens every input capability of one profile, and reports whether anything changed.
    /// </summary>
    private static bool Widen(JsonObject profile)
    {
        var changed = false;

        foreach (var capability in new[] { "DirectPlayProfiles", "ContainerProfiles", "CodecProfiles" })
        {
            if (profile[capability] is not JsonArray entries)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry is not JsonObject stated || stated["Container"] is not JsonValue container)
                {
                    continue;
                }

                var widened = TransportStreamAliases.Widen(container.GetValue<string?>());
                if (widened is null || string.Equals(widened, container.GetValue<string?>(), StringComparison.Ordinal))
                {
                    continue;
                }

                stated["Container"] = widened;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Whether this is a request for the transport stream itself rather than a playlist.
    /// </summary>
    private static bool IsStaticVideoRequest(HttpRequest request)
    {
        var path = request.Path.Value;

        return !string.IsNullOrEmpty(path)
            && path.Contains("/videos/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/stream", StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.Query[StaticParameter].ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gives a static request the identifier of the live stream it is asking for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jellyfin serves a live stream from this endpoint only when the request names one, because
    /// that is what lets it reach the stream through its direct stream provider. Without it there
    /// is no provider to ask, so it serves the buffer as an ordinary file, which ends at whatever
    /// had been written. Some clients direct play a file source without carrying the identifier
    /// across, and this supplies it.
    /// </para>
    /// <para>
    /// Only where it is certain, and the media source alone is not certainty: one channel can
    /// have several streams open at once, one per viewer whose profile needs its own rendering,
    /// and they all carry the same media source identifier. So the device the request names is
    /// part of the question, and a lookup that does not single out one stream answers nothing.
    /// </para>
    /// <para>
    /// Three things then have to hold. The media source and device named by the request must
    /// resolve to exactly one stream this plugin has open, that stream must have been given an
    /// identifier, and Jellyfin must still resolve that identifier to the very same stream.
    /// Anything short of that and the request goes on exactly as it arrived -- an identifier
    /// guessed wrong would hand a viewer somebody else's channel.
    /// </para>
    /// </remarks>
    private void SupplyLiveStreamId(HttpRequest request, IMediaSourceManager mediaSourceManager)
    {
        if (!IsStaticVideoRequest(request)
            || !string.IsNullOrEmpty(request.Query[LiveStreamIdParameter].ToString()))
        {
            return;
        }

        // The device identifier the client sends on the streaming endpoints, which is the same
        // one Jellyfin put in the session's claims when the stream was opened. Read from the
        // request rather than from the session, because this step runs before authentication.
        var stream = _openStreams.Find(
            request.Query[MediaSourceIdParameter].ToString(),
            request.Query[DeviceIdParameter].ToString());

        var liveStreamId = stream?.MediaSource?.LiveStreamId;
        if (stream is null || string.IsNullOrEmpty(liveStreamId))
        {
            return;
        }

        // The identifier has to still mean this stream. It is Jellyfin that hands them out and
        // Jellyfin that closes streams, so its register is the only thing that can say so.
        if (!ReferenceEquals(mediaSourceManager.GetLiveStreamInfo(liveStreamId), stream))
        {
            return;
        }

        var parameters = request.Query
            .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)))
            .ToList();

        parameters.Add(new KeyValuePair<string, string?>(LiveStreamIdParameter, liveStreamId));
        request.QueryString = QueryString.Create(parameters);

        _logger.LogDebug(
            "Live TV: {Path} named a live source without its stream; supplied {LiveStreamId}",
            request.Path.Value,
            liveStreamId);
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
