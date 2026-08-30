using System;

namespace TVHeadEnd.Tvheadend;

/// <summary>
/// Where the TVHeadend side gets the settings it works from, and how it learns they changed.
/// </summary>
/// <remarks>
/// <para>
/// One capability, and it is the only thing this side of the plugin needs to know about
/// configuration: which server to talk to, and that somebody has changed the answer. Where those
/// settings are stored, what the settings page calls them, and that any of it belongs to a
/// Jellyfin plugin at all are questions with an answer on the other side of this line.
/// </para>
/// <para>
/// It exists because the connection used to read a static plugin singleton and subscribe to its
/// events. That made the whole TVHeadend adapter untestable without a Jellyfin plugin instance,
/// and it made the adapter's dependency on the host invisible -- a global is a dependency nobody
/// declared.
/// </para>
/// </remarks>
public interface ITvheadendSettingsSource
{
    /// <summary>
    /// Occurs when the settings have changed and should be read again.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Gets the settings as they stand, validated.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The plugin has not been configured far enough to reach a server.
    /// </exception>
    TvheadendSettings Current { get; }
}
