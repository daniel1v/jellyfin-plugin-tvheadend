using System;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Reassembles PSI sections out of the transport stream packets carrying them.
/// </summary>
/// <remarks>
/// <para>
/// A section is not a packet. It may span any number of them, and a PMT that names several audio
/// tracks and a handful of subtitle pages routinely does. Reading only the packet that starts one
/// works on the simplest broadcasts and silently truncates the rest, which is how a channel comes
/// to be described with half its tracks.
/// </para>
/// <para>
/// A packet that starts a payload unit can carry two things: the tail of the section already in
/// progress, and the beginning of the next one. The pointer field says where the split is. Taking
/// the pointer as the start and discarding what precedes it loses the last bytes of the section
/// before -- for a PMT split across two packets, that is most of it.
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
    private readonly byte[] _completed = new byte[MaximumSectionLength];

    private int _collected;
    private int _expected;
    private int _completedLength;

    /// <summary>
    /// Gets the completed section, valid only while <see cref="Accept"/> has just returned
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Held apart from the section being collected, because the packet that completes one section
    /// may start the next in the same breath, and the caller has to be able to read the finished
    /// bytes after that has happened.
    /// </remarks>
    public ReadOnlySpan<byte> Section => _completed.AsSpan(0, _completedLength);

    /// <summary>
    /// Offers the payload of one packet.
    /// </summary>
    /// <remarks>
    /// At most one completed section is reported per packet. In the rare case where a packet
    /// finishes one section and carries a whole further section after it, the later one is what
    /// gets reported: two versions of the same table, of which the newer is the one in force.
    /// </remarks>
    /// <param name="packet">A whole transport stream packet on the section's PID.</param>
    /// <returns>Whether a complete section is now available in <see cref="Section"/>.</returns>
    public bool Accept(ReadOnlySpan<byte> packet)
    {
        var payload = TransportStreamPacket.ReadPayload(packet);
        if (payload.IsEmpty)
        {
            return false;
        }

        if (!TransportStreamPacket.StartsPayloadUnit(packet))
        {
            return Append(payload);
        }

        // The first byte says how many bytes of the section already in progress come before the
        // new one begins.
        var pointer = payload[0];
        payload = payload[1..];
        if (pointer > payload.Length)
        {
            Reset();
            return false;
        }

        // Finish what was in progress before starting anything new. Skipping to the pointer, as
        // the obvious reading of the field invites, throws away the end of that section.
        var completedTail = pointer > 0 && Append(payload[..pointer]);
        payload = payload[pointer..];

        Reset();
        var completedSection = StartSection(payload);

        return completedTail || completedSection;
    }

    /// <summary>
    /// Forgets a partially collected section.
    /// </summary>
    public void Reset()
    {
        _collected = 0;
        _expected = 0;
    }

    private bool StartSection(ReadOnlySpan<byte> payload)
    {
        // Stuffing: the rest of the packet after the last section is filled with 0xFF.
        if (payload.Length < 3 || payload[0] == 0xFF)
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

        return Append(payload);
    }

    private bool Append(ReadOnlySpan<byte> payload)
    {
        if (_expected == 0)
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

        if (_collected != _expected)
        {
            return false;
        }

        _section.AsSpan(0, _expected).CopyTo(_completed);
        _completedLength = _expected;
        return true;
    }
}
