using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Media
{
    /// <summary>
    /// Keeps what each channel was observed to be, across restarts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives in the plugin's data directory rather than in the configuration, because it is
    /// observation and not settings: a user who exports their configuration should not carry
    /// a snapshot of somebody's transponder with it, and a user who edits their settings should
    /// not silently discard an analysis.
    /// </para>
    /// <para>
    /// This replaces three separate mechanisms that answered overlapping questions with
    /// different lifetimes -- a probe cache keyed by PMT fingerprint, a re-encode verdict kept
    /// for the life of the process, and a list of channel identifiers in the configuration.
    /// </para>
    /// </remarks>
    public sealed class ChannelMediaDescriptorStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        private readonly string _path;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, ChannelMediaDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _saveGate = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="ChannelMediaDescriptorStore"/> class.
        /// </summary>
        /// <param name="applicationPaths">The Jellyfin application paths.</param>
        /// <param name="logger">The logger.</param>
        public ChannelMediaDescriptorStore(IApplicationPaths applicationPaths, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(applicationPaths);
            ArgumentNullException.ThrowIfNull(logger);

            _logger = logger;
            _path = Path.Combine(applicationPaths.DataPath, "tvheadend", "channel-media-descriptors.json");
            Load();
        }

        /// <summary>
        /// Gets the number of stored descriptors.
        /// </summary>
        public int Count => _descriptors.Count;

        /// <summary>
        /// Returns the descriptor of a channel if it is still current.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="nativeProfile">The stream profile now configured for native playback.</param>
        /// <param name="variantRole">
        /// Which delivery form to look up, or <see langword="null"/> for the broadcast itself.
        /// </param>
        /// <returns>The descriptor, or <see langword="null"/> when none applies.</returns>
        public ChannelMediaDescriptor? Get(string channelId, string? nativeProfile, string? variantRole = null)
        {
            if (string.IsNullOrEmpty(channelId))
            {
                return null;
            }

            if (!_descriptors.TryGetValue(ChannelMediaDescriptor.Key(channelId, variantRole), out var descriptor))
            {
                return null;
            }

            return descriptor.IsCurrentFor(nativeProfile) ? descriptor : null;
        }

        /// <summary>
        /// Reports whether a channel needs analysing.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="nativeProfile">The stream profile now configured for native playback.</param>
        /// <returns>Whether nothing current is known about it.</returns>
        public bool NeedsAnalysis(string channelId, string? nativeProfile)
            => Get(channelId, nativeProfile) is null;

        /// <summary>
        /// Stores the result of a successful analysis. Failures are never stored: a channel that
        /// could not be analysed stays unknown, which is a state the policy handles.
        /// </summary>
        /// <param name="descriptor">What was observed.</param>
        public void Record(ChannelMediaDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            if (!descriptor.IsUsable)
            {
                return;
            }

            _descriptors[descriptor.StorageKey] = descriptor;
            _logger.LogDebug(
                "TVHeadend channel descriptor: {ChannelId} is {Container}/{Codec}, random access {RandomAccess}",
                descriptor.ChannelId,
                descriptor.Container,
                descriptor.VideoCodec ?? "<unknown>",
                descriptor.RandomAccess);

            Save();
        }

        /// <summary>
        /// Discards the descriptor of one channel, so the next playback analyses it again.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        public void Invalidate(string channelId)
        {
            if (string.IsNullOrEmpty(channelId))
            {
                return;
            }

            // Every variant of the channel goes, not only the native one: what was observed of a
            // compatibility output describes a stream produced from the broadcast that is now in
            // doubt.
            var removed = _descriptors.Keys
                .Where(key => _descriptors.TryGetValue(key, out var entry)
                    && string.Equals(entry.ChannelId, channelId, StringComparison.OrdinalIgnoreCase))
                .Count(key => _descriptors.TryRemove(key, out _));

            if (removed > 0)
            {
                Save();
            }
        }

        /// <summary>
        /// Discards every descriptor, for the settings action that rebuilds them after the
        /// TVHeadend configuration has changed.
        /// </summary>
        /// <returns>How many were discarded.</returns>
        public int InvalidateAll()
        {
            var count = _descriptors.Count;
            _descriptors.Clear();
            Save();
            _logger.LogInformation("TVHeadend channel descriptors: discarded {Count} descriptor(s) for re-analysis", count);
            return count;
        }

        /// <summary>
        /// Drops descriptors of channels TVHeadend no longer offers.
        /// </summary>
        /// <param name="knownChannelIds">The channels that currently exist.</param>
        public void RemoveMissingChannels(IEnumerable<string> knownChannelIds)
        {
            ArgumentNullException.ThrowIfNull(knownChannelIds);

            var known = new HashSet<string>(knownChannelIds, StringComparer.OrdinalIgnoreCase);
            var removed = 0;
            foreach (var entry in _descriptors.ToList())
            {
                if (!known.Contains(entry.Value.ChannelId) && _descriptors.TryRemove(entry.Key, out _))
                {
                    removed++;
                }
            }

            if (removed > 0)
            {
                _logger.LogInformation("TVHeadend channel descriptors: removed {Count} descriptor(s) of channels that no longer exist", removed);
                Save();
            }
        }

        /// <summary>
        /// Takes over the channels an earlier version recorded as carrying no IDR frames.
        /// </summary>
        /// <remarks>
        /// They become a <see cref="Streaming.H264RandomAccessKind.RecoveryOpenGop"/> observation
        /// with no streams, which is exactly what was known about them -- and, because a
        /// descriptor without streams is not usable, it is deliberately not enough to skip the
        /// analysis. It only stops the first tune from handing out a stream already known not to
        /// start on affected clients.
        /// </remarks>
        /// <param name="channelIds">The channels the old configuration lists.</param>
        public void SeedFromKnownChannelsWithoutIdr(IEnumerable<string> channelIds)
        {
            ArgumentNullException.ThrowIfNull(channelIds);

            var seeded = 0;
            foreach (var channelId in channelIds.Where(id => !string.IsNullOrEmpty(id)))
            {
                if (_descriptors.ContainsKey(channelId))
                {
                    continue;
                }

                _descriptors[channelId] = new ChannelMediaDescriptor
                {
                    ChannelId = channelId,
                    RandomAccess = Streaming.H264RandomAccessKind.RecoveryOpenGop,
                    VideoStreamType = 0x1B,
                    IsTransportStream = true,
                };
                seeded++;
            }

            if (seeded > 0)
            {
                _logger.LogInformation(
                    "TVHeadend channel descriptors: carried over {Count} channel(s) previously found to send no IDR frames",
                    seeded);
                Save();
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return;
                }

                var stored = JsonSerializer.Deserialize<List<ChannelMediaDescriptor>>(
                    File.ReadAllText(_path),
                    SerializerOptions);
                if (stored is null)
                {
                    return;
                }

                var dropped = 0;
                foreach (var descriptor in stored.Where(descriptor => !string.IsNullOrEmpty(descriptor.ChannelId)))
                {
                    if (descriptor.SchemaVersion != ChannelMediaDescriptor.CurrentSchemaVersion)
                    {
                        dropped++;
                        continue;
                    }

                    _descriptors[descriptor.StorageKey] = descriptor;
                }

                _logger.LogInformation(
                    "TVHeadend channel descriptors: loaded {Count} channel(s){Dropped}",
                    _descriptors.Count,
                    dropped > 0 ? $", discarded {dropped} written by an older analysis" : string.Empty);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // Losing this costs one analysis per channel, so it is never worth failing over.
                _logger.LogWarning(exception, "TVHeadend channel descriptors: could not be read, starting empty");
            }
        }

        private void Save()
        {
            lock (_saveGate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

                    // Written beside and moved into place, so a crash mid-write cannot leave a
                    // half-written file that fails to parse on the next start.
                    var temporary = _path + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(_descriptors.Values.ToList(), SerializerOptions));
                    File.Move(temporary, _path, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
                {
                    _logger.LogWarning(exception, "TVHeadend channel descriptors: could not be written");
                }
            }
        }
    }
}
