using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd;

/// <summary>
/// Registers the services this plugin contributes to Jellyfin.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // One connection, shared. Everything the plugin knows about the server arrives over it,
        // and every live subscription is multiplexed onto it.
        serviceCollection.AddSingleton<TvheadendConnection>();

        serviceCollection.AddSingleton<LiveTvService>();
        serviceCollection.AddSingleton<ILiveTvService>(provider => provider.GetRequiredService<LiveTvService>());

        // Registered under its own type as well, so the endpoint serving recordings can ask it
        // what its analysis found instead of establishing the same thing a second time. Both
        // registrations resolve to the one instance.
        serviceCollection.AddSingleton<RecordingsChannel>();
        serviceCollection.AddSingleton<IChannel>(provider => provider.GetRequiredService<RecordingsChannel>());
    }
}
