using System;
using System.Collections.Generic;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Knows where in a buffered stream a decoder may be started, and what it has to be given
    /// there.
    /// </summary>
    /// <remarks>
    /// Every container answers this, in its own vocabulary. A transport stream needs its program
    /// tables and a random access point; Matroska needs its initialisation header and a cluster
    /// boundary. Both are "a prefix plus a position", which is why the buffer can be written once
    /// and stay indifferent to what TVHeadend delivers.
    /// </remarks>
    public interface ILiveStreamBootstrap
    {
        /// <summary>
        /// Gets the byte boundary a join position has to respect. A transport stream is only
        /// addressable at packet boundaries; other containers are addressable exactly.
        /// </summary>
        int Alignment { get; }

        /// <summary>
        /// Records what was just appended to the buffer.
        /// </summary>
        /// <param name="basePosition">The logical position the chunk was written at.</param>
        /// <param name="data">The bytes written.</param>
        /// <param name="randomAccessOffsets">
        /// Access point offsets within the chunk, where the caller already knows them. Containers
        /// that have to find their own ignore this.
        /// </param>
        void Record(long basePosition, ReadOnlySpan<byte> data, IReadOnlyList<int>? randomAccessOffsets);

        /// <summary>
        /// Finds the latest position a reader may join at.
        /// </summary>
        /// <param name="oldestPosition">The oldest position the buffer still holds.</param>
        /// <param name="position">The position to start reading at.</param>
        /// <returns>Whether a usable entry point is still inside the window.</returns>
        bool TryGetJoinPosition(long oldestPosition, out long position);

        /// <summary>
        /// Builds the bytes a joining reader needs before the buffer content.
        /// </summary>
        /// <returns>The prefix, empty when none has been captured yet.</returns>
        byte[] CreateBootstrapPrefix();

        /// <summary>
        /// Forgets every recorded position.
        /// </summary>
        void Reset();
    }
}
