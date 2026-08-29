using System;
using Tvheadend.Htsp.Protocol;

namespace TVHeadEnd.Domain;

/// <summary>
/// One file TVHeadend actually wrote for a DVR entry.
/// </summary>
/// <remarks>
/// <para>
/// A DVR entry's <see cref="DvrEntry.StartUtc"/> and <see cref="DvrEntry.StopUtc"/> are when the
/// recording was <em>scheduled</em> for. They are not when it ran: a recording stopped by hand
/// ends early, one that never started has no file at all, and padding moves both ends. The times
/// here are the ones TVHeadend reports for the bytes on disk, and they are kept apart from the
/// scheduled ones rather than replacing them, because both questions get asked.
/// </para>
/// <para>
/// <c>stop</c> is only meaningful once the file is closed. While a recording runs TVHeadend
/// reports the file with a start and no usable stop, which is the difference between "this is how
/// long it is" and "this is still being written".
/// </para>
/// </remarks>
public sealed record DvrRecordingFile
{
    /// <summary>
    /// Gets when TVHeadend began writing the file.
    /// </summary>
    public DateTime? StartUtc { get; init; }

    /// <summary>
    /// Gets when TVHeadend closed the file, if it has.
    /// </summary>
    public DateTime? StopUtc { get; init; }

    /// <summary>
    /// Gets how large the file is, where the server states it.
    /// </summary>
    public long? Size { get; init; }

    /// <summary>
    /// Gets the path TVHeadend wrote it to. Of no use to Jellyfin, which generally runs elsewhere.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Gets how long the file runs, once it is finished.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> while the file is still being written, and for a file the server
    /// described without usable times. Both mean "not known", and neither is worth turning into a
    /// number: a duration invented for a growing file is wrong the moment it is read.
    /// </remarks>
    public TimeSpan? Duration =>
        StartUtc is { } start && StopUtc is { } stop && stop > start ? stop - start : null;

    /// <summary>
    /// Reads one entry of the <c>files</c> list TVHeadend sends with a DVR entry.
    /// </summary>
    /// <param name="message">The file map.</param>
    /// <returns>The file.</returns>
    public static DvrRecordingFile FromMessage(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new DvrRecordingFile
        {
            StartUtc = ReadUnixTime(message, "start"),
            StopUtc = ReadUnixTime(message, "stop"),
            Size = message.GetInt64("size"),
            FileName = message.GetString("filename"),
        };
    }

    private static DateTime? ReadUnixTime(HtspMessage message, string field)
    {
        // Zero is how an unset time arrives, not a recording made in 1970.
        if (message.GetInt64(field) is not { } seconds || seconds <= 0)
        {
            return null;
        }

        return DateTime.UnixEpoch.AddSeconds(seconds);
    }
}
