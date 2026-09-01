using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace TVHeadEnd.Compatibility.Jellyfin12;

/// <summary>
/// Puts <see cref="LivePlaybackRequestMiddleware"/> into Jellyfin's request pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The ordinary ASP.NET Core extension point, registered from the plugin's service registrator
/// like any other service. Nothing of Jellyfin's is replaced, decorated, patched or reflected
/// over; the pipeline simply gains one more step.
/// </para>
/// <para>
/// A startup filter runs its middleware ahead of the application's own, which is to say before
/// routing and before authentication. That is sound here because the middleware makes no decision
/// about a user: it resolves a live stream Jellyfin has already opened and reads a property that
/// was settled when it was opened.
/// </para>
/// </remarks>
public sealed class LivePlaybackStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return builder =>
        {
            builder.UseMiddleware<LivePlaybackRequestMiddleware>();
            next(builder);
        };
    }
}
