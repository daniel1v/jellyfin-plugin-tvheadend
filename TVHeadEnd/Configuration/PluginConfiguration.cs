using System;
using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace TVHeadEnd.Configuration;

/// <summary>
/// The plugin's settings.
/// </summary>
/// <remarks>
/// How to reach TVHeadend, and how it should record. Nothing about how a stream is delivered:
/// TVHeadend forwards the broadcast untouched and Jellyfin decides what each client can do with
/// it, so there is nothing left here for an administrator to get wrong.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "These property names are the element names Jellyfin persists this configuration under. Renaming them would silently reset every existing user's settings on upgrade, and would also break Web/tvheadend.js.")]
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        TVH_ServerName = "localhost";
        HTTP_Port = 9981;
        HTSP_Port = 9982;
        Username = string.Empty;
        Password = string.Empty;
        Priority = 5;
        DvrProfile = string.Empty;
        Pre_Padding = 0;
        Post_Padding = 0;
        ChannelType = "Ignore";
        HideRecordingsChannel = false;
        UseChannelLogoWhereArtworkIsMissing = true;
        LiveBufferSizeMegabytes = 512;
        RecordingAccessSecret = string.Empty;
    }

    /// <summary>
    /// Gets or sets the TVHeadend host name or address.
    /// </summary>
    public string TVH_ServerName { get; set; }

    /// <summary>
    /// Gets or sets the port TVHeadend's HTTP interface listens on, which carries the broadcast.
    /// </summary>
    public int HTTP_Port { get; set; }

    /// <summary>
    /// Gets or sets the port TVHeadend's HTSP interface listens on, which carries the control
    /// protocol, the channel list, the guide and the DVR.
    /// </summary>
    public int HTSP_Port { get; set; }

    /// <summary>
    /// Gets or sets the user name.
    /// </summary>
    /// <remarks>
    /// An ordinary streaming account is enough. The plugin reads the channel list, the guide and
    /// the DVR over HTSP and the broadcast over HTTP; nothing in the live path touches an
    /// administrative API.
    /// </remarks>
    public string Username { get; set; }

    /// <summary>
    /// Gets or sets the password.
    /// </summary>
    public string Password { get; set; }

    /// <summary>
    /// Gets or sets the recording priority: 0 important, 2 normal, 5 leaves it to the DVR
    /// configuration.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Gets or sets the TVHeadend <em>DVR configuration</em> used when creating timers and
    /// series rules.
    /// </summary>
    public string DvrProfile { get; set; }

    /// <summary>
    /// Gets or sets the value of the former <c>Profile</c> setting.
    /// </summary>
    /// <remarks>
    /// Kept solely so an existing configuration file still deserializes and its value can be
    /// moved to <see cref="DvrProfile"/> by <see cref="Migrate"/>. Nothing reads it otherwise,
    /// and it is cleared once migrated.
    /// </remarks>
    [Obsolete("Migrated to DvrProfile. Present only so existing configuration files keep their value.")]
    public string? Profile { get; set; }

    /// <summary>
    /// Gets or sets the padding before a recording, in seconds.
    /// </summary>
    public int Pre_Padding { get; set; }

    /// <summary>
    /// Gets or sets the padding after a recording, in seconds.
    /// </summary>
    public int Post_Padding { get; set; }

    /// <summary>
    /// Gets or sets how a channel whose service is tagged "other" is treated.
    /// </summary>
    public string ChannelType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the recordings channel is hidden.
    /// </summary>
    public bool HideRecordingsChannel { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a recording or guide entry with no artwork of its
    /// own borrows the logo of the channel it came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On over-the-air listings there is nothing else to show: broadcast DVB EIT has no field for
    /// a picture, so nothing that comes from it has one. The channel's logo at least says which
    /// broadcaster the programme came from, and it never displaces artwork that does exist.
    /// </para>
    /// <para>
    /// A matter of taste, so it is a setting rather than a decision made here. On by default,
    /// because the alternative on such a listing is a wall of blank tiles. A server whose EPG
    /// carries real artwork loses nothing by leaving it on and nothing by turning it off.
    /// </para>
    /// <para>
    /// Turning it off stops it being published; the recordings that already took a logo keep it
    /// until the artwork is forgotten, which is what the reset in the settings page is for.
    /// </para>
    /// </remarks>
    public bool UseChannelLogoWhereArtworkIsMissing { get; set; }

    /// <summary>
    /// Gets or sets the size of the buffer each running channel occupies on disk.
    /// </summary>
    /// <remarks>
    /// The live stream is written into it in a circle, so this is the whole cost of a channel
    /// however long it runs -- and at the same time the distance a client can fall behind the
    /// live edge, by pausing for instance, before it is moved forward. Roughly ten minutes of an
    /// HD broadcast fit into 512 MB.
    /// </remarks>
    public int LiveBufferSizeMegabytes { get; set; }

    /// <summary>
    /// Gets or sets the secret the addresses of recordings are derived from.
    /// </summary>
    /// <remarks>
    /// The endpoint that serves them has to answer without a session, because FFmpeg fetches from
    /// it, so what protects a recording is that its address cannot be guessed. Generated once, on
    /// the server, and never shown.
    /// </remarks>
    public string RecordingAccessSecret { get; set; }

    /// <summary>
    /// Moves settings that have changed meaning or location into their new form.
    /// </summary>
    /// <returns>Whether anything changed and the configuration should be saved.</returns>
    public bool Migrate()
    {
        var changed = false;

#pragma warning disable CS0618 // Migration is the only permitted use of the obsolete members.
        if (!string.IsNullOrEmpty(Profile))
        {
            if (string.IsNullOrEmpty(DvrProfile))
            {
                DvrProfile = Profile;
            }

            Profile = null;
            changed = true;
        }
#pragma warning restore CS0618

        return changed;
    }
}
