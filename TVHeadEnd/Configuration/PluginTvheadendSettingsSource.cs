using System;
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
/// Nothing here reaches for the plugin: it is handed the configuration through
/// <see cref="IPluginConfigurationSource"/>, which is the one door onto it and the one place that
/// has to know a plugin instance may not exist yet.
/// </para>
/// </remarks>
public sealed class PluginTvheadendSettingsSource : ITvheadendSettingsSource
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

    private readonly IPluginConfigurationSource _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginTvheadendSettingsSource"/> class.
    /// </summary>
    /// <param name="configuration">The stored plugin configuration.</param>
    public PluginTvheadendSettingsSource(IPluginConfigurationSource configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _configuration = configuration;
        _configuration.Changed += (sender, arguments) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public TvheadendSettings Current => Validate(_configuration.Current);

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
}
