using System;
using System.Collections.Generic;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Remembers where in a buffered stream a decoder may be started, and the program tables it
    /// needs there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A consumer that joins a channel already running cannot simply be pointed near the live
    /// edge. It would land in the middle of a picture, with no PAT or PMT to map the elementary
    /// streams by, which is exactly the state a tuner hands over and which no decoder recovers
    /// from on its own. This records every random access point the writer passed, so a late
    /// reader can be given the most recent one that is still inside the buffer window,
    /// preceded by the tables that were valid there.
    /// </para>
    /// <para>
    /// Positions are the logical, ever-increasing ones of
    /// <see cref="LiveRingBuffer.WritePosition"/>, so points that have been overwritten fall
    /// out simply by comparing against the oldest position still held.
    /// </para>
    /// </remarks>
    public sealed class StreamBootstrapIndex : ILiveStreamBootstrap
    {
        /// <summary>
        /// At roughly one access point every 0.6 seconds this covers a buffer window of some
        /// five minutes, well past the point where the ring itself has wrapped.
        /// </summary>
        private const int MaximumPoints = 512;

        private readonly object _gate = new();
        private readonly Queue<long> _points = new();

        private byte[]? _programAssociationTable;
        private byte[]? _programMapTable;

        /// <inheritdoc />
        /// <remarks>
        /// A transport stream is addressable at packet boundaries and nowhere else.
        /// </remarks>
        public int Alignment => TransportStreamPacket.Length;

        /// <summary>
        /// Gets a value indicating whether both program tables have been seen.
        /// </summary>
        public bool HasProgramTables
        {
            get
            {
                lock (_gate)
                {
                    return _programAssociationTable is not null && _programMapTable is not null;
                }
            }
        }

        /// <summary>
        /// Gets the number of random access points currently remembered.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _points.Count;
                }
            }
        }

        /// <summary>
        /// Records the most recent Program Association Table.
        /// </summary>
        /// <param name="packet">The whole transport stream packet.</param>
        public void RecordProgramAssociationTable(ReadOnlySpan<byte> packet)
        {
            lock (_gate)
            {
                _programAssociationTable ??= new byte[TransportStreamPacket.Length];
                packet[..TransportStreamPacket.Length].CopyTo(_programAssociationTable);
            }
        }

        /// <summary>
        /// Records the most recent Program Map Table.
        /// </summary>
        /// <param name="packet">The whole transport stream packet.</param>
        public void RecordProgramMapTable(ReadOnlySpan<byte> packet)
        {
            lock (_gate)
            {
                _programMapTable ??= new byte[TransportStreamPacket.Length];
                packet[..TransportStreamPacket.Length].CopyTo(_programMapTable);
            }
        }

        /// <summary>
        /// Records that a decoder may start at <paramref name="position"/>.
        /// </summary>
        /// <param name="position">The logical buffer position of the access point.</param>
        public void RecordRandomAccessPoint(long position)
        {
            lock (_gate)
            {
                if (_points.Count == MaximumPoints)
                {
                    _points.Dequeue();
                }

                _points.Enqueue(position);
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// A transport stream's access points are found by the conditioner, which is already
        /// parsing every packet, so they arrive as offsets rather than being searched for again.
        /// </remarks>
        public void Record(long basePosition, ReadOnlySpan<byte> data, IReadOnlyList<int>? randomAccessOffsets)
        {
            if (randomAccessOffsets is null)
            {
                return;
            }

            foreach (var offset in randomAccessOffsets)
            {
                RecordRandomAccessPoint(basePosition + offset);
            }
        }

        /// <summary>
        /// Finds the latest position a reader may join at.
        /// </summary>
        /// <param name="oldestPosition">The oldest position the buffer still holds.</param>
        /// <param name="position">The position to start reading at.</param>
        /// <returns>Whether a usable access point is still inside the window.</returns>
        public bool TryGetJoinPosition(long oldestPosition, out long position)
        {
            lock (_gate)
            {
                while (_points.Count > 0 && _points.Peek() < oldestPosition)
                {
                    _points.Dequeue();
                }

                if (_points.Count == 0)
                {
                    position = 0;
                    return false;
                }

                // The newest one: a late reader wants the least delay it can safely have, and
                // everything recorded has by definition already been written.
                position = long.MinValue;
                foreach (var candidate in _points)
                {
                    if (candidate > position)
                    {
                        position = candidate;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Builds the bytes a joining reader has to be given before the buffer content, so that
        /// its decoder can map the elementary streams.
        /// </summary>
        /// <returns>The tables as transport stream packets, empty when none have been seen.</returns>
        public byte[] CreateBootstrapPrefix()
        {
            lock (_gate)
            {
                if (_programAssociationTable is null && _programMapTable is null)
                {
                    return [];
                }

                var length = (_programAssociationTable is null ? 0 : TransportStreamPacket.Length)
                    + (_programMapTable is null ? 0 : TransportStreamPacket.Length);
                var prefix = new byte[length];
                var written = 0;
                if (_programAssociationTable is not null)
                {
                    _programAssociationTable.CopyTo(prefix, written);
                    written += TransportStreamPacket.Length;
                }

                _programMapTable?.CopyTo(prefix, written);
                return prefix;
            }
        }

        /// <summary>
        /// Forgets every recorded position, for a buffer that has been restarted.
        /// </summary>
        public void Reset()
        {
            lock (_gate)
            {
                _points.Clear();
            }
        }
    }
}
