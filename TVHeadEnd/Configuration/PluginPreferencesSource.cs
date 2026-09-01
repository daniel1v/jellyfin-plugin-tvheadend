using System;

namespace TVHeadEnd.Configuration;

/// <summary>
/// Reads the plugin's behaviour settings out of the stored configuration.
/// </summary>
public sealed class PluginPreferencesSource : IPluginPreferencesSource
{
    private readonly IPluginConfigurationSource _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginPreferencesSource"/> class.
    /// </summary>
    /// <param name="configuration">The stored configuration.</param>
    public PluginPreferencesSource(IPluginConfigurationSource configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _configuration = configuration;
    }

    /// <inheritdoc />
    public PluginPreferences Current
    {
        get
        {
            var stored = _configuration.Current;
            return new PluginPreferences(
                stored.Pre_Padding,
                stored.Post_Padding,
                stored.HideRecordingsChannel,
                stored.UseChannelLogoWhereArtworkIsMissing);
        }
    }
}
