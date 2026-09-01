using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using TVHeadEnd.Configuration;
using TVHeadEnd.LiveTv;
using TVHeadEnd.Playback;
using TVHeadEnd.Recordings;
using TVHeadEnd.Streaming;
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
        // The one door onto Jellyfin's plugin configuration. Everything that needs a setting is
        // handed it from here; nothing else reaches for the plugin singleton.
        serviceCollection.AddSingleton<IPluginConfigurationSource, PluginConfigurationSource>();
        serviceCollection.AddSingleton<IPluginPreferencesSource, PluginPreferencesSource>();
        serviceCollection.AddSingleton<ITvheadendSettingsSource, PluginTvheadendSettingsSource>();

        // One secret, created once and never rotated: Jellyfin stores the addresses derived from
        // it, and an address that stopped verifying is an item linking to nothing.
        serviceCollection.AddSingleton<Api.TvheadendAccessSecret>();
        serviceCollection.AddSingleton<Api.TvheadendArtwork>();

        // One connection, shared. Everything the plugin knows about the server arrives over it,
        // and every live subscription is multiplexed onto it.
        serviceCollection.AddSingleton<TvheadendConnection>();
        serviceCollection.AddSingleton<ITvheadendHttpEndpointSource>(
            provider => provider.GetRequiredService<TvheadendConnection>());

        // Resolved once and swept once, at startup, because both are facts about this host rather
        // than about any one stream.
        serviceCollection.AddSingleton<LiveBufferLocation>();

        // The live TV service's collaborators, each built by the container rather than by the
        // service itself. It is an adapter between two vocabularies; opening a stream, writing a
        // timer and reading the guide are three other jobs.
        serviceCollection.AddSingleton<ChannelItemIds>();
        serviceCollection.AddSingleton<PlaybackClient>();
        serviceCollection.AddSingleton<LiveStreamOpener>();
        serviceCollection.AddSingleton<TvheadendDvr>();
        serviceCollection.AddSingleton<TvheadendGuide>();

        serviceCollection.AddSingleton<LiveTvService>();
        serviceCollection.AddSingleton<ILiveTvService>(provider => provider.GetRequiredService<LiveTvService>());

        // One reading of a recording, shared. The channel describing a recording and the filter
        // deciding whether this client can play it directly both ask it, within milliseconds of
        // each other, and neither should cause a second eight megabyte fetch.
        serviceCollection.AddSingleton<TvheadendRecordings>();
        serviceCollection.AddSingleton<RecordingMediaSourceFactory>();
        serviceCollection.AddSingleton<IRecordingSampleSource, TvheadendRecordingSampleSource>();
        serviceCollection.AddSingleton<RecordingAnalysisService>();
        serviceCollection.AddSingleton<IRecordingAnalyser>(
            provider => provider.GetRequiredService<RecordingAnalysisService>());

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

        // The recordings half of the same decision, and it has to be an MVC filter rather than a
        // middleware step: the parameters it sets are action arguments, which exist only after
        // model binding has run. Options are built on first use, so a plugin registering this
        // reaches the same MvcOptions the server configured, whatever order the two ran in.
        serviceCollection.AddSingleton<RecordingPlaybackCompatibilityFilter>();
        serviceCollection.Configure<MvcOptions>(
            options => options.Filters.AddService<RecordingPlaybackCompatibilityFilter>());
    }
}
