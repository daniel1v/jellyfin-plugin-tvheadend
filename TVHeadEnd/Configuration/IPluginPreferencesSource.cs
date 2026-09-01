namespace TVHeadEnd.Configuration;

/// <summary>
/// Where the plugin's own behaviour settings are read.
/// </summary>
/// <remarks>
/// A snapshot per read rather than a cached one: these are read at the moment they matter -- when
/// a timer is created, when a listing is built -- and a viewer who changes one expects the next
/// thing they do to use it.
/// </remarks>
public interface IPluginPreferencesSource
{
    /// <summary>
    /// Gets the preferences as they stand.
    /// </summary>
    PluginPreferences Current { get; }
}
