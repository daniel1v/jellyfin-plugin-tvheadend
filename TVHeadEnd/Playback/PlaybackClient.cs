using System;
using System.Security.Claims;
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

    /// <summary>
    /// Jellyfin.Api.Constants.InternalClaimTypes.DeviceId, named for the same reason.
    /// </summary>
    private const string DeviceIdClaim = "Jellyfin-DeviceId";

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
    /// Gets a value indicating whether this client's decoder will only start on an IDR picture.
    /// </summary>
    /// <remarks>
    /// The one client quirk this plugin knows, stated once. See
    /// <see cref="NeedsIdrEntryPointFor(ClaimsPrincipal?)"/> for what it means and why it is asked
    /// of the session rather than of the request.
    /// </remarks>
    public bool NeedsIdrEntryPoint => NeedsIdrEntryPointFor(_httpContextAccessor?.HttpContext?.User);

    /// <summary>
    /// Gets the device identifier of the request being served, or <see langword="null"/> when
    /// there is no request -- a scheduled task, a channel refresh, or an internal call.
    /// </summary>
    /// <remarks>
    /// The same value a client sends back as <c>deviceId</c> on the streaming endpoints, which is
    /// what lets a stream opened here be found again by the request that plays it. Jellyfin put it
    /// in the session's claims; it is not read from a header the client could spell differently.
    /// </remarks>
    public string? DeviceId => _httpContextAccessor?.HttpContext?.User?.FindFirst(DeviceIdClaim)?.Value;

    /// <summary>
    /// Whether the client an already authenticated session belongs to will only start on an IDR
    /// picture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Android is asked about because of one measured defect, not as a category: MediaCodec will
    /// not emit a frame until it has seen an IDR, so an H.264 broadcast whose access points are
    /// recovery points never starts there. It consumes samples at full rate, renders nothing, and
    /// reports no error -- the endless spinner.
    /// </para>
    /// <para>
    /// Matched on the word rather than on a list of exact names, because the family spells itself
    /// several ways and an exact list gets one of them wrong silently. The mobile app reports
    /// "Jellyfin for Android" -- checking for "Jellyfin Android", which is what a list here first
    /// contained, meant the real app was never recognised and the channels this exists for went
    /// out untouched and never started. This is still the name the session authenticated with,
    /// not a user agent: the client chose it and Jellyfin recorded it.
    /// </para>
    /// <para>
    /// Static and pure so that a request filter, which is handed the principal and nothing else,
    /// asks exactly the same question the streaming path asks. When the claim is absent or names
    /// something else the answer is no and the caller takes the ordinary path, which is the safe
    /// direction: the ordinary path is the one that delivers the broadcast untouched.
    /// </para>
    /// </remarks>
    /// <param name="user">The claims of the session Jellyfin authenticated, if there is one.</param>
    /// <returns>Whether this client needs an IDR picture to start.</returns>
    public static bool NeedsIdrEntryPointFor(ClaimsPrincipal? user)
        => user?.FindFirst(ClientClaim)?.Value?.Contains("android", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Identifies the viewer the request is being served for, for as long as a live stream needs
    /// to be kept open for them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client name and the device identifier, which is how the session manager of the server
    /// keys a session of its own: <c>GetSessionKey(appName, deviceId)</c>. Using the same two
    /// claims means a stream is held open for exactly the viewers Jellyfin considers to be
    /// watching it, and that a client which negotiates playback several times over -- as one does
    /// when its first attempt fails -- is recognised as the one viewer it is.
    /// </para>
    /// <para>
    /// Where there is no request there is no viewer to recognise, and a fresh identity is
    /// returned so that two such callers are never mistaken for one.
    /// </para>
    /// </remarks>
    /// <returns>An identity stable for as long as the viewer is watching.</returns>
    public string ResolveConsumerId()
    {
        var device = DeviceId;

        return string.IsNullOrEmpty(device)
            ? Guid.NewGuid().ToString("N")
            : Name + device;
    }
}
