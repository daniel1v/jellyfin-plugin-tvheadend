using System;
using System.Collections.Generic;
using TVHeadEnd.Streaming;

namespace TVHeadEnd.Playback;

/// <summary>
/// Which of this plugin's live streams a media source identifier stands for, on which device.
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
/// The media source alone does not identify a stream. One channel can have several open at once
/// -- two viewers whose device profiles differ get a rendering each, which is the whole reason
/// the streams are per-session -- and they all carry the same media source identifier. So the
/// device is part of the key, and an ambiguous lookup answers nothing at all: handing a viewer
/// another viewer's rendering is worse than leaving the request as it arrived.
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
    private readonly Dictionary<string, List<Entry>> _streams = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// Records which stream a media source identifier stands for on one device.
    /// </summary>
    /// <param name="mediaSourceId">The media source the client will name.</param>
    /// <param name="deviceId">
    /// The device the stream was opened for, or <see langword="null"/> where there was no request
    /// to read it from.
    /// </param>
    /// <param name="stream">The stream that was opened or reused for it.</param>
    public void Register(string mediaSourceId, string? deviceId, TvheadendLiveStream stream)
    {
        ArgumentException.ThrowIfNullOrEmpty(mediaSourceId);
        ArgumentNullException.ThrowIfNull(stream);

        lock (_lock)
        {
            if (!_streams.TryGetValue(mediaSourceId, out var entries))
            {
                entries = [];
                _streams[mediaSourceId] = entries;
            }

            Prune(entries);

            // One entry per device. A device that negotiates playback again is the same viewer
            // asking a second time, not a second viewer.
            entries.RemoveAll(entry => entry.IsFor(deviceId));
            entries.Add(new Entry(deviceId, new WeakReference<TvheadendLiveStream>(stream)));
        }
    }

    /// <summary>
    /// The stream a media source identifier stands for on this device, where exactly one does.
    /// </summary>
    /// <param name="mediaSourceId">The media source named by the request.</param>
    /// <param name="deviceId">The device named by the request, if it named one.</param>
    /// <returns>
    /// The stream, or <see langword="null"/> when none is known, when it has gone, or when the
    /// request does not single one out.
    /// </returns>
    public TvheadendLiveStream? Find(string? mediaSourceId, string? deviceId)
    {
        if (string.IsNullOrEmpty(mediaSourceId))
        {
            return null;
        }

        lock (_lock)
        {
            if (!_streams.TryGetValue(mediaSourceId, out var entries))
            {
                return null;
            }

            Prune(entries);
            if (entries.Count == 0)
            {
                _streams.Remove(mediaSourceId);
                return null;
            }

            // The device the request named, where it named one and one entry answers to it.
            if (!string.IsNullOrEmpty(deviceId))
            {
                var matches = entries.FindAll(entry => entry.IsFor(deviceId));
                if (matches.Count == 1)
                {
                    return matches[0].Resolve();
                }

                if (matches.Count > 1)
                {
                    return null;
                }
            }

            // No device named, or none registered under the one named -- a stream opened outside
            // a request has no device to record. Either way this only answers where the media
            // source leaves no room for doubt.
            return entries.Count == 1 ? entries[0].Resolve() : null;
        }
    }

    /// <summary>
    /// Drops the entries whose stream has been collected.
    /// </summary>
    /// <remarks>
    /// Keeps the table the size of what is actually open without a sweep of its own, and keeps a
    /// closed stream from making a live one look ambiguous.
    /// </remarks>
    private static void Prune(List<Entry> entries)
        => entries.RemoveAll(entry => entry.Resolve() is null);

    private sealed record Entry(string? DeviceId, WeakReference<TvheadendLiveStream> Stream)
    {
        public bool IsFor(string? deviceId)
            => string.Equals(DeviceId ?? string.Empty, deviceId ?? string.Empty, StringComparison.Ordinal);

        public TvheadendLiveStream? Resolve()
            => Stream.TryGetTarget(out var stream) ? stream : null;
    }
}
