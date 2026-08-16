using System;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.Domain
{
    /// <summary>
    /// One TVHeadend DVR entry, whatever state it is in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TVHeadend does not distinguish a timer from a recording: both are this entry, moving from
    /// <see cref="DvrState.Scheduled"/> through <see cref="DvrState.Recording"/> to
    /// <see cref="DvrState.Completed"/>. Jellyfin does distinguish them, asking for timers
    /// through ILiveTvService and for recordings through IChannel, so the split belongs in the
    /// mappers that answer those two questions -- not in how the entry is read from the server.
    /// </para>
    /// <para>
    /// Series rules are a separate TVHeadend entity, the autorec entry, and are modelled
    /// separately. What an entry keeps of one is <see cref="AutoRecId"/>: the rule that created
    /// it.
    /// </para>
    /// </remarks>
    public sealed record DvrEntry
    {
        private static readonly DateTime UnixEpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Gets the TVHeadend identifier of the entry.
        /// </summary>
        public required string Id { get; init; }

        public DvrState State { get; init; }

        public string? ChannelId { get; init; }

        /// <summary>
        /// Gets the identifier of the EPG event this entry was made from, if any.
        /// </summary>
        public string? EventId { get; init; }

        /// <summary>
        /// Gets the identifier of the series rule that created this entry, if any.
        /// </summary>
        public string? AutoRecId { get; init; }

        public string? Title { get; init; }

        public string? Subtitle { get; init; }

        public string? Description { get; init; }

        public DateTime StartUtc { get; init; }

        public DateTime StopUtc { get; init; }

        public TimeSpan PrePadding { get; init; }

        public TimeSpan PostPadding { get; init; }

        public int? Priority { get; init; }

        /// <summary>
        /// Gets the path of the recording on the TVHeadend server. Of no use to Jellyfin, which
        /// generally runs elsewhere, but it is what the server reports.
        /// </summary>
        public string? FilePath { get; init; }

        /// <summary>
        /// Gets the server-relative address the recording is served from, as TVHeadend states it.
        /// </summary>
        public string? Url { get; init; }

        /// <summary>
        /// Gets what TVHeadend reports went wrong, if anything.
        /// </summary>
        public string? Error { get; init; }

        /// <summary>
        /// Gets a value indicating whether the recording TVHeadend still lists no longer has a
        /// file behind it.
        /// </summary>
        /// <remarks>
        /// A removed recording keeps its entry, and its state stays "completed"; the only sign is
        /// an error mentioning a missing file. Listing it would offer something unplayable.
        /// </remarks>
        public bool FileIsMissing =>
            Error is not null && Error.Contains("missing", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Reads an entry from the HTSP message TVHeadend sent for it.
        /// </summary>
        /// <param name="message">The <c>dvrEntryAdd</c> or <c>dvrEntryUpdate</c> message.</param>
        /// <returns>The entry, or <see langword="null"/> if the message carries no identifier.</returns>
        public static DvrEntry? FromMessage(HTSMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            var id = ReadInt(message, "id");
            if (id is null)
            {
                return null;
            }

            return new DvrEntry
            {
                Id = id,
                State = ReadState(ReadString(message, "state")),
                ChannelId = ReadInt(message, "channel"),
                EventId = ReadInt(message, "eventId"),
                AutoRecId = ReadString(message, "autorecId"),
                Title = ReadString(message, "title"),
                Subtitle = ReadString(message, "subtitle"),

                // Up to HTSP v31 "description" is a collapsed fallback of
                // description/summary/subtitle; from v32 on the three fields are independent, so
                // fall back to keep an overview in both layouts.
                Description = ReadString(message, "description")
                    ?? ReadString(message, "summary")
                    ?? ReadString(message, "subtitle"),

                StartUtc = ReadUnixTime(message, "start"),
                StopUtc = ReadUnixTime(message, "stop"),

                // TVHeadend states padding in minutes.
                PrePadding = TimeSpan.FromMinutes(ReadLong(message, "startExtra") ?? 0),
                PostPadding = TimeSpan.FromMinutes(ReadLong(message, "stopExtra") ?? 0),

                Priority = ReadIntValue(message, "priority"),
                FilePath = ReadString(message, "path"),
                Url = ReadString(message, "url"),
                Error = ReadString(message, "error"),
            };
        }

        private static DvrState ReadState(string? state) => state switch
        {
            "scheduled" => DvrState.Scheduled,
            "recording" => DvrState.Recording,
            "completed" => DvrState.Completed,
            "missed" => DvrState.Missed,
            "invalid" => DvrState.Invalid,
            _ => DvrState.Unknown,
        };

        private static string? ReadString(HTSMessage message, string field)
        {
            try
            {
                return message.ContainsField(field) ? message.GetString(field) : null;
            }
            catch (InvalidCastException)
            {
                return null;
            }
        }

        private static string? ReadInt(HTSMessage message, string field)
        {
            var value = ReadIntValue(message, field);
            return value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static int? ReadIntValue(HTSMessage message, string field)
        {
            try
            {
                return message.ContainsField(field) ? message.GetInt(field) : null;
            }
            catch (InvalidCastException)
            {
                return null;
            }
        }

        private static long? ReadLong(HTSMessage message, string field)
        {
            try
            {
                return message.ContainsField(field) ? message.GetLong(field) : null;
            }
            catch (InvalidCastException)
            {
                return null;
            }
        }

        private static DateTime ReadUnixTime(HTSMessage message, string field)
        {
            var seconds = ReadLong(message, field);
            return seconds is null ? default : UnixEpochUtc.AddSeconds(seconds.Value);
        }
    }
}
