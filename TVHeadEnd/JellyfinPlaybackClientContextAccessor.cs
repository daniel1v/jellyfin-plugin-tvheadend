using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TVHeadEnd.Playback;

namespace TVHeadEnd
{
    /// <summary>
    /// Reads who is asking out of the request Jellyfin is currently serving.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The boundary. This is the only type in the plugin that touches
    /// <see cref="HttpContext"/>, and it converts what it finds into a
    /// <see cref="PlaybackClientContext"/> immediately, so that nothing below it -- playback
    /// policy, media, TVHeadend, streaming -- ever depends on Jellyfin's web pipeline or can be
    /// tempted to look at something else about the caller.
    /// </para>
    /// <para>
    /// The values come from the claims of the session Jellyfin already authenticated. The
    /// user-agent header is deliberately ignored.
    /// </para>
    /// </remarks>
    public sealed class JellyfinPlaybackClientContextAccessor : IPlaybackClientContextAccessor
    {
        // Jellyfin.Api.Constants.InternalClaimTypes, which is internal to the server assembly.
        // Named here rather than referenced, because a plugin cannot see it; if the server ever
        // renames them the client context goes quiet and the quirk stops firing, which is the
        // safe direction -- every client would then receive the broadcast unchanged.
        private const string ClientClaim = "Jellyfin-Client";
        private const string VersionClaim = "Jellyfin-Version";
        private const string DeviceClaim = "Jellyfin-Device";
        private const string DeviceIdClaim = "Jellyfin-DeviceId";

        private readonly IHttpContextAccessor _httpContextAccessor;

        /// <summary>
        /// Initializes a new instance of the <see cref="JellyfinPlaybackClientContextAccessor"/> class.
        /// </summary>
        /// <param name="httpContextAccessor">The Jellyfin request accessor.</param>
        public JellyfinPlaybackClientContextAccessor(IHttpContextAccessor httpContextAccessor)
        {
            ArgumentNullException.ThrowIfNull(httpContextAccessor);

            _httpContextAccessor = httpContextAccessor;
        }

        /// <inheritdoc />
        public PlaybackClientContext Current
        {
            get
            {
                var user = _httpContextAccessor.HttpContext?.User;
                if (user is null)
                {
                    // A scheduled task, a channel refresh, or anything else not serving a
                    // request. Policy treats this as "assume nothing".
                    return PlaybackClientContext.None;
                }

                var client = Claim(user, ClientClaim);
                return string.IsNullOrEmpty(client)
                    ? PlaybackClientContext.None
                    : new PlaybackClientContext(
                        client,
                        Claim(user, VersionClaim),
                        Claim(user, DeviceClaim),
                        Claim(user, DeviceIdClaim));
            }
        }

        private static string? Claim(ClaimsPrincipal user, string type)
            => user.FindFirst(type)?.Value;
    }
}
