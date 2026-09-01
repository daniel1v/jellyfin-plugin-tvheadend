using System;
using System.Threading;
using MediaBrowser.Model.Plugins;

namespace TVHeadEnd.Configuration;

/// <summary>
/// The plugin's stored configuration, as Jellyfin holds it.
/// </summary>
/// <remarks>
/// The only class in the plugin that reaches for <c>Plugin.Instance</c> outside the plugin type
/// itself. The subscription is taken on the first read rather than in the constructor, because
/// this object is built by Jellyfin's container while the plugin instance is still being created:
/// there is nothing to subscribe to then, and the first read of the configuration is by definition
/// a moment when there is.
/// </remarks>
public sealed class PluginConfigurationSource : IPluginConfigurationSource, IDisposable
{
    private int _subscribed;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public PluginConfiguration Current
    {
        get
        {
            if (Interlocked.Exchange(ref _subscribed, 1) == 0)
            {
                Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
            }

            return Plugin.Instance.Configuration;
        }
    }

    /// <inheritdoc />
    public void Save() => Plugin.Instance.SaveConfiguration();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _subscribed, 2) == 1)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
        => Changed?.Invoke(this, EventArgs.Empty);
}
