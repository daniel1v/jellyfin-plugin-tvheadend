using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Infrastructure.LiveBuffer
{
    /// <summary>
    /// Where live buffers are written, and how leftovers are cleared.
    /// </summary>
    public static class LiveBufferDirectory
    {
        /// <summary>
        /// Gets the directory the shared live buffers are written to, beside Jellyfin's
        /// transcode directory rather than inside it.
        /// </summary>
        /// <remarks>
        /// Deliberately not the transcode directory itself: Jellyfin empties that whenever any
        /// transcoding job or live stream ends, which would delete the buffer of every other
        /// stream still running, and the client then receives a source that answers 404 for the
        /// rest of its session.
        /// </remarks>
        /// <param name="configurationManager">The Jellyfin configuration manager.</param>
        /// <returns>The buffer directory.</returns>
        public static string Resolve(IConfigurationManager configurationManager)
        {
            ArgumentNullException.ThrowIfNull(configurationManager);

            var transcodePath = configurationManager.GetTranscodePath();
            var parent = Path.GetDirectoryName(transcodePath);
            return parent is null
                ? transcodePath
                : Path.Combine(parent, "tvheadend-livebuffers");
        }

        /// <summary>
        /// Removes buffers left behind by a previous run.
        /// </summary>
        /// <remarks>
        /// A server that stops while a stream is open never reaches a close, and each orphan
        /// keeps a recording's worth of disk space. Safe only before the first stream of a
        /// process is opened, when no buffer can belong to a live stream.
        /// </remarks>
        /// <param name="bufferDirectory">The buffer directory.</param>
        /// <param name="logger">The logger.</param>
        public static void RemoveOrphaned(string bufferDirectory, ILogger logger)
        {
            ArgumentException.ThrowIfNullOrEmpty(bufferDirectory);
            ArgumentNullException.ThrowIfNull(logger);

            try
            {
                if (!Directory.Exists(bufferDirectory))
                {
                    return;
                }

                long reclaimedBytes = 0;
                var removed = 0;
                foreach (var path in Directory.EnumerateFiles(bufferDirectory, "tvheadend-*"))
                {
                    try
                    {
                        var length = new FileInfo(path).Length;
                        File.Delete(path);
                        reclaimedBytes += length;
                        removed++;
                    }
                    catch (IOException)
                    {
                        // Still held by something; it will be swept on a later start.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Same.
                    }
                }

                if (removed > 0)
                {
                    logger.LogInformation(
                        "Removed {Count} live TV buffer(s) left behind by a previous run, reclaiming {ReclaimedMegabytes} MB",
                        removed,
                        reclaimedBytes / (1024 * 1024));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not sweep the live TV buffer directory");
            }
        }
    }
}
