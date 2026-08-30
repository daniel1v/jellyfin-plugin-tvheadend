using System;

namespace TVHeadEnd.Tvheadend;

/// <summary>
/// The plugin configuration, validated once and in the form the rest of the plugin uses it.
/// </summary>
/// <remarks>
/// Validated once at the edge of the plugin rather than passed around as stored text: a setting is a
/// string somebody typed, and everything below this line should be dealing with a host name that
/// is known not to be empty and a priority that is known to be in range.
/// </remarks>
public sealed record TvheadendSettings
{
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
}
