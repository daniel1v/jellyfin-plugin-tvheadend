using System;
using TVHeadEnd.Configuration;

namespace TVHeadEnd.Tvheadend;

/// <summary>
/// The plugin configuration, validated once and in the form the rest of the plugin uses it.
/// </summary>
/// <remarks>
/// Read from <see cref="PluginConfiguration"/> rather than passed around as it: a setting is a
/// string somebody typed, and everything below this line should be dealing with a host name that
/// is known not to be empty and a priority that is known to be in range.
/// </remarks>
public sealed record TvheadendSettings
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

    /// <summary>
    /// Gets the TVHeadend host.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Gets the HTTP port, which carries the media.
    /// </summary>
    public required int HttpPort { get; init; }

    /// <summary>
    /// Gets the HTSP port, which carries the control and metadata protocol.
    /// </summary>
    public required int HtspPort { get; init; }

    /// <summary>
    /// Gets the user name.
    /// </summary>
    public required string UserName { get; init; }

    /// <summary>
    /// Gets the password.
    /// </summary>
    public required string Password { get; init; }

    /// <summary>
    /// Gets the recording priority.
    /// </summary>
    public required int Priority { get; init; }

    /// <summary>
    /// Gets the TVHeadend DVR configuration new timers are created under.
    /// </summary>
    public required string DvrProfile { get; init; }

    /// <summary>
    /// Gets how to treat a channel whose service is tagged "other".
    /// </summary>
    public required string ChannelTypeForOther { get; init; }

    /// <summary>
    /// Gets the size of each running channel's ring buffer, in megabytes.
    /// </summary>
    public required int LiveBufferSizeMegabytes { get; init; }

    /// <summary>
    /// Reads and validates the stored configuration.
    /// </summary>
    /// <param name="configuration">The stored configuration.</param>
    /// <returns>The validated settings.</returns>
    /// <exception cref="InvalidOperationException">A required setting is missing.</exception>
    public static TvheadendSettings From(PluginConfiguration configuration)
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
            Password = configuration.Password.Trim(),
            Priority = priority,
            DvrProfile = configuration.DvrProfile.Trim(),
            ChannelTypeForOther = configuration.ChannelType.Trim(),
            LiveBufferSizeMegabytes = configuration.LiveBufferSizeMegabytes,
        };
    }
}
