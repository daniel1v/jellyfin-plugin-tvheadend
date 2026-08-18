using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using Tvheadend.Htsp;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.Tvheadend;
using TVHeadEnd.Tvheadend.Catalogs;

namespace TVHeadEnd.LiveTv;

/// <summary>
/// Creates, changes and cancels what TVHeadend records.
/// </summary>
/// <remarks>
/// The whole of the plugin's write access to the DVR, kept apart from reading it: the catalogues
/// are fed by the server's own announcements and never by what this class just asked for. Every
/// operation is one request whose reply is awaited, so a failure is reported where it happened
/// rather than noticed later as a timer that never appeared.
/// </remarks>
public sealed class TvheadendDvr
{
    /// <summary>
    /// DVR_AUTOREC_BTYPE_ALL, meaning record any broadcast.
    /// </summary>
    private const int BroadcastTypeAll = 0;

    /// <summary>
    /// DVR_AUTOREC_BTYPE_NEW_OR_UNKNOWN, meaning record only what is flagged new or unflagged.
    /// </summary>
    private const int BroadcastTypeNewOrUnknown = 1;

    private readonly TvheadendConnection _connection;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvheadendDvr"/> class.
    /// </summary>
    /// <param name="connection">The TVHeadend connection.</param>
    /// <param name="logger">The logger.</param>
    public TvheadendDvr(TvheadendConnection connection, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _logger = logger;
    }

    /// <summary>
    /// Gets when the recordings last changed, which is what Jellyfin polls to refresh them.
    /// </summary>
    public DateTime LastRecordingChange { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// Schedules a recording.
    /// </summary>
    /// <param name="info">The timer to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once TVHeadend has accepted it.</returns>
    public async Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        var request = HtspMessage.Create("addDvrEntry")
            .Set("start", ToUnixTime(info.StartDate))
            .Set("stop", ToUnixTime(info.EndDate))
            .Set("startExtra", info.PrePaddingSeconds / 60)
            .Set("stopExtra", info.PostPaddingSeconds / 60)
            .Set("priority", _connection.Settings.Priority)
            .Set("configName", _connection.Settings.DvrProfile)
            .Set("title", info.Name ?? string.Empty)
            .Set("description", info.Overview ?? string.Empty);

        if (int.TryParse(info.ChannelId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelId))
        {
            request.Set("channelId", channelId);
        }

        await SendAsync(request, "schedule a recording", cancellationToken).ConfigureAwait(false);
        LastRecordingChange = DateTime.UtcNow;
    }

    /// <summary>
    /// Changes the padding of a scheduled recording.
    /// </summary>
    /// <param name="info">The timer to update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once TVHeadend has accepted it.</returns>
    public async Task UpdateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        var request = HtspMessage.Create("updateDvrEntry")
            .Set("id", info.Id ?? string.Empty)
            .Set("startExtra", info.PrePaddingSeconds / 60)
            .Set("stopExtra", info.PostPaddingSeconds / 60);

        await SendAsync(request, "change a recording", cancellationToken).ConfigureAwait(false);
        LastRecordingChange = DateTime.UtcNow;
    }

    /// <summary>
    /// Cancels a scheduled recording.
    /// </summary>
    /// <param name="timerId">The TVHeadend entry identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once TVHeadend has accepted it.</returns>
    public async Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        var request = HtspMessage.Create("cancelDvrEntry").Set("id", timerId);
        await SendAsync(request, "cancel a recording", cancellationToken).ConfigureAwait(false);
        LastRecordingChange = DateTime.UtcNow;
    }

    /// <summary>
    /// Deletes a recording.
    /// </summary>
    /// <param name="recordingId">The TVHeadend entry identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once TVHeadend has accepted it.</returns>
    public async Task DeleteRecordingAsync(string recordingId, CancellationToken cancellationToken)
    {
        var request = HtspMessage.Create("deleteDvrEntry").Set("id", recordingId);
        await SendAsync(request, "delete a recording", cancellationToken).ConfigureAwait(false);
        LastRecordingChange = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a series rule.
    /// </summary>
    /// <param name="info">The series timer to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once TVHeadend has accepted it.</returns>
    public async Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        var request = HtspMessage.Create("addAutorecEntry")
            .Set("configName", _connection.Settings.DvrProfile);
        ApplySeriesFields(request, info);

        await SendAsync(request, "create a series rule", cancellationToken).ConfigureAwait(false);
        LastRecordingChange = DateTime.UtcNow;
    }

    /// <summary>
    /// Changes a series rule.
    /// </summary>
    /// <param name="info">The series timer to update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once TVHeadend has accepted it.</returns>
    public async Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        var request = HtspMessage.Create("updateAutorecEntry").Set("id", info.Id ?? string.Empty);
        ApplySeriesFields(request, info);

        await SendAsync(request, "change a series rule", cancellationToken).ConfigureAwait(false);
        LastRecordingChange = DateTime.UtcNow;
    }

    /// <summary>
    /// Deletes a series rule.
    /// </summary>
    /// <param name="timerId">The TVHeadend entry identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once TVHeadend has accepted it.</returns>
    public async Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        var request = HtspMessage.Create("deleteAutorecEntry").Set("id", timerId);
        await SendAsync(request, "delete a series rule", cancellationToken).ConfigureAwait(false);
        LastRecordingChange = DateTime.UtcNow;
    }

    private void ApplySeriesFields(HtspMessage request, SeriesTimerInfo info)
    {
        request.Set("title", info.Name ?? string.Empty);

        // A negative channelId means "any channel" from HTSP v25 on; older servers read an
        // absent channelId the same way, so it is only sent for a channel-bound rule.
        if (!info.RecordAnyChannel
            && int.TryParse(info.ChannelId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelId))
        {
            request.Set("channelId", channelId);
        }
        else
        {
            request.Set("channelId", -1);
        }

        if (info.Days is { Count: > 0 and < 7 })
        {
            request.Set("daysOfWeek", SeriesRuleCatalog.ToDaysOfWeek(info.Days));
        }

        // Minutes from midnight, with -1 meaning any time.
        if (info.RecordAnyTime)
        {
            request.Set("start", -1);
            request.Set("startWindow", -1);
        }
        else
        {
            var start = SeriesRuleCatalog.ToMinutesFromMidnight(info.StartDate);
            request.Set("start", start);
            request.Set("startWindow", (start + 30) % (24 * 60));
        }

        request.Set("startExtra", info.PrePaddingSeconds / 60);
        request.Set("stopExtra", info.PostPaddingSeconds / 60);
        request.Set("priority", _connection.Settings.Priority);
        request.Set("broadcastType", info.RecordNewOnly ? BroadcastTypeNewOrUnknown : BroadcastTypeAll);
    }

    private async Task SendAsync(HtspMessage request, string what, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await _connection.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // TVHeadend answers a DVR change with an explicit success flag rather than an error,
            // so a reply on its own does not mean it worked.
            if (reply.Contains("success") && !reply.GetBoolean("success"))
            {
                _logger.LogError(
                    "TVHeadend would not {What}: {Reason}",
                    what,
                    reply.GetString("error") ?? "no reason given");
            }
        }
        catch (HtspException exception)
        {
            _logger.LogError(exception, "TVHeadend would not {What}", what);
            throw;
        }
    }

    private static long ToUnixTime(DateTime value)
        => ((DateTimeOffset)DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
