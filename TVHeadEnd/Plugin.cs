using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using TVHeadEnd.Configuration;

namespace TVHeadEnd
{
    /// <summary>
    /// Class Plugin.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private static readonly Guid _pluginId = new Guid("e55d13e1-3874-40a5-ac05-1569c06767bc");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;

            // The stored configuration may predate the current field names. Migrating it here is
            // what makes the migration part of the product rather than of its tests: it ran
            // nowhere else, so a server upgraded from an older plugin kept reading the old fields
            // and finding them empty.
            if (Configuration.Migrate())
            {
                SaveConfiguration();
            }
        }

        /// <summary>
        /// Gets the instance.
        /// </summary>
        /// <value>The instance.</value>
        public static Plugin Instance { get; private set; } = null!;

        /// <summary>
        /// Gets the name of the plugin.
        /// </summary>
        /// <value>The name.</value>
        public override string Name => "TVHeadend EX";

        /// <summary>
        /// Gets the description.
        /// </summary>
        /// <value>The description.</value>
        public override string Description => "Provides live TV and recordings using TVHeadend as the source. An independent, unofficial plugin, not the Jellyfin project's TVHeadend plugin.";

        /// <inheritdoc />
        public override Guid Id => _pluginId;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "tvheadend",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.tvheadend.html",
                },
                new PluginPageInfo
                {
                    Name = "tvheadendjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.tvheadend.js"
                }
            };
        }
    }
}
