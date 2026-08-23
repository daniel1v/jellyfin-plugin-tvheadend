using System;

namespace TVHeadEnd.Streaming;

/// <summary>
/// What the opening of a source has been shown to be so far.
/// </summary>
internal enum SourceContainerVerdict
{
    /// <summary>
    /// Too little has arrived to say. The caller carries on reading.
    /// </summary>
    Undecided,

    /// <summary>
    /// Proven to be an MPEG-TS stream.
    /// </summary>
    TransportStream,

    /// <summary>
    /// Enough of the opening has arrived, and it is not an MPEG-TS stream.
    /// </summary>
    NotTransportStream,
}

/// <summary>
/// Decides what a source is from its opening bytes, however they happen to be delivered.
/// </summary>
/// <remarks>
/// <para>
/// A read is not a message. <c>ReadAsync</c> returns whatever has arrived, and over a slow or
/// distant link the first one is regularly a few hundred bytes -- fewer than the proof needs.
/// Deciding on one read therefore declares perfectly good transport streams to be something else,
/// which reaches the caller as TVHeadend having supposedly substituted a profile.
/// </para>
/// <para>
/// So the opening is accumulated until it can carry an answer, and no further. This costs one
/// small buffer for the first fraction of a second of a channel: no probe, no second
/// subscription, and nothing that reads the stream twice.
/// </para>
/// </remarks>
internal sealed class SourceContainerCheck
{
    private readonly byte[] _opening = new byte[SourceContainer.ConclusiveLength];

    private int _collected;
    private SourceContainerVerdict _verdict = SourceContainerVerdict.Undecided;

    /// <summary>
    /// Offers the next bytes read from the source.
    /// </summary>
    /// <remarks>
    /// Settles on the first read that proves the stream, and at the latest once
    /// <see cref="SourceContainer.ConclusiveLength"/> bytes have been seen. Once settled the
    /// answer never changes, and further bytes cost nothing.
    /// </remarks>
    /// <param name="chunk">The bytes just read.</param>
    /// <returns>What can be said so far.</returns>
    public SourceContainerVerdict Accept(ReadOnlySpan<byte> chunk)
    {
        if (_verdict != SourceContainerVerdict.Undecided)
        {
            return _verdict;
        }

        var wanted = Math.Min(_opening.Length - _collected, chunk.Length);
        chunk[..wanted].CopyTo(_opening.AsSpan(_collected));
        _collected += wanted;

        if (SourceContainer.IsTransportStream(_opening.AsSpan(0, _collected)))
        {
            _verdict = SourceContainerVerdict.TransportStream;
        }
        else if (_collected >= SourceContainer.ConclusiveLength)
        {
            _verdict = SourceContainerVerdict.NotTransportStream;
        }

        return _verdict;
    }
}
