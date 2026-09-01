using System;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Where this server keeps the ring buffer of a running channel.
/// </summary>
/// <remarks>
/// <para>
/// Resolved once, and the leftovers of a previous run cleared once, because both are facts about
/// this host rather than about any one stream. They used to happen in the live TV service's
/// constructor, where a service being built for the first time had a filesystem side effect and
/// nothing said so.
/// </para>
/// <para>
/// Sweeping at startup rather than on a timer: a buffer belongs to a stream, and a stream that has
/// gone leaves its file behind only when the process did not shut down cleanly. The next start is
/// exactly when that is knowable and nothing is holding it.
/// </para>
/// </remarks>
public sealed class LiveBufferLocation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LiveBufferLocation"/> class.
    /// </summary>
    /// <param name="configurationManager">Jellyfin's paths, which say where transcodes go.</param>
    /// <param name="logger">The logger.</param>
    public LiveBufferLocation(IConfigurationManager configurationManager, ILogger<LiveBufferLocation> logger)
    {
        ArgumentNullException.ThrowIfNull(configurationManager);
        ArgumentNullException.ThrowIfNull(logger);

        Path = LiveBufferDirectory.Resolve(configurationManager);
        LiveBufferDirectory.RemoveOrphaned(Path, logger);
    }

    /// <summary>
    /// Gets the directory every live buffer is written into.
    /// </summary>
    public string Path { get; }
}
