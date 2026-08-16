using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace TVHeadEnd;

/// <summary>
/// Registers the services this plugin contributes to Jellyfin.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<HTSConnectionHandler>();
        serviceCollection.AddSingleton<ILiveTvService, LiveTvService>();

        // Registered under its own type as well, so the endpoint serving recordings can ask it
        // what its analysis found instead of establishing the same thing a second time. Both
        // registrations resolve to the one instance.
        serviceCollection.AddSingleton<RecordingsChannel>();
        serviceCollection.AddSingleton<IChannel>(provider => provider.GetRequiredService<RecordingsChannel>());
    }
}
