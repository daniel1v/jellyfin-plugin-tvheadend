using System;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Configuration;

namespace TVHeadEnd.Api
{
    /// <summary>
    /// The secret this plugin's unguessable addresses are derived from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One secret for every address the plugin publishes, created the first time one is needed and
    /// kept from then on. It has to survive a restart, because Jellyfin stores the addresses it
    /// produced -- on a recording's media source and on a channel's image path -- and an address
    /// that stopped verifying would leave those items with a link to nothing. It is therefore
    /// never rotated on its own.
    /// </para>
    /// <para>
    /// Stored under the name it was first given, <c>RecordingAccessSecret</c>, although recordings
    /// are no longer the only thing it names. Renaming the setting would orphan the secret on every
    /// server that already has one, which is the one thing this must not do.
    /// </para>
    /// </remarks>
    public sealed class TvheadendAccessSecret
    {
        private readonly IPluginConfigurationSource _configuration;
        private readonly ILogger<TvheadendAccessSecret> _logger;
        private readonly object _lock = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="TvheadendAccessSecret"/> class.
        /// </summary>
        /// <param name="configuration">Where the secret is stored.</param>
        /// <param name="logger">The logger, for the one time it is created.</param>
        public TvheadendAccessSecret(IPluginConfigurationSource configuration, ILogger<TvheadendAccessSecret> logger)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(logger);

            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Gets the secret, creating it on first use.
        /// </summary>
        /// <returns>The secret.</returns>
        public string Ensure()
        {
            var configuration = _configuration.Current;
            if (!string.IsNullOrEmpty(configuration.RecordingAccessSecret))
            {
                return configuration.RecordingAccessSecret;
            }

            lock (_lock)
            {
                // Read again inside the lock: two callers can arrive together, and the second must
                // return the secret the first stored rather than replace it.
                configuration = _configuration.Current;
                if (string.IsNullOrEmpty(configuration.RecordingAccessSecret))
                {
                    configuration.RecordingAccessSecret = TvheadendAccessToken.CreateSecret();
                    _configuration.Save();
                    _logger.LogInformation("TVHeadend: created the secret this plugin's addresses are derived from");
                }

                return configuration.RecordingAccessSecret;
            }
        }
    }
}
