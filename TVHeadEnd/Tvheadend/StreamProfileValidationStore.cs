using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Tvheadend
{
    /// <summary>
    /// Remembers which TVHeadend profile was proven to satisfy which role.
    /// </summary>
    /// <remarks>
    /// A role counts as usable for compatibility work only once an opened stream of it was
    /// observed to keep the role's promise. Without this the proof would be forgotten on every
    /// restart, and the transitional plugin-side encoder -- which stands down only for a proven
    /// profile -- would come back each time the server was restarted.
    /// </remarks>
    public sealed class StreamProfileValidationStore
    {
        private readonly string _path;
        private readonly ILogger _logger;
        private readonly object _gate = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamProfileValidationStore"/> class.
        /// </summary>
        /// <param name="applicationPaths">The Jellyfin application paths.</param>
        /// <param name="logger">The logger.</param>
        public StreamProfileValidationStore(IApplicationPaths applicationPaths, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(applicationPaths);
            ArgumentNullException.ThrowIfNull(logger);

            _logger = logger;
            _path = Path.Combine(applicationPaths.DataPath, "tvheadend", "stream-profile-validation.json");
        }

        /// <summary>
        /// Reads the profile name proven for each role.
        /// </summary>
        /// <returns>Role to profile name, empty when nothing has been proven.</returns>
        public Dictionary<string, string> Load()
        {
            lock (_gate)
            {
                try
                {
                    if (!File.Exists(_path))
                    {
                        return [];
                    }

                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [];
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(exception, "Could not read which TVHeadend stream profiles have been validated");
                    return [];
                }
            }
        }

        /// <summary>
        /// Records the outcome of validating a role, so it survives a restart.
        /// </summary>
        /// <param name="role">The role.</param>
        /// <param name="profileName">The profile that was used.</param>
        /// <param name="satisfiesContract">Whether it kept the role's promise.</param>
        public void Record(StreamProfileRole role, string? profileName, bool satisfiesContract)
        {
            lock (_gate)
            {
                var proven = Load();
                var key = role.ToString();

                if (satisfiesContract && !string.IsNullOrEmpty(profileName))
                {
                    proven[key] = profileName;
                }
                else if (!proven.Remove(key))
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    var temporary = _path + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(proven));
                    File.Move(temporary, _path, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(exception, "Could not record which TVHeadend stream profiles have been validated");
                }
            }
        }
    }
}
