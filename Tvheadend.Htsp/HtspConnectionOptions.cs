using System;

namespace Tvheadend.Htsp;

/// <summary>
/// Where a TVHeadend server is and how to identify to it.
/// </summary>
public sealed class HtspConnectionOptions
{
    /// <summary>
    /// The highest HTSP version this client understands.
    /// </summary>
    /// <remarks>
    /// TVHeadend negotiates <c>min(server version, requested version)</c> and does not report the
    /// result, so this is an upper bound rather than a request: an older server settles on its
    /// own version and a newer one is held here. It may only be raised to a version whose
    /// behaviour this client actually handles, because the server withholds every field gated
    /// above the agreed number.
    /// </remarks>
    public const int SupportedProtocolVersion = 44;

    /// <summary>
    /// Gets the TVHeadend host name or address.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Gets the HTSP port.
    /// </summary>
    public int Port { get; init; } = 9982;

    /// <summary>
    /// Gets the user name, or empty for an anonymous connection.
    /// </summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the password.
    /// </summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name this client reports to the server, which TVHeadend shows in its
    /// subscription list.
    /// </summary>
    public string ClientName { get; init; } = "Tvheadend.Htsp";

    /// <summary>
    /// Gets the version this client reports. The client's own version, not the protocol's.
    /// </summary>
    public string ClientVersion { get; init; } = "1.0";

    /// <summary>
    /// Gets how long to wait for the TCP connection and the handshake.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets how long to wait for a reply before a request is abandoned.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
