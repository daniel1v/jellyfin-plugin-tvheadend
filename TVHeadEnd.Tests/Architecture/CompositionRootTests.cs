using System;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace TVHeadEnd.Tests.Architecture;

/// <summary>
/// That everything this plugin registers can actually be built.
/// </summary>
/// <remarks>
/// <para>
/// A plugin whose graph does not close does not fail at build time and does not fail at load time
/// either: Jellyfin resolves the live TV service and the channel lazily, so a missing registration
/// surfaces as a channel list that is empty for no stated reason, on somebody's server.
/// </para>
/// <para>
/// The container is asked to validate rather than to instantiate. Nothing here needs a real
/// Jellyfin -- the question is whether every constructor this plugin declares can be satisfied
/// from what the composition root registers, and that is answered from the call sites alone.
/// </para>
/// </remarks>
public class CompositionRootTests
{
    /// <summary>
    /// The services Jellyfin itself brings, which the plugin may take but does not register.
    /// </summary>
    private static readonly Type[] TheHostProvides =
    [
        typeof(IServerApplicationHost),
        typeof(IServerConfigurationManager),
        typeof(IConfigurationManager),
        typeof(IApplicationPaths),
        typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor),
        typeof(ILibraryManager),
        typeof(IMediaSourceManager),
        typeof(IMediaEncoder),
        typeof(IHostApplicationLifetime),
        typeof(System.Net.Http.IHttpClientFactory),
    ];

    [Fact]
    public void EveryServiceThisPluginRegistersCanBeConstructed()
    {
        using var provider = Compose();

        // Reached only by validation, which is the point: building the graph for real would need
        // a server behind every one of the host's services.
        Assert.NotNull(provider);
    }

    [Fact]
    public void TheTwoJellyfinEntryPointsStayResolvable()
    {
        // The two registrations Jellyfin looks for. Everything this plugin does for a user is
        // reached through one or the other, and neither is asked for by name anywhere else.
        var registered = new ServiceCollection();
        Register(registered);

        Assert.Contains(registered, service => service.ServiceType == typeof(ILiveTvService));
        Assert.Contains(registered, service => service.ServiceType == typeof(IChannel));
        Assert.Contains(registered, service => service.ServiceType == typeof(IStartupFilter));
    }

    [Fact]
    public void NothingThisPluginRegistersOutlivesTheServerOrIsRebuiltPerRequest()
    {
        // One connection, one set of catalogs, one analysis of a recording, one register of open
        // streams. All of it is state that belongs to the server rather than to a request, and a
        // scoped or transient registration of any of it would quietly make a second copy.
        var registered = new ServiceCollection();
        Register(registered);

        Assert.All(
            registered.Where(service => service.ServiceType.Assembly == typeof(ServiceRegistrator).Assembly
                || service.ImplementationType?.Assembly == typeof(ServiceRegistrator).Assembly),
            service => Assert.Equal(ServiceLifetime.Singleton, service.Lifetime));
    }

    private static ServiceProvider Compose()
    {
        var services = new ServiceCollection();
        Register(services);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static void Register(IServiceCollection services)
    {
        services.AddLogging();

        foreach (var provided in TheHostProvides)
        {
            // Registered as a call site and never as an instance. Validation asks whether the
            // dependency can be satisfied, not what it does.
            services.AddSingleton(provided, _ => throw new NotSupportedException(provided.Name));
        }

        new ServiceRegistrator().RegisterServices(services, null!);
    }
}
