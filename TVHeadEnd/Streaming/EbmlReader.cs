using System;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Reads the two variable-length integers every EBML element begins with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An element is an identifier followed by a size, both stored as variable-length integers
    /// whose length is announced by the number of leading zero bits of the first byte. The
    /// identifier keeps its marker bit -- which is why the Cluster identifier is written
    /// <c>1F 43 B6 75</c> -- while the size has it removed.
    /// </para>
    /// <para>
    /// This exists because searching for an identifier as a plain byte sequence does not work.
    /// In eight megabits per second of compressed video the four bytes of the Cluster identifier
    /// occur constantly by coincidence, and an earlier attempt that scanned for them landed
    /// readers in the middle of a picture.
    /// </para>
    /// </remarks>
    internal static class EbmlReader
    {
        /// <summary>
        /// The size value that means "this element runs until something else begins", which live
        /// Matroska uses for the Segment and often for every Cluster.
        /// </summary>
        public const long UnknownSize = -1;

        /// <summary>
        /// Reads an element identifier.
        /// </summary>
        /// <param name="data">The bytes at the element boundary.</param>
        /// <param name="id">The identifier, marker bits included.</param>
        /// <param name="length">How many bytes it occupied.</param>
        /// <returns>Whether a whole identifier was available.</returns>
        public static bool TryReadId(ReadOnlySpan<byte> data, out uint id, out int length)
        {
            id = 0;
            length = 0;
            if (data.IsEmpty)
            {
                return false;
            }

            length = LeadingLength(data[0]);
            if (length is < 1 or > 4 || data.Length < length)
            {
                length = 0;
                return false;
            }

            for (var i = 0; i < length; i++)
            {
                id = (id << 8) | data[i];
            }

            return true;
        }

        /// <summary>
        /// Reads an element size.
        /// </summary>
        /// <param name="data">The bytes following the identifier.</param>
        /// <param name="size">The size, or <see cref="UnknownSize"/>.</param>
        /// <param name="length">How many bytes it occupied.</param>
        /// <returns>Whether a whole size was available.</returns>
        public static bool TryReadSize(ReadOnlySpan<byte> data, out long size, out int length)
        {
            size = 0;
            length = 0;
            if (data.IsEmpty)
            {
                return false;
            }

            length = LeadingLength(data[0]);
            if (length is < 1 or > 8 || data.Length < length)
            {
                length = 0;
                return false;
            }

            // The marker bit is not part of the value.
            long value = data[0] & ((1 << (8 - length)) - 1);
            var allOnes = value == ((1 << (8 - length)) - 1);

            for (var i = 1; i < length; i++)
            {
                value = (value << 8) | data[i];
                allOnes &= data[i] == 0xFF;
            }

            size = allOnes ? UnknownSize : value;
            return true;
        }

        /// <summary>
        /// Returns how many bytes a variable-length integer starting with this byte occupies, or
        /// zero when no marker bit is set at all.
        /// </summary>
        /// <param name="first">The first byte.</param>
        /// <returns>The length in bytes.</returns>
        private static int LeadingLength(byte first)
        {
            if (first == 0)
            {
                return 0;
            }

            var length = 1;
            for (var mask = 0x80; (first & mask) == 0; mask >>= 1)
            {
                length++;
            }

            return length;
        }
    }
}
