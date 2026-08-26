using System;
using Tvheadend.Htsp.Protocol;

namespace Tvheadend.Htsp.Model;

/// <summary>
/// What TVHeadend says about itself in answer to <c>hello</c>.
/// </summary>
public sealed record HtspHello
{
    /// <summary>
    /// Gets the highest HTSP version the server supports. This is the server's own maximum, not
    /// the agreed version; the connection reports that separately.
    /// </summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>
    /// Gets the server's name.
    /// </summary>
    public string? ServerName { get; init; }

    /// <summary>
    /// Gets the server's own version string.
    /// </summary>
    public string? ServerVersion { get; init; }

    /// <summary>
    /// Gets the path prefix the server's HTTP interface is served under, or
    /// <see langword="null"/> when it is served from the root.
    /// </summary>
    /// <remarks>
    /// TVHeadend only sends this when it is actually configured behind a prefix, and it is the
    /// only party that knows, so this is the sole source for it. Every HTTP address built for
    /// this server has to be built on top of it.
    /// </remarks>
    public string? WebRoot { get; init; }

    /// <summary>
    /// Gets the per-connection salt the authentication digest is taken over.
    /// </summary>
    /// <remarks>
    /// Held as the array the protocol delivered rather than copied on every read: it is consumed
    /// once, by the handshake, and it is a wire payload rather than a value anybody mutates.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1819:Properties should not return arrays",
        Justification = "A protocol payload consumed once by the handshake. Copying it per access would be pure waste.")]
    public byte[]? Challenge { get; init; }

    /// <summary>
    /// Reads a hello response.
    /// </summary>
    /// <param name="message">The response.</param>
    /// <returns>The parsed response.</returns>
    public static HtspHello From(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new HtspHello
        {
            ProtocolVersion = message.GetInt32("htspversion") ?? 0,
            ServerName = message.GetString("servername"),
            ServerVersion = message.GetString("serverversion"),
            WebRoot = message.GetString("webroot"),
            Challenge = message.GetBinary("challenge"),
        };
    }
}
