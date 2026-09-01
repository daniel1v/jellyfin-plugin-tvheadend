using System;

namespace TVHeadEnd.Core.Media;

/// <summary>
/// Whether some bytes are an MPEG transport stream.
/// </summary>
/// <remarks>
/// Neither a live stream nor a recording is guaranteed to be one. The container of a live stream
/// follows the streaming profile of the TVHeadend access entry and that of a recording follows the
/// DVR profile; a server configured for one of the WebTV profiles serves Matroska instead. Both
/// settings live on the TVHeadend server, out of this plugin's reach, so the format is established
/// from the bytes rather than assumed.
/// </remarks>
public static class TransportStreamDetector
{
    private const int TransportStreamPacketLength = 188;
    private const byte SyncByte = 0x47;

    /// <summary>
    /// The number of consecutive packet boundaries that must carry a sync byte before a
    /// prefix counts as a transport stream. One sync byte proves nothing -- 0x47 is the
    /// letter 'G' and turns up in any binary -- but a run of them at exactly 188 bytes
    /// apart does not happen by chance.
    /// </summary>
    private const int RequiredConsecutiveSyncBytes = 4;

    /// <summary>
    /// How many opening bytes are needed before a negative answer means anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The proof needs four sync bytes a packet apart, and the stream need not begin on a
    /// packet boundary, so the first packet can start anywhere in the first 188 bytes. That
    /// worst case wants 187 + 3 * 188 + 1 bytes, which is this.
    /// </para>
    /// <para>
    /// It matters because a read is not a message. A single <c>ReadAsync</c> returns whatever
    /// has arrived, which over a slow or distant link is regularly a few hundred bytes, and
    /// deciding on that alone declares a perfectly good transport stream to be something else.
    /// Below this length "not proven" is the only honest answer; above it, a negative is one.
    /// </para>
    /// </remarks>
    public const int ConclusiveLength = TransportStreamPacketLength * RequiredConsecutiveSyncBytes;

    /// <summary>
    /// Establishes whether the opening bytes of a source are an MPEG-TS stream.
    /// </summary>
    /// <remarks>
    /// A false answer on fewer than <see cref="ConclusiveLength"/> bytes means only that the
    /// proof is not there yet.
    /// </remarks>
    /// <param name="prefix">The first bytes received from the source.</param>
    /// <returns><see langword="true"/> if the prefix is a transport stream.</returns>
    public static bool IsTransportStream(ReadOnlySpan<byte> prefix)
    {
        // The stream need not begin on a packet boundary, so every offset within one packet
        // is a candidate for where the first packet starts.
        for (var start = 0; start < TransportStreamPacketLength && start < prefix.Length; start++)
        {
            if (prefix[start] != SyncByte)
            {
                continue;
            }

            var confirmed = 1;
            for (var offset = start + TransportStreamPacketLength;
                 offset < prefix.Length && confirmed < RequiredConsecutiveSyncBytes;
                 offset += TransportStreamPacketLength)
            {
                if (prefix[offset] != SyncByte)
                {
                    confirmed = 0;
                    break;
                }

                confirmed++;
            }

            if (confirmed >= RequiredConsecutiveSyncBytes)
            {
                return true;
            }
        }

        return false;
    }
}
