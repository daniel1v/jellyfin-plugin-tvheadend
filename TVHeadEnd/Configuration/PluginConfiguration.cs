using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace TVHeadEnd.Configuration
{
    /// <summary>
    /// Class PluginConfiguration.
    /// </summary>
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
            Profile = string.Empty;
            Pre_Padding = 0;
            Post_Padding = 0;
            ChannelType = "Ignore";
            HideRecordingsChannel = false;
            EnableSubsMaudios = false;
            ForceDeinterlace = false;
            ReencodeWhenNoIdr = true;
        }

        public string TVH_ServerName { get; set; }

        public int HTTP_Port { get; set; }

        public int HTSP_Port { get; set; }

        public string Username { get; set; }

        public string Password { get; set; }

        public int Priority { get; set; }

        public string Profile { get; set; }

        public int Pre_Padding { get; set; }

        public int Post_Padding { get; set; }

        public string ChannelType { get; set; }

        public bool HideRecordingsChannel { get; set; }

        public bool EnableSubsMaudios { get; set; }

        public bool ForceDeinterlace { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the video of broadcasts that carry no
        /// H.264 IDR frames is re-encoded into the shared live buffer. Such broadcasts --
        /// the ARD network among them -- signal random access purely through recovery
        /// points, and common device decoders never emit a frame from them. Audio and all
        /// other channels are passed through untouched.
        /// </summary>
        public bool ReencodeWhenNoIdr { get; set; }

        /// <summary>
        /// Gets or sets the channels found to carry no IDR frames. Remembering them lets the
        /// encoder start immediately instead of spending the detection window on every first
        /// tune after a restart. The list maintains itself: a channel that starts sending IDR
        /// frames is removed again the next time it is watched.
        /// </summary>
        [SuppressMessage(
            "Performance",
            "CA1819:Properties should not return arrays",
            Justification = "Jellyfin serialises the plugin configuration with XmlSerializer, which does not handle read-only collections.")]
        public string[] ChannelsWithoutIdr { get; set; } = [];
    }
}
