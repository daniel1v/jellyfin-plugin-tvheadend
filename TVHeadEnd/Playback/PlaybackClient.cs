using System;
using Microsoft.AspNetCore.Http;

namespace TVHeadEnd.Playback;

/// <summary>
/// Who is asking for the stream, as far as the session Jellyfin already authenticated says so.
/// </summary>
/// <remarks>
/// <para>
/// The one place in the plugin that looks at the caller at all, and it looks at one thing: the
/// client name Jellyfin put in the session's claims. That is enough for the single decision that
/// depends on it, and everything else is written as if every client were correct.
/// </para>
/// <para>
/// User agents are not parsed and addresses are not inspected. Every client Jellyfin serves
/// identifies itself in its authentication header, and that is the only statement here that
/// cannot be a coincidence.
/// </para>
/// </remarks>
public sealed class PlaybackClient
{
    /// <summary>
    /// Jellyfin.Api.Constants.InternalClaimTypes.Client, which is internal to the server assembly.
    /// Named rather than referenced, because a plugin cannot see it. If the server renames it this
    /// goes quiet and every client gets the broadcast untouched, which is the safe direction.
    /// </summary>
    private const string ClientClaim = "Jellyfin-Client";

    private readonly IHttpContextAccessor? _httpContextAccessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackClient"/> class.
    /// </summary>
    /// <param name="httpContextAccessor">
    /// The accessor for the request in flight, or <see langword="null"/> where there is no web
    /// pipeline at all.
    /// </param>
    public PlaybackClient(IHttpContextAccessor? httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Gets the client name of the request being served, or <see langword="null"/> when there is
    /// no request -- a scheduled task, a channel refresh, or an internal call.
    /// </summary>
    public string? Name => _httpContextAccessor?.HttpContext?.User?.FindFirst(ClientClaim)?.Value;

    /// <summary>
    /// Gets a value indicating whether the request is being served for one of Jellyfin's Android
    /// clients.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Android is asked about because of one measured defect, not as a category: MediaCodec will
    /// not emit a frame until it has seen an IDR, so an H.264 broadcast whose access points are
    /// recovery points never starts there. It consumes samples at full rate, renders nothing, and
    /// reports no error -- the endless spinner.
    /// </para>
    /// <para>
    /// When the claim is absent or says something else the answer is no, and the caller takes the
    /// ordinary path. A client that is wrongly treated as Android gets a re-encode it did not
    /// need; one that is wrongly treated as anything else gets a channel that never starts. The
    /// asymmetry is the reason this errs towards the plain answer.
    /// </para>
    /// </remarks>
    public bool IsAndroid
    {
        get
        {
            var name = Name;
            return string.Equals(name, "Jellyfin Android", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "AndroidTV", StringComparison.OrdinalIgnoreCase);
        }
    }
}
