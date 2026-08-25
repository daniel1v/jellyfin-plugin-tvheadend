using System;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Reads one H.264 access unit far enough to say whether it contains an IDR picture.
/// </summary>
/// <remarks>
/// <para>
/// One access unit, not one PES packet. A PES packet may carry several access units, and an IDR in
/// a later one says nothing about whether a decoder starting at the first will produce a picture --
/// so treating the next payload unit start as the end of the access unit would qualify an entry
/// point by evidence that arrives after it.
/// </para>
/// <para>
/// The boundary is found the way the specification names it, and no further. An access unit
/// delimiter (<c>nal_unit_type 9</c>) begins one wherever a broadcaster sends them. Where none is
/// sent, a coded slice whose <c>first_mb_in_slice</c> is zero begins a new picture, and that field
/// is the first Exp-Golomb value of the slice header: zero exactly when the top bit of the first
/// byte after the NAL header is set. That single bit is all this reads. It is not a decoder and
/// not a parser; it answers one question about the first access unit and stops.
/// </para>
/// <para>
/// Only meaningful for stream type <c>0x1B</c>. In MPEG-2 the same three bytes followed by
/// <c>0x05</c> are a slice start code for picture row five, which occurs constantly in a broadcast
/// containing no NAL units at all.
/// </para>
/// </remarks>
internal sealed class H264AccessUnitScanner
{
    private byte _first = 0xFF;
    private byte _second = 0xFF;
    private byte _third = 0xFF;

    private bool _awaitingSliceHeader;
    private bool _pendingIdr;
    private bool _seenVideoSlice;

    /// <summary>
    /// Gets a value indicating whether the first access unit has been seen to its end.
    /// </summary>
    public bool Completed { get; private set; }

    /// <summary>
    /// Gets a value indicating whether an IDR picture was found in it.
    /// </summary>
    /// <remarks>
    /// Meaningful once <see cref="Completed"/> is set. Before that it is what has been seen so far,
    /// which is enough to stop early when the answer is already yes.
    /// </remarks>
    public bool CarriesIdr { get; private set; }

    /// <summary>
    /// Offers the next run of elementary stream bytes.
    /// </summary>
    /// <param name="payload">The bytes, which need not begin or end on any boundary.</param>
    public void Scan(ReadOnlySpan<byte> payload)
    {
        if (Completed)
        {
            return;
        }

        foreach (var current in payload)
        {
            if (_awaitingSliceHeader)
            {
                _awaitingSliceHeader = false;

                // first_mb_in_slice is the first Exp-Golomb value of the slice header, and it is
                // zero exactly when this bit is set. A slice that starts at macroblock zero starts
                // a picture; one that does not is a continuation of the picture already open.
                if ((current & 0x80) != 0 && _seenVideoSlice)
                {
                    // A new picture begins here, so the access unit being read has ended --
                    // and this slice, IDR or not, belongs to the next one.
                    Completed = true;
                    return;
                }

                // Committed only now that this slice is known to belong to the access unit being
                // read. Crediting it at the NAL header would let a picture that follows the one
                // being judged qualify the entry point in front of it.
                CarriesIdr |= _pendingIdr;
                _pendingIdr = false;
                _seenVideoSlice = true;
                Carry(current);
                continue;
            }

            // The three bytes carried are the start code, so this byte is the NAL header.
            if (_first == 0x00 && _second == 0x00 && _third == 0x01)
            {
                ReadNalHeader(current);
                if (Completed)
                {
                    return;
                }
            }

            Carry(current);
        }
    }

    /// <summary>
    /// Forgets everything, for the start of another access unit.
    /// </summary>
    public void Reset()
    {
        _first = 0xFF;
        _second = 0xFF;
        _third = 0xFF;
        _awaitingSliceHeader = false;
        _pendingIdr = false;
        _seenVideoSlice = false;
        Completed = false;
        CarriesIdr = false;
    }

    private void ReadNalHeader(byte header)
    {
        var type = header & 0x1F;

        switch (type)
        {
            case 9:
                // An access unit delimiter. The first one opens this access unit; a second one
                // ends it.
                if (_seenVideoSlice)
                {
                    Completed = true;
                }

                break;

            case 5:
                _pendingIdr = true;
                _awaitingSliceHeader = true;
                break;

            case 1:
                _awaitingSliceHeader = true;
                break;

            default:
                break;
        }
    }

    private void Carry(byte current)
    {
        _first = _second;
        _second = _third;
        _third = current;
    }
}
