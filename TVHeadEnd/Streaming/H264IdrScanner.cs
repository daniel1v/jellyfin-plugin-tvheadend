using System;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Finds IDR pictures in an H.264 elementary stream, across whatever packet boundaries the
/// transport stream happens to put them behind.
/// </summary>
/// <remarks>
/// <para>
/// The whole of the analysis: a NAL unit begins with the start code <c>00 00 01</c>, and the byte
/// after it carries the type in its low five bits. Type five is an IDR picture -- a place a
/// decoder can begin with nothing behind it. Nothing else is read; parameter sets are not parsed
/// and no picture is decoded.
/// </para>
/// <para>
/// Only meaningful for stream type <c>0x1B</c>. The same three bytes followed by <c>0x05</c> are
/// an MPEG-2 slice start code for picture row five, which occurs constantly in a broadcast that
/// contains no NAL units at all -- once measured at 205 matches in eight megabytes of RTL. Every
/// caller therefore checks the program map's stream type first; asking this about MPEG-2 does not
/// give a wrong answer so much as an answer to a different question.
/// </para>
/// </remarks>
internal sealed class H264IdrScanner
{
    /// <summary>
    /// The three bytes before the current one, so a start code split across two packets is still
    /// seen. Initialised to something that cannot be part of one.
    /// </summary>
    private byte _first = 0xFF;
    private byte _second = 0xFF;
    private byte _third = 0xFF;

    /// <summary>
    /// Gets a value indicating whether an IDR picture has been seen since the last reset.
    /// </summary>
    public bool HasSeenIdr { get; private set; }

    /// <summary>
    /// Offers the next run of elementary stream bytes.
    /// </summary>
    /// <param name="payload">The bytes, which need not begin or end on any boundary.</param>
    /// <returns>Whether an IDR picture has been seen since the last reset.</returns>
    public bool Scan(ReadOnlySpan<byte> payload)
    {
        foreach (var current in payload)
        {
            if (_first == 0x00 && _second == 0x00 && _third == 0x01 && (current & 0x1F) == 5)
            {
                HasSeenIdr = true;
                return true;
            }

            _first = _second;
            _second = _third;
            _third = current;
        }

        return HasSeenIdr;
    }

    /// <summary>
    /// Forgets everything, for the start of a new access unit.
    /// </summary>
    public void Reset()
    {
        HasSeenIdr = false;
        _first = 0xFF;
        _second = 0xFF;
        _third = 0xFF;
    }
}
