using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TVHeadEnd.Playback;
using TVHeadEnd.Recordings;
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

        // One reading of a recording, shared. The channel describing a recording and the filter
        // deciding whether this client can play it directly both ask it, within milliseconds of
        // each other, and neither should cause a second eight megabyte fetch.
        serviceCollection.AddSingleton<RecordingAnalysisService>();

        // Registered under its own type as well, so the endpoint serving recordings can ask it
        // what its analysis found instead of establishing the same thing a second time. Both
        // registrations resolve to the one instance.
        serviceCollection.AddSingleton<RecordingsChannel>();
        serviceCollection.AddSingleton<IChannel>(provider => provider.GetRequiredService<RecordingsChannel>());

        // One step added to the request pipeline, through the framework's own extension point.
        // It exists for a single measured case: a live stream opened for a decoder that will not
        // start without an IDR picture has to be re-encoded rather than copied, and the request
        // parameter that says so is the only place that can be stated.
        // The one place a request naming only a media source can learn which live stream that
        // is. Shared by the service that opens streams and the middleware that answers for them,
        // and holding none of them alive.
        serviceCollection.AddSingleton<OpenLiveStreams>();

        serviceCollection.AddSingleton<IStartupFilter, LivePlaybackStartupFilter>();
    }
}
