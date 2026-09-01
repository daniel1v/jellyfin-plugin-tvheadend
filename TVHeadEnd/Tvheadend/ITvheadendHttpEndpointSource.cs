using System.Threading;
using System.Threading.Tasks;

namespace TVHeadEnd.Tvheadend;

/// <summary>
/// Where TVHeadend's HTTP interface is, once the server has said so itself.
/// </summary>
/// <remarks>
/// One question, asked by everything that fetches bytes from TVHeadend over HTTP. It is not the
/// synchronous property beside it: the web root is only known from a handshake, so an address
/// built before one has happened points at a path the server never reported.
/// </remarks>
public interface ITvheadendHttpEndpointSource
{
    /// <summary>
    /// Gets the HTTP endpoint, connecting first so that the server's web root is known.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The endpoint.</returns>
    Task<TvheadendHttpEndpoint> GetHttpEndpointAsync(CancellationToken cancellationToken);
}
