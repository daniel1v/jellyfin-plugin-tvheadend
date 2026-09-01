using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using Tvheadend.Htsp;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.Tvheadend;
using TVHeadEnd.Tvheadend.Catalogs;
using DvrEntry = TVHeadEnd.Core.Dvr.DvrEntry;
using DvrState = TVHeadEnd.Core.Dvr.DvrState;

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
    /// How wide a start window a new rule is given, in minutes.
    /// </summary>
    /// <remarks>
    /// Only for a rule that has never had one. An existing rule keeps its own.
    /// </remarks>
    private const int DefaultStartWindowMinutes = 30;

    /// <summary>
    /// The characters POSIX extended regular expressions give a meaning to.
    /// </summary>
    private const string RegexMetacharacters = @"\^$.[]|()*+?{}";

    private readonly TvheadendConnection _connection;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvheadendDvr"/> class.
    /// </summary>
    /// <param name="connection">The TVHeadend connection.</param>
    /// <param name="logger">The logger.</param>
    public TvheadendDvr(TvheadendConnection connection, ILogger<TvheadendDvr> logger)
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
    /// <returns>The identifier TVHeadend gave the new entry.</returns>
    /// <exception cref="HtspException">
    /// TVHeadend refused the request, or accepted it without naming the entry it made.
    /// </exception>
    public async Task<string> CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        var request = BuildCreateTimerRequest(info, _connection.Settings);
        var reply = await SendAsync(request, "schedule a recording", cancellationToken).ConfigureAwait(false);

        // Nothing is written to the catalog here on purpose. The entry appears when TVHeadend
        // announces it with dvrEntryAdd, which is the same moment a recording made in the web
        // interface appears, and having one path that guesses and another that is told is how the
        // two disagree.
        return RequireNewEntryId(reply, "a recording");
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
    /// Reads the identifier of a newly created entry, insisting there is one.
    /// </summary>
    /// <remarks>
    /// Jellyfin is told this identifier through ISupportsNewTimerIds and keeps it as the timer's
    /// own, so an empty one is not a smaller answer than a real one -- it is a timer Jellyfin
    /// records under nothing, cannot find again, and cannot update or cancel. An accepted request
    /// that names no entry is the server not keeping its side of the protocol, and saying so where
    /// it happened is the only place it can still be understood.
    /// </remarks>
    /// <param name="reply">The accepted reply TVHeadend sent.</param>
    /// <param name="what">What was being created, for the message.</param>
    /// <returns>The identifier.</returns>
    /// <exception cref="HtspException">The reply named no entry.</exception>
    internal static string RequireNewEntryId(HtspMessage reply, string what)
        => ReadNewEntryId(reply)
            ?? throw new HtspException($"TVHeadend accepted {what} but did not say which entry it made.");

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
    public async Task<string> CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        // Only when the rule actually states a time. An "any time" rule is written as -1/-1 and
        // has no clock in it at all.
        var serverOffset = info.RecordAnyTime
            ? TimeSpan.Zero
            : await _connection.GetServerOffsetAsync(cancellationToken).ConfigureAwait(false);

        var request = HtspMessage.Create("addAutorecEntry")
            .Set("configName", _connection.Settings.DvrProfile);
        ApplySeriesFields(request, info, existing: null, serverOffset);

        var reply = await SendAsync(request, "create a series rule", cancellationToken).ConfigureAwait(false);

        // What the rule is made of is unchanged; only its identifier now travels back, because
        // ISupportsNewTimerIds asks for both create methods and answering one of them with
        // nothing would leave series rules worse off than they are.
        return RequireNewEntryId(reply, "a series rule");
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

        // The rule as TVHeadend has it. Everything Jellyfin could not show, and therefore could
        // not return, is read back from here rather than replaced by an editor default.
        var existing = _connection.SeriesRules.Find(info.Id);

        // Asked for only where the answer is used: a rule keeping the window the server already
        // has needs no conversion, and asking anyway would be one request per edit for a number
        // nothing reads.
        var serverOffset = NeedsServerClock(info, existing)
            ? await _connection.GetServerOffsetAsync(cancellationToken).ConfigureAwait(false)
            : TimeSpan.Zero;

        var request = HtspMessage.Create("updateAutorecEntry").Set("id", info.Id ?? string.Empty);
        ApplySeriesFields(request, info, existing, serverOffset);

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

    /// <summary>
    /// Fills in the fields an autorec entry is created or changed with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TVHeadend applies only the fields a request mentions, so what is left out survives and what
    /// is sent is overwritten. Both halves of that matter: a field Jellyfin narrowed has to be
    /// sent even when it now means "no restriction", or the old restriction stays; and a field
    /// Jellyfin cannot show has to be left out, or the editor's default replaces whatever the
    /// server was set to.
    /// </para>
    /// <para>
    /// <paramref name="existing"/> is the rule as the server last announced it, where there is
    /// one. It is what makes an edit that changed only the padding leave everything else alone.
    /// </para>
    /// </remarks>
    /// <param name="request">The request being built.</param>
    /// <param name="info">The series timer Jellyfin is asking for.</param>
    /// <param name="existing">The rule as TVHeadend has it, for a rule that already exists.</param>
    /// <param name="serverOffset">How far the TVHeadend server's clock is from UTC.</param>
    internal static void ApplySeriesFields(
        HtspMessage request,
        SeriesTimerInfo info,
        SeriesRule? existing,
        TimeSpan serverOffset)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(info);

        // What the rule is bound to. Jellyfin drops SeriesId on its way back from the client -- it
        // is not in the series timer DTO -- so an update would otherwise unbind a rule from its
        // series every time somebody touched it. Taken from the server's own copy where Jellyfin
        // did not return one.
        var seriesLink = !string.IsNullOrEmpty(info.SeriesId) ? info.SeriesId : existing?.SeriesLink;
        if (!string.IsNullOrEmpty(seriesLink))
        {
            request.Set("serieslinkUri", seriesLink);
        }

        // The rule's readable name, which is what a person calls it. Always sent, and freely
        // updated from Jellyfin: it is the one of the two title-ish fields Jellyfin actually has.
        request.Set("name", info.Name ?? string.Empty);

        // The pattern the rule matches programme titles with, which is a different field. Written
        // once, when the rule is made: TVHeadend reads it as a regular expression, so it goes out
        // escaped -- unescaped, "Law & Order: S.V.U." matches titles nobody asked for and a title
        // of "(" is not a pattern at all.
        //
        // Never rewritten afterwards. Jellyfin has no editor for it, so an update carries no
        // opinion about it; deriving it from the name again on every edit would overwrite a
        // pattern somebody wrote by hand in TVHeadend, and would escape an already-escaped one a
        // second time. Where the link is present TVHeadend matches on that and does not consult
        // this at all.
        if (string.IsNullOrEmpty(existing?.TitlePattern))
        {
            request.Set("title", EscapeForTitleMatch(info.Name));
        }

        // The note kept on the rule. Jellyfin shows it as the overview and can edit it, emptying
        // included, so it is sent as it comes back.
        request.Set("comment", info.Overview ?? string.Empty);

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

        // Always sent, including when it means every day. Leaving the field out on an update left
        // the old filter in place, so a rule somebody had narrowed to Mondays could never be
        // widened again from Jellyfin.
        //
        // What an empty list means depends on where it came from. A new timer that was never
        // given any days is the ordinary daily rule, and TVHeadend spells that 0x7F. A rule the
        // server itself has at zero -- which matches nothing -- reads back as the same empty list,
        // and writing 0x7F for it would silently switch it on.
        request.Set(
            "daysOfWeek",
            SeriesRuleFields.ToDaysOfWeek(
                info.Days ?? [],
                existing is null ? SeriesRuleFields.AllDaysOfWeek : SeriesRuleFields.NoDaysOfWeek));

        // Minutes from midnight on the server's clock, with -1 meaning any time.
        if (info.RecordAnyTime)
        {
            request.Set("start", SeriesRuleFields.AnyTime);
            request.Set("startWindow", SeriesRuleFields.AnyTime);
        }
        else if (KeepsItsExistingWindow(info, existing))
        {
            // The rule already had a window and still wants one, and Jellyfin has no way to say
            // anything else about it. The server's own numbers go back untouched -- converting
            // them out and in again would move the rule by however much the server's offset had
            // shifted since it was read, which across a daylight saving change is an hour.
            request.Set("start", existing!.Start!.Value);
            request.Set("startWindow", existing.StartWindow!.Value);
        }
        else
        {
            // A window being set for the first time, or a rule leaving "any time" for one. This
            // is the moment Jellyfin's times mean something, and they are read with the offset the
            // server reports now.
            var start = SeriesRuleFields.ToMinutesFromMidnight(info.StartDate, serverOffset);
            request.Set("start", start);
            request.Set("startWindow", WindowEndFor(info, existing, serverOffset, start));
        }

        request.Set("startExtra", info.PrePaddingSeconds / 60);
        request.Set("stopExtra", info.PostPaddingSeconds / 60);

        // The rule's own priority, which is what was published for it and what comes back. This
        // used to be the configured default, so every edit of any rule reset its priority to the
        // one setting.
        request.Set("priority", info.Priority);

        // How many recordings the rule keeps. Zero is unlimited on both sides.
        request.Set("maxCount", info.KeepUpTo);

        // Only where Jellyfin's answer can mean what TVHeadend would store. The server has
        // broadcast types beyond "all" and "new or unknown"; a rule set to one of those has no
        // representation in Jellyfin, comes back as RecordNewOnly = false, and would be quietly
        // reset to "all" if that were written. Left alone instead.
        if (CanWriteBroadcastType(existing))
        {
            request.Set(
                "broadcastType",
                info.RecordNewOnly
                    ? SeriesRuleFields.BroadcastTypeNewOrUnknown
                    : SeriesRuleFields.BroadcastTypeAll);
        }
    }

    /// <summary>
    /// Escapes a title so that TVHeadend's regular expression matches it literally.
    /// </summary>
    /// <remarks>
    /// POSIX extended regular expressions, which is what the server compiles the title with. Only
    /// the metacharacters are escaped: a backslash before an ordinary character is undefined
    /// there, so escaping more than this would be its own bug. The match stays a substring match,
    /// as it has always been -- this makes a title mean itself, it does not anchor it.
    /// </remarks>
    /// <param name="title">The title as Jellyfin knows it.</param>
    /// <returns>The pattern to send.</returns>
    internal static string EscapeForTitleMatch(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        var escaped = new StringBuilder(title.Length + 8);
        foreach (var character in title)
        {
            if (RegexMetacharacters.Contains(character, StringComparison.Ordinal))
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Reports whether writing this rule needs to know what the server's clock reads.
    /// </summary>
    /// <remarks>
    /// Only when a time actually has to be converted: a rule with no time restriction is written
    /// as -1/-1, and one that keeps the window the server already has is written from the server's
    /// own numbers. Everything else -- padding, priority, days, the channel -- is the same on any
    /// clock.
    /// </remarks>
    /// <param name="info">The series timer Jellyfin is asking for.</param>
    /// <param name="existing">The rule as TVHeadend has it, if it exists yet.</param>
    /// <returns>Whether the server has to be asked for its offset.</returns>
    private static bool NeedsServerClock(SeriesTimerInfo info, SeriesRule? existing)
        => !info.RecordAnyTime && !KeepsItsExistingWindow(info, existing);

    /// <summary>
    /// Reports whether a rule keeps the start window it already has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It does whenever it had one and still wants one. Jellyfin's editor has no field for a start
    /// window -- only the "any time" switch -- so an update carries no opinion about the minutes,
    /// and the only change it can actually express is turning the restriction off.
    /// </para>
    /// <para>
    /// Which means the times must not be recomputed. Jellyfin returns the instants this plugin
    /// published, read with whatever the server's offset was then; reading them back with the
    /// offset now moves the rule by any difference between the two, and a daylight saving change
    /// between a listing and an unrelated edit is an hour. Changing the window itself is done in
    /// TVHeadend, which is where the field lives.
    /// </para>
    /// </remarks>
    /// <param name="info">The series timer Jellyfin is asking for.</param>
    /// <param name="existing">The rule as TVHeadend has it, if it exists yet.</param>
    /// <returns>Whether to send the server's own numbers back unchanged.</returns>
    private static bool KeepsItsExistingWindow(SeriesTimerInfo info, SeriesRule? existing)
        => !info.RecordAnyTime
            && SeriesRuleFields.IsTimeOfDay(existing?.Start)
            && SeriesRuleFields.IsTimeOfDay(existing?.StartWindow);

    /// <summary>
    /// Reports whether the broadcast type is one this plugin may write.
    /// </summary>
    /// <param name="existing">The rule as TVHeadend has it, if it exists yet.</param>
    /// <returns>Whether to send it.</returns>
    private static bool CanWriteBroadcastType(SeriesRule? existing)
        => existing?.BroadcastType is null
            or SeriesRuleFields.BroadcastTypeAll
            or SeriesRuleFields.BroadcastTypeNewOrUnknown;

    /// <summary>
    /// Gets the last minute of the start window to send.
    /// </summary>
    /// <remarks>
    /// The window is published as the span between the timer's start and end, so an unedited
    /// series timer returns the same span and the same two minute values reach the server again.
    /// Jellyfin has no editor for it, so a timer that arrives without one keeps whatever window
    /// the rule already had.
    /// </remarks>
    /// <param name="info">The series timer Jellyfin is asking for.</param>
    /// <param name="existing">The rule as TVHeadend has it, if it exists yet.</param>
    /// <param name="serverOffset">How far the TVHeadend server's clock is from UTC.</param>
    /// <param name="start">The first minute of the window, already converted.</param>
    /// <returns>The last minute of the window.</returns>
    private static int WindowEndFor(
        SeriesTimerInfo info,
        SeriesRule? existing,
        TimeSpan serverOffset,
        int start)
    {
        if (info.EndDate >= info.StartDate && info.EndDate != default)
        {
            return SeriesRuleFields.ToMinutesFromMidnight(info.EndDate, serverOffset);
        }

        if (SeriesRuleFields.IsTimeOfDay(existing?.Start) && SeriesRuleFields.IsTimeOfDay(existing?.StartWindow))
        {
            return (start + SeriesRuleFields.WindowLength(existing!.Start!.Value, existing.StartWindow!.Value))
                % (24 * 60);
        }

        return (start + DefaultStartWindowMinutes) % (24 * 60);
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
