using System;

namespace TVHeadEnd.Configuration;

/// <summary>
/// Where the plugin's stored configuration is read, written and watched.
/// </summary>
/// <remarks>
/// <para>
/// The one door onto Jellyfin's plugin singleton. Everything else asks for what it needs and is
/// handed it; nothing else reaches for a static. A global is a dependency nobody declared, and the
/// cost of leaving it scattered was that no class touching configuration could be built in a test
/// without a whole plugin instance behind it.
/// </para>
/// <para>
/// Implementations must not read the plugin during construction. Jellyfin's container builds
/// services while the plugin instance is still being created, so there is nothing there yet -- the
/// first read is by definition a moment when there is.
/// </para>
/// </remarks>
public interface IPluginConfigurationSource
{
    /// <summary>
    /// Occurs after the configuration has been changed and saved.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Gets the configuration as it stands.
    /// </summary>
    PluginConfiguration Current { get; }

    /// <summary>
    /// Persists whatever has been written to <see cref="Current"/>.
    /// </summary>
    void Save();
}
