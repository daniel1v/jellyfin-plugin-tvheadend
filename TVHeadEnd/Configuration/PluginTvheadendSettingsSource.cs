using System;
using System.Threading;
using MediaBrowser.Model.Plugins;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.Configuration;

/// <summary>
/// Turns what somebody typed on the settings page into settings the TVHeadend side can work from.
/// </summary>
/// <remarks>
/// <para>
/// The one place where Jellyfin's plugin configuration and the TVHeadend adapter meet, and it is
/// deliberately a place rather than a habit: everything below it deals with a host name that is
/// known not to be empty and a priority that is known to be in range, and nothing below it knows
/// there is a settings page at all.
/// </para>
/// <para>
/// The subscription is taken on the first read rather than in the constructor. This object is
/// built by Jellyfin's container while the plugin instance is still being created, so there is
/// nothing to subscribe to then -- and the first read of the configuration is by definition a
/// moment when there is.
/// </para>
/// </remarks>
public sealed class PluginTvheadendSettingsSource : ITvheadendSettingsSource, IDisposable
{
    /// <summary>
    /// DVR_PRIO_IMPORTANT, the lowest value TVHeadend accepts for a recording priority.
    /// </summary>
    private const int PriorityImportant = 0;

    /// <summary>
    /// DVR_PRIO_NORMAL, the fallback for a priority outside the range.
    /// </summary>
    private const int PriorityNormal = 2;

    /// <summary>
    /// DVR_PRIO_NOTSET, which leaves the priority to the DVR configuration.
    /// </summary>
    private const int PriorityNotSet = 5;

    private int _subscribed;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public TvheadendSettings Current
    {
        get
        {
            if (Interlocked.Exchange(ref _subscribed, 1) == 0)
            {
                Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
            }

            return Validate(Plugin.Instance.Configuration);
        }
    }

    /// <summary>
    /// Reads and validates a stored configuration.
    /// </summary>
    /// <param name="configuration">The stored configuration.</param>
    /// <returns>The validated settings.</returns>
    /// <exception cref="InvalidOperationException">A required setting is missing.</exception>
    public static TvheadendSettings Validate(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(configuration.TVH_ServerName))
        {
            throw new InvalidOperationException("The TVHeadend server name has to be configured before the plugin can be used.");
        }

        var priority = configuration.Priority;
        if (priority is < PriorityImportant or > PriorityNotSet)
        {
            priority = PriorityNormal;
        }

        return new TvheadendSettings
        {
            Host = configuration.TVH_ServerName.Trim(),
            HttpPort = configuration.HTTP_Port,
            HtspPort = configuration.HTSP_Port,
            UserName = configuration.Username.Trim(),

            // Not trimmed. A host name with a stray space is a typo; a password with one is a
            // password, and silently changing it turns a working credential into a failing login
            // nobody can explain.
            Password = configuration.Password,

            Priority = priority,
            DvrProfile = configuration.DvrProfile.Trim(),
            ChannelTypeForOther = configuration.ChannelType.Trim(),
            LiveBufferSizeMegabytes = configuration.LiveBufferSizeMegabytes,
        };
    }

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
