using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Playback;
using TVHeadEnd.Recordings;

namespace TVHeadEnd.Compatibility.Jellyfin12;

/// <summary>
/// Withdraws the ways of delivering a recording that this client cannot actually start on.
/// </summary>
/// <remarks>
/// <para>
/// Live TV has a stream being opened, and a media source can be built for the viewer who opened
/// it. A recording has neither: the media source Jellyfin publishes for one is written when the
/// channel is listed, long before anybody asks to play it, and it is the same source for every
/// viewer. Making it client-dependent would mean caching one client's answer under the recording's
/// name and handing it to the next.
/// </para>
/// <para>
/// So the decision is made where it belongs -- in the request that asks how to play this recording
/// now, for this session. Jellyfin's own playback negotiation already takes three parameters that
/// say which deliveries are on offer, and this sets those three and nothing else. The transcode
/// that follows is Jellyfin's, chosen by Jellyfin, and the recording endpoint still serves the
/// file TVHeadend stored.
/// </para>
/// <para>
/// Every precondition below fails open. An item the library does not know, an item that is not
/// ours, a recording with no external identifier, an analysis that could not be made, evidence
/// that settles nothing -- all of them leave the request exactly as it arrived. The failure this
/// guards against is a black screen on one client; the failure it must not cause is re-encoding
/// everybody's recordings on a guess.
/// </para>
/// </remarks>
public sealed class RecordingPlaybackCompatibilityFilter : IAsyncActionFilter
{
    /// <summary>
    /// The route of the one endpoint this applies to.
    /// </summary>
    /// <remarks>
    /// Matched on the route template rather than on the controller or action name, because the
    /// template is the part of Jellyfin's API that other clients are built against and therefore
    /// the part least likely to be renamed underneath a plugin.
    /// </remarks>
    private const string PlaybackInfoRoute = "Items/{itemId}/PlaybackInfo";

    private const string ItemIdArgument = "itemId";
    private const string EnableDirectPlayArgument = "enableDirectPlay";
    private const string EnableDirectStreamArgument = "enableDirectStream";
    private const string AllowVideoStreamCopyArgument = "allowVideoStreamCopy";

    private readonly ILibraryManager _libraryManager;
    private readonly IRecordingAnalyser _analysisService;
    private readonly ILogger<RecordingPlaybackCompatibilityFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingPlaybackCompatibilityFilter"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library, which says whose item this is.</param>
    /// <param name="analysisService">What is known about the recording itself.</param>
    /// <param name="logger">The logger.</param>
    public RecordingPlaybackCompatibilityFilter(
        ILibraryManager libraryManager,
        IRecordingAnalyser analysisService,
        ILogger<RecordingPlaybackCompatibilityFilter> logger)
    {
        _libraryManager = libraryManager;
        _analysisService = analysisService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            await WithdrawUnstartableDeliveries(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller has gone. Nothing to decide and nothing to log.
        }
#pragma warning disable CA1031 // A workaround must never be the reason a request fails.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogError(exception, "The recording playback compatibility check could not be made");
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Takes direct play, direct stream and video stream copy off the table when this client
    /// cannot start on what this recording contains.
    /// </summary>
    private async Task WithdrawUnstartableDeliveries(ActionExecutingContext context)
    {
        if (!IsPostedPlaybackInfo(context))
        {
            return;
        }

        // The session Jellyfin authenticated, not a header. Everything else is left alone before
        // a single byte of the recording is touched.
        if (!PlaybackClient.NeedsIdrEntryPointFor(context.HttpContext.User))
        {
            return;
        }

        if (context.ActionArguments.TryGetValue(ItemIdArgument, out var argument) is false
            || argument is not Guid itemId
            || itemId.Equals(default))
        {
            return;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !TvheadendItems.IsRecording(_libraryManager, item))
        {
            return;
        }

        // What TVHeadend calls this recording, which Jellyfin stored when the channel listed it.
        var recordingId = item.ExternalId;
        if (string.IsNullOrEmpty(recordingId))
        {
            return;
        }

        // Whether the recording has finished is not known here, and saying so costs at most one
        // further reading; the channel, which does know, is about to say so for the same one.
        var analysis = await _analysisService
            .AnalyseAsync(recordingId, recordingHasFinished: false, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (!PlaybackCompatibilityPolicy.RequiresVideoReencode(true, analysis.EntryPointEvidence))
        {
            return;
        }

        // Withdrawing direct play is not enough on its own: Jellyfin will still transcode with the
        // video stream copied, which hands the same pictures to the same decoder in a different
        // container. The third parameter is the one that makes it a real re-encode.
        context.ActionArguments[EnableDirectPlayArgument] = false;
        context.ActionArguments[EnableDirectStreamArgument] = false;
        context.ActionArguments[AllowVideoStreamCopyArgument] = false;

        _logger.LogInformation(
            "TVHeadend recording {RecordingId} carries no IDR entry point in the sample examined; "
            + "direct play, direct stream and video stream copy withdrawn for this request",
            recordingId);
    }

    /// <summary>
    /// Whether this is the request that negotiates how one item is to be played.
    /// </summary>
    /// <remarks>
    /// The posted form only. The GET form takes none of the parameters this sets, and every client
    /// that negotiates playback in earnest posts.
    /// </remarks>
    private static bool IsPostedPlaybackInfo(ActionExecutingContext context)
        => HttpMethods.IsPost(context.HttpContext.Request.Method)
        && string.Equals(
            context.ActionDescriptor.AttributeRouteInfo?.Template,
            PlaybackInfoRoute,
            StringComparison.OrdinalIgnoreCase);
}
