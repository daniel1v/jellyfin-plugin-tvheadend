using System;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Reassembles a PSI section out of the transport stream packets carrying it.
/// </summary>
/// <remarks>
/// <para>
/// A section is not a packet. It may span any number of them, and a PMT that names several audio
/// tracks and a handful of subtitle pages routinely does. Reading only the packet that starts one
/// works on the simplest broadcasts and silently truncates the rest, which is how a channel comes
/// to be described with half its tracks.
/// </para>
/// <para>
/// Only the section that begins at a payload unit start is collected. A packet arriving mid
/// section with nothing started is discarded rather than guessed at, which is the normal state of
/// affairs when a stream is joined in flight.
/// </para>
/// </remarks>
internal sealed class PsiSectionAssembler
{
    /// <summary>
    /// The largest section the syntax allows: a twelve bit length field of which the top two bits
    /// are reserved, plus the three bytes ahead of it.
    /// </summary>
    private const int MaximumSectionLength = 1024;

    private readonly byte[] _section = new byte[MaximumSectionLength];

    private int _collected;
    private int _expected;

    /// <summary>
    /// Gets the completed section, valid only while <see cref="Accept"/> has just returned
    /// <see langword="true"/>.
    /// </summary>
    public ReadOnlySpan<byte> Section => _section.AsSpan(0, _expected);

    /// <summary>
    /// Offers the payload of one packet.
    /// </summary>
    /// <param name="packet">A whole transport stream packet on the section's PID.</param>
    /// <returns>Whether a complete section is now available in <see cref="Section"/>.</returns>
    public bool Accept(ReadOnlySpan<byte> packet)
    {
        var payload = TransportStreamPacket.ReadPayload(packet);
        if (payload.IsEmpty)
        {
            return false;
        }

        if (TransportStreamPacket.StartsPayloadUnit(packet))
        {
            // The first byte is a pointer to where the section starts, which is how a packet can
            // carry the tail of one section and the head of the next.
            var pointer = payload[0];
            if (pointer + 1 > payload.Length)
            {
                Reset();
                return false;
            }

            payload = payload[(pointer + 1)..];
            Reset();

            if (payload.Length < 3)
            {
                return false;
            }

            var sectionLength = ((payload[1] & 0x0F) << 8) | payload[2];
            _expected = sectionLength + 3;
            if (_expected > MaximumSectionLength)
            {
                Reset();
                return false;
            }
        }
        else if (_expected == 0)
        {
            // Mid-section with no section started: the stream was joined part way through one.
            return false;
        }

        var wanted = Math.Min(_expected - _collected, payload.Length);
        if (wanted <= 0)
        {
            return false;
        }

        payload[..wanted].CopyTo(_section.AsSpan(_collected));
        _collected += wanted;

        return _collected == _expected;
    }

    /// <summary>
    /// Forgets a partially collected section.
    /// </summary>
    public void Reset()
    {
        _collected = 0;
        _expected = 0;
    }
}
