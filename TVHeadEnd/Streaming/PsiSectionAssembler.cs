using System;
using System.Collections.Generic;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Reassembles PSI sections out of the transport stream packets carrying them.
/// </summary>
/// <remarks>
/// <para>
/// A section is not a packet, and a packet is not a section. One section may span any number of
/// packets -- a PMT naming several audio tracks and a handful of subtitle pages routinely does --
/// and one packet may carry the end of one section, a whole second, and the start of a third. The
/// pointer field says where the first boundary is; the section lengths say where the rest are.
/// </para>
/// <para>
/// Everything a packet completes is reported. Reading only as far as the first complete section,
/// which is the obvious shape, silently drops any that follow it in the same packet.
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
    private readonly List<byte[]> _collectingPackets = [];
    private readonly List<PsiSection> _completed = [];

    private int _collected;
    private int _expected;

    /// <summary>
    /// Gets the sections the last <see cref="Accept"/> completed, in the order they arrived.
    /// </summary>
    public IReadOnlyList<PsiSection> Completed => _completed;

    /// <summary>
    /// Offers the payload of one packet.
    /// </summary>
    /// <param name="packet">A whole transport stream packet on the section's PID.</param>
    /// <returns>Whether any section was completed by it.</returns>
    public bool Accept(ReadOnlySpan<byte> packet)
    {
        _completed.Clear();

        var payload = TransportStreamPacket.ReadPayload(packet);
        if (payload.IsEmpty)
        {
            return false;
        }

        var owned = packet.ToArray();

        if (!TransportStreamPacket.StartsPayloadUnit(packet))
        {
            _collectingPackets.Add(owned);
            Consume(payload, owned);
            return _completed.Count > 0;
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

        // This packet belongs to both sections when the pointer is non-zero: the end of the one in
        // progress, and the beginning of the next. Skipping to the pointer, as the obvious reading
        // of the field invites, throws away the end of the section before.
        _collectingPackets.Add(owned);
        if (pointer > 0)
        {
            Consume(payload[..pointer], owned);
        }

        payload = payload[pointer..];

        // Whatever was still in progress is abandoned here by definition: the pointer says the
        // next section starts, so anything unfinished before it never will be.
        _collected = 0;
        _expected = 0;
        _collectingPackets.Clear();
        _collectingPackets.Add(owned);

        Consume(payload, owned);

        return _completed.Count > 0;
    }

    /// <summary>
    /// Forgets a partially collected section.
    /// </summary>
    public void Reset()
    {
        _collected = 0;
        _expected = 0;
        _collectingPackets.Clear();
    }

    /// <summary>
    /// Takes as much of a payload as the sections in it account for.
    /// </summary>
    private void Consume(ReadOnlySpan<byte> payload, byte[] packet)
    {
        while (!payload.IsEmpty)
        {
            if (_expected == 0 && !StartSection(payload))
            {
                return;
            }

            var taken = Append(payload, packet);
            if (taken == 0)
            {
                return;
            }

            payload = payload[taken..];
        }
    }

    /// <summary>
    /// Reads the length of the section beginning here.
    /// </summary>
    /// <returns>Whether a section begins here at all.</returns>
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

        return true;
    }

    /// <summary>
    /// Copies what fits into the section being collected, completing it if that finishes it.
    /// </summary>
    /// <returns>How many bytes were taken.</returns>
    private int Append(ReadOnlySpan<byte> payload, byte[] packet)
    {
        if (_expected == 0)
        {
            // Mid-section with no section started: the stream was joined part way through one.
            return 0;
        }

        var wanted = Math.Min(_expected - _collected, payload.Length);
        if (wanted <= 0)
        {
            return 0;
        }

        payload[..wanted].CopyTo(_section.AsSpan(_collected));
        _collected += wanted;

        if (_collected != _expected)
        {
            return wanted;
        }

        _completed.Add(new PsiSection(_section.AsSpan(0, _expected).ToArray(), [.. _collectingPackets]));

        // Anything following in this packet is a new section, carried by this packet alone so far.
        _collected = 0;
        _expected = 0;
        _collectingPackets.Clear();
        _collectingPackets.Add(packet);

        return wanted;
    }
}
