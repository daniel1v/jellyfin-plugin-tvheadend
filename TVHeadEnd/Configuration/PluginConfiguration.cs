using System;
using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.Configuration
{
    /// <summary>
    /// The plugin's settings.
    /// </summary>
    /// <remarks>
    /// Settings only. What the plugin has observed about a channel is not configuration and
    /// lives in the descriptor store in the plugin's data directory, so that editing a setting
    /// never discards an analysis and exporting a configuration never carries a snapshot of
    /// somebody's transponder with it.
    /// </remarks>
    [SuppressMessage(
        "Naming",
        "CA1707:Identifiers should not contain underscores",
        Justification = "These property names are the element names Jellyfin persists this configuration under. Renaming them would silently reset every existing user's settings on upgrade, and would also break Web/tvheadend.js.")]
    public class PluginConfiguration : BasePluginConfiguration
    {
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
            EnableSubsMaudios = false;
            NativeStreamProfile = TvheadendStreamProfiles.DefaultNativeProfile;
            Mpeg2H264CompatibilityProfile = string.Empty;
            H264IdrNormalizationProfile = string.Empty;
            AnalyzeChannelFormatsOnRefresh = false;
            EnableLegacyH264Fallback = true;
            LiveBufferSizeMegabytes = 512;
            RecordingAccessSecret = string.Empty;
        }

        public string TVH_ServerName { get; set; }

        public int HTTP_Port { get; set; }

        public int HTSP_Port { get; set; }

        public string Username { get; set; }

        public string Password { get; set; }

        public int Priority { get; set; }

        /// <summary>
        /// Gets or sets the TVHeadend <em>DVR configuration</em> used when creating timers and
        /// autorec rules.
        /// </summary>
        /// <remarks>
        /// This is what the setting has always meant. It was called <c>Profile</c>, which read
        /// like a stream profile and is not one; <see cref="Profile"/> still exists only to carry
        /// the stored value over once.
        /// </remarks>
        public string DvrProfile { get; set; }

        /// <summary>
        /// Gets or sets the value of the former <c>Profile</c> setting.
        /// </summary>
        /// <remarks>
        /// Kept solely so an existing configuration file still deserializes and its value can be
        /// moved to <see cref="DvrProfile"/> by <see cref="Migrate"/>. Nothing reads it
        /// otherwise, and it is cleared once migrated.
        /// </remarks>
        [Obsolete("Migrated to DvrProfile. Present only so existing configuration files keep their value.")]
        public string? Profile { get; set; }

        public int Pre_Padding { get; set; }

        public int Post_Padding { get; set; }

        public string ChannelType { get; set; }

        public bool HideRecordingsChannel { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether live TV subtitles and multiple audio tracks
        /// are offered.
        /// </summary>
        /// <remarks>
        /// A capability question only. It used to decide the HTTP authentication method as well,
        /// which put the TVHeadend password into stream URLs; authentication is now always a
        /// header and is unrelated to this setting.
        /// </remarks>
        public bool EnableSubsMaudios { get; set; }

        /// <summary>
        /// Gets or sets the TVHeadend stream profile the broadcast is received through.
        /// Defaults to <c>pass</c>, which forwards it untouched.
        /// </summary>
        /// <remarks>
        /// Changing this invalidates every stored channel description, because a different
        /// profile can change the container and the elementary streams.
        /// </remarks>
        public string NativeStreamProfile { get; set; }

        /// <summary>
        /// Gets or sets the TVHeadend stream profile that renders MPEG-2 broadcasts as H.264, or
        /// empty to offer no such variant.
        /// </summary>
        public string Mpeg2H264CompatibilityProfile { get; set; }

        /// <summary>
        /// Gets or sets the TVHeadend stream profile that re-encodes H.264 with genuine IDR
        /// access points, or empty to offer no such variant.
        /// </summary>
        public string H264IdrNormalizationProfile { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether channels with no current description are
        /// analysed while the channel list is refreshed, rather than on first playback.
        /// </summary>
        public bool AnalyzeChannelFormatsOnRefresh { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the plugin's own H.264 encoder may stand in
        /// while no <see cref="H264IdrNormalizationProfile"/> is configured and validated.
        /// </summary>
        /// <remarks>
        /// Transitional. It exists because broadcasts that signal random access without IDR
        /// frames do not cold-start on some device decoders, and TVHeadend cannot be asked to fix
        /// that until a profile has been set up for it. Once one has, this can be switched off
        /// and the encoder removed.
        /// </remarks>
        public bool EnableLegacyH264Fallback { get; set; }

        /// <summary>
        /// Gets or sets the size of the buffer each running channel occupies on disk. The live
        /// stream is written into it in a circle, so this is the whole cost of a channel however
        /// long it runs -- and at the same time the distance a client can be behind the live edge,
        /// by pausing for instance, before it is moved forward. Roughly ten minutes of an HD
        /// broadcast fit into 512 MB.
        /// </summary>
        public int LiveBufferSizeMegabytes { get; set; }

        /// <summary>
        /// Gets or sets the secret the addresses of recordings are derived from. The endpoint that
        /// serves them has to answer without a session, because FFmpeg fetches from it, so what
        /// protects a recording is that its address cannot be guessed. Generated once, on the
        /// server, and never shown.
        /// </summary>
        public string RecordingAccessSecret { get; set; }

        /// <summary>
        /// Gets or sets the channels an earlier version found to carry no IDR frames.
        /// </summary>
        /// <remarks>
        /// Retired. Learned state does not belong in the configuration, and this is now kept in
        /// the descriptor store; the list is read once to seed it and then cleared.
        /// </remarks>
        [Obsolete("Migrated to the channel media descriptor store.")]
        [SuppressMessage(
            "Performance",
            "CA1819:Properties should not return arrays",
            Justification = "Jellyfin serialises the plugin configuration with XmlSerializer, which does not handle read-only collections.")]
        public string[]? ChannelsWithoutIdr { get; set; }

        /// <summary>
        /// Moves settings that have changed meaning or location into their new form.
        /// </summary>
        /// <remarks>
        /// Deliberate rather than silent: the old <c>Profile</c> named a DVR configuration, and
        /// leaving it under a name that reads like a stream profile would have made the new
        /// stream profile settings ambiguous the moment they were added.
        /// </remarks>
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

            if (string.IsNullOrWhiteSpace(NativeStreamProfile))
            {
                NativeStreamProfile = TvheadendStreamProfiles.DefaultNativeProfile;
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Returns the channels an earlier version recorded as carrying no IDR frames, and clears
        /// the list so it is only ever taken over once.
        /// </summary>
        /// <returns>The channel identifiers, empty when there are none left to take over.</returns>
        public string[] TakeChannelsWithoutIdr()
        {
#pragma warning disable CS0618 // Migration is the only permitted use of the obsolete members.
            var channels = ChannelsWithoutIdr;
            if (channels is null || channels.Length == 0)
            {
                return [];
            }

            ChannelsWithoutIdr = null;
            return channels;
#pragma warning restore CS0618
        }
    }
}
