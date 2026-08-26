using System;
using System.Collections.Generic;

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
/// Arrivals are named and departures are not. Jellyfin's contract carries the request's identity
/// into opening a stream -- <c>GetChannelStreamWithDirectStreamProvider</c> runs on the
/// authenticated request -- but closing one is <c>ILiveStream.ConsumerCount--</c>, reached from
/// four places, not all of them on a request. So a departure says how many are left and nothing
/// about who.
/// </para>
/// <para>
/// That asymmetry is modelled rather than papered over. A departure does not pick a name to
/// delete, because any of the named viewers could have been the one that left, and deleting one
/// would assert something nobody said. It reduces the total and forgets every name, leaving
/// viewers this knows exist but can no longer identify. A later arrival takes one of those places
/// back rather than adding to the count, which is what makes a client negotiating again after a
/// departure cost nothing.
/// </para>
/// </remarks>
public sealed class LiveStreamConsumers
{
    private readonly HashSet<string> _named = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// Viewers known to be here, whose identity has been forgotten to a departure.
    /// </summary>
    private int _unnamed;

    /// <summary>
    /// Gets how many viewers the stream is being held open for.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _named.Count + _unnamed;
            }
        }
    }

    /// <summary>
    /// Registers a viewer.
    /// </summary>
    /// <param name="consumerId">Who is watching.</param>
    /// <returns>
    /// <see langword="true"/> when this arrival raised the count, and <see langword="false"/>
    /// when it did not -- either the same viewer is negotiating again, or it took back a place
    /// left by a viewer whose identity a departure had forgotten.
    /// </returns>
    public bool Acquire(string consumerId)
    {
        lock (_lock)
        {
            if (!_named.Add(consumerId))
            {
                return false;
            }

            if (_unnamed > 0)
            {
                _unnamed--;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Records that one viewer has gone, without being told which.
    /// </summary>
    /// <returns>How many are left.</returns>
    public int ReleaseOne()
    {
        lock (_lock)
        {
            var remaining = _named.Count + _unnamed;
            if (remaining == 0)
            {
                return 0;
            }

            // Any of them could have been the one that left, so none of them is still known to be
            // here. What survives is the number.
            remaining--;
            _named.Clear();
            _unnamed = remaining;
            return remaining;
        }
    }
}
