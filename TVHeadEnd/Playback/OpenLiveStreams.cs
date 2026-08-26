using System;
using System.Collections.Generic;
using TVHeadEnd.Streaming;

namespace TVHeadEnd.Playback;

/// <summary>
/// Which of this plugin's live streams a media source identifier currently stands for.
/// </summary>
/// <remarks>
/// <para>
/// One question, asked in one place: a client fetching the static video endpoint names the media
/// source it wants and, on some clients, nothing else. Jellyfin needs the live stream identifier
/// to reach the stream through its provider, and this is what lets the identifier be looked up
/// rather than guessed. Nothing here is inferred from a client name, a user agent, a channel name
/// or the order things happened in.
/// </para>
/// <para>
/// The references are weak, deliberately. This is a lookup, not an owner: a stream stays open
/// exactly as long as its viewers keep it open, and an entry that outlives its stream answers
/// nothing rather than holding a tuner. Entries whose stream has gone are dropped as they are
/// found.
/// </para>
/// </remarks>
public sealed class OpenLiveStreams
{
    private readonly Dictionary<string, WeakReference<TvheadendLiveStream>> _streams = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// Records which stream a media source identifier now stands for.
    /// </summary>
    /// <param name="mediaSourceId">The media source the client will name.</param>
    /// <param name="stream">The stream that was opened or reused for it.</param>
    public void Register(string mediaSourceId, TvheadendLiveStream stream)
    {
        ArgumentException.ThrowIfNullOrEmpty(mediaSourceId);
        ArgumentNullException.ThrowIfNull(stream);

        lock (_lock)
        {
            _streams[mediaSourceId] = new WeakReference<TvheadendLiveStream>(stream);
        }
    }

    /// <summary>
    /// The stream a media source identifier stands for, if one is still open for it.
    /// </summary>
    /// <param name="mediaSourceId">The media source named by the request.</param>
    /// <returns>The stream, or <see langword="null"/> when none is known or it has gone.</returns>
    public TvheadendLiveStream? Find(string? mediaSourceId)
    {
        if (string.IsNullOrEmpty(mediaSourceId))
        {
            return null;
        }

        lock (_lock)
        {
            if (!_streams.TryGetValue(mediaSourceId, out var reference))
            {
                return null;
            }

            if (reference.TryGetTarget(out var stream))
            {
                return stream;
            }

            // Collected, so the entry describes nothing. Dropping it here keeps the table the
            // size of what is actually open without a sweep of its own.
            _streams.Remove(mediaSourceId);
            return null;
        }
    }
}
