using System.Collections.Generic;
using System.Linq;

namespace TVHeadEnd.Streaming;

/// <summary>
/// The viewers a running live stream is being kept open for.
/// </summary>
/// <remarks>
/// <para>
/// We count logical active viewers, not playback negotiation attempts. Those are not the same
/// number: a client that fails to start and negotiates again asks Jellyfin to open the stream
/// once more, and Jellyfin reuses the running one. Counting each of those as a viewer leaves the
/// stream held open by attempts nobody is watching, because a client reports one stop, not one
/// per attempt it abandoned.
/// </para>
/// <para>
/// Jellyfin itself already draws this distinction one layer up: <c>SessionManager</c> keeps one
/// entry per session against a live stream and replaces the play session when the same session
/// negotiates again. This mirrors that, keyed the same way, so the two agree.
/// </para>
/// <para>
/// Departures arrive without a name -- Jellyfin's contract decrements a count and does not say
/// whose. <see cref="ReleaseOne"/> therefore forgets an arbitrary viewer. That cannot make the
/// count too low, which is the direction that would matter: the count only falls when Jellyfin
/// says a viewer left, and forgetting the wrong key only means a viewer who is still watching is
/// no longer recognised, so their next arrival is counted afresh rather than suppressed.
/// </para>
/// </remarks>
public sealed class LiveStreamConsumers
{
    private readonly HashSet<string> _active = new(System.StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// Gets how many viewers the stream is being held open for.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _active.Count;
            }
        }
    }

    /// <summary>
    /// Registers a viewer, if it is not already registered.
    /// </summary>
    /// <param name="consumerId">Who is watching.</param>
    /// <returns>
    /// <see langword="true"/> when this is a viewer the stream was not already being held open
    /// for, and <see langword="false"/> when the same one is negotiating again.
    /// </returns>
    public bool Acquire(string consumerId)
    {
        lock (_lock)
        {
            return _active.Add(consumerId);
        }
    }

    /// <summary>
    /// Forgets one viewer, without being told which.
    /// </summary>
    /// <returns>How many are left.</returns>
    public int ReleaseOne()
    {
        lock (_lock)
        {
            var any = _active.FirstOrDefault();
            if (any is not null)
            {
                _active.Remove(any);
            }

            return _active.Count;
        }
    }
}
