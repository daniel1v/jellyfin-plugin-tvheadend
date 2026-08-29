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
using DvrEntry = TVHeadEnd.Domain.DvrEntry;
using DvrState = TVHeadEnd.Domain.DvrState;

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
    /// Gets a number that changes whenever TVHeadend's own view of the timers and recordings does.
    /// </summary>
    /// <remarks>
    /// Read straight from the catalog, because the catalog is what the server has confirmed. This
    /// used to be a timestamp stamped when a command of ours came back, which said only that the
    /// plugin had asked for something: a recording made in TVHeadend's web interface moved
    /// nothing, a recording starting or ending moved nothing, and a reply that overtook its own
    /// <c>dvrEntryAdd</c> moved it before the change it announced had arrived.
    /// </remarks>
    public long RecordingRevision => _connection.Dvr.Revision;

    /// <summary>
    /// Schedules a recording.
    /// </summary>
    /// <param name="info">The timer to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The identifier TVHeadend gave the new entry, or <see langword="null"/> if the reply carried
    /// none.
    /// </returns>
    public async Task<string?> CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        var request = BuildCreateTimerRequest(info, _connection.Settings);
        var reply = await SendAsync(request, "schedule a recording", cancellationToken).ConfigureAwait(false);

        // Nothing is written to the catalog here on purpose. The entry appears when TVHeadend
        // announces it with dvrEntryAdd, which is the same moment a recording made in the web
        // interface appears, and having one path that guesses and another that is told is how the
        // two disagree.
        return ReadNewEntryId(reply);
    }

    /// <summary>
    /// Builds the request that schedules one recording.
    /// </summary>
    /// <remarks>
    /// The EPG event and the times are both sent. Binding the entry to the event is what lets
    /// TVHeadend follow a broadcast that moves and what gives the recording the server's own
    /// title, subtitle and artwork; but an event is only known to the server that issued it, and
    /// a manual timer has none at all -- so the times, the channel and the title travel as well,
    /// and a server that ignores or has lost the event still records the right thing.
    /// </remarks>
    /// <param name="info">The timer to create.</param>
    /// <param name="settings">The connection settings the recording is made under.</param>
    /// <returns>The <c>addDvrEntry</c> request.</returns>
    internal static HtspMessage BuildCreateTimerRequest(TimerInfo info, TvheadendSettings settings)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(settings);

        var request = HtspMessage.Create("addDvrEntry")
            .Set("start", ToUnixTime(info.StartDate))
            .Set("stop", ToUnixTime(info.EndDate))
            .Set("startExtra", info.PrePaddingSeconds / 60)
            .Set("stopExtra", info.PostPaddingSeconds / 60)
            .Set("priority", settings.Priority)
            .Set("configName", settings.DvrProfile)
            .Set("title", info.Name ?? string.Empty)
            .Set("description", info.Overview ?? string.Empty);

        if (int.TryParse(info.ChannelId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelId))
        {
            request.Set("channelId", channelId);
        }

        // TvheadendGuide puts TVHeadend's own eventId in ProgramInfo.Id, so a timer made from the
        // guide carries the server's identifier straight back. Anything else there -- a manual
        // timer, or a program id from some other shape of guide -- is not an event, and sending it
        // as one would bind the recording to whatever event happened to have that number.
        if (int.TryParse(info.ProgramId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
        {
            request.Set("eventId", eventId);
        }

        return request;
    }

    /// <summary>
    /// Reads the identifier TVHeadend gave an entry it has just created.
    /// </summary>
    /// <remarks>
    /// Jellyfin keeps this as the timer's external identifier and asks for the timer by it
    /// afterwards, so inventing one -- or leaving Jellyfin to invent one -- means every later
    /// update and cancel names an entry the server has never heard of.
    /// </remarks>
    /// <param name="reply">The reply TVHeadend sent.</param>
    /// <returns>The identifier, or <see langword="null"/> if the reply carried none.</returns>
    internal static string? ReadNewEntryId(HtspMessage reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        // A DVR entry is numbered; an autorec entry is named by a uuid. Both arrive as "id".
        if (reply.GetInt32("id") is { } number)
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        var id = reply.GetString("id");
        return string.IsNullOrEmpty(id) ? null : id;
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
    }

    /// <summary>
    /// Cancels a scheduled recording, or stops one that has already started.
    /// </summary>
    /// <param name="timerId">The TVHeadend entry identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once TVHeadend has accepted it.</returns>
    public async Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        var method = ChooseCancelMethod(_connection.Dvr.Find(timerId));
        var request = HtspMessage.Create(method).Set("id", timerId);
        await SendAsync(request, "cancel a recording", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Chooses how to end an entry, from where the server last said it had got to.
    /// </summary>
    /// <remarks>
    /// TVHeadend has two verbs and they are not interchangeable. <c>cancelDvrEntry</c> abandons a
    /// recording that has not started; <c>stopDvrEntry</c> ends one that is running and keeps what
    /// has been recorded so far, which is what a viewer pressing stop means. Sending cancel to a
    /// running entry is the one that loses the recording.
    /// <para>
    /// Decided against the catalog, so it is the server's own view rather than whatever state the
    /// timer had when Jellyfin last listed it. An entry the catalog does not know is treated as
    /// scheduled: that is the only thing it can be if nothing has announced it starting.
    /// </para>
    /// </remarks>
    /// <param name="entry">The entry as the catalog holds it, if it holds one.</param>
    /// <returns>The HTSP method to send.</returns>
    internal static string ChooseCancelMethod(DvrEntry? entry)
        => entry?.State == DvrState.Recording ? "stopDvrEntry" : "cancelDvrEntry";

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
    }

    /// <summary>
    /// Creates a series rule.
    /// </summary>
    /// <param name="info">The series timer to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once TVHeadend has accepted it.</returns>
    public async Task<string?> CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        var request = HtspMessage.Create("addAutorecEntry")
            .Set("configName", _connection.Settings.DvrProfile);
        ApplySeriesFields(request, info);

        var reply = await SendAsync(request, "create a series rule", cancellationToken).ConfigureAwait(false);

        // What the rule is made of is unchanged; only its identifier now travels back, because
        // ISupportsNewTimerIds asks for both create methods and answering one of them with
        // nothing would leave series rules worse off than they are.
        return ReadNewEntryId(reply);
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

    private async Task<HtspMessage> SendAsync(HtspMessage request, string what, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await _connection.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // TVHeadend answers a DVR change with an explicit success flag rather than an error,
            // so a reply on its own does not mean it worked. Logging that and returning left
            // Jellyfin believing the timer had been set: it reports success to the client,
            // schedules nothing, and the recording quietly never happens. A refusal has to
            // travel back as one.
            // Not logged here: the catch below logs every refusal, and doing it in both places
            // writes the same failure to the log twice.
            EnsureAccepted(reply);

            return reply;
        }
        catch (HtspException exception)
        {
            _logger.LogError(exception, "TVHeadend would not {What}", what);
            throw;
        }
    }

    /// <summary>
    /// Throws when TVHeadend answered that it would not do what it was asked.
    /// </summary>
    /// <remarks>
    /// The flag is only meaningful when it is there. A reply that does not mention success is one
    /// from a server or a request that does not use the flag, and treating its absence as a
    /// refusal would fail every such operation.
    /// </remarks>
    /// <param name="reply">The reply TVHeadend sent.</param>
    internal static void EnsureAccepted(HtspMessage reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        if (reply.Contains("success") && !reply.GetBoolean("success"))
        {
            throw new HtspException(
                $"TVHeadend refused it: {reply.GetString("error") ?? "no reason given"}");
        }
    }

    private static long ToUnixTime(DateTime value)
        => ((DateTimeOffset)DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
