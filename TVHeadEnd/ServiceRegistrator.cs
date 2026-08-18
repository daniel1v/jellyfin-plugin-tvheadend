using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Media;
using TVHeadEnd.Playback;
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
        serviceCollection.AddSingleton<HTSConnectionHandler>();

        // The one place client information enters the plugin. Registered here so that the
        // playback layer can depend on the abstraction and never on Jellyfin's request pipeline.
        serviceCollection.AddHttpContextAccessor();
        // What the plugin has observed about each channel. One instance: the live path writes it,
        // the settings page discards it, and both have to see the same thing.
        serviceCollection.AddSingleton(provider => new ChannelMediaDescriptorStore(
            provider.GetRequiredService<IApplicationPaths>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<ChannelMediaDescriptorStore>()));

        // Which TVHeadend profile has been proven to keep its role's promise, across restarts.
        serviceCollection.AddSingleton(provider => new StreamProfileValidationStore(
            provider.GetRequiredService<IApplicationPaths>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<StreamProfileValidationStore>()));

        serviceCollection.AddSingleton<ILiveTvService, LiveTvService>();

        // Registered under its own type as well, so the endpoint serving recordings can ask it
        // what its analysis found instead of establishing the same thing a second time. Both
        // registrations resolve to the one instance.
        serviceCollection.AddSingleton<RecordingsChannel>();
        serviceCollection.AddSingleton<IChannel>(provider => provider.GetRequiredService<RecordingsChannel>());
    }
}
