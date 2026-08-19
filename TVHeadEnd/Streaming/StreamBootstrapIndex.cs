using System;
using System.Collections.Generic;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Remembers where in a buffered stream a decoder may be started, and the program tables it needs
/// when it starts there.
/// </summary>
/// <remarks>
/// <para>
/// A consumer joining a channel that is already running cannot simply be pointed near the live
/// edge. It would land in the middle of a picture with no program tables to map the elementary
/// streams by, which is exactly the state a tuner hands over and which no decoder recovers from on
/// its own. This records every random access point the writer passed, so a late reader can be
/// given the most recent one still inside the buffer window, preceded by the tables that were
/// valid there.
/// </para>
/// <para>
/// Positions are the logical, ever-increasing ones of <see cref="LiveRingBuffer.WritePosition"/>,
/// so points that have been overwritten fall out by comparing against the oldest position still
/// held.
/// </para>
/// </remarks>
public sealed class StreamBootstrapIndex
{
    /// <summary>
    /// At roughly one access point every 0.6 seconds this covers a window of some five minutes,
    /// well past the point where the ring itself has wrapped.
    /// </summary>
    private const int MaximumPoints = 512;

    private readonly object _gate = new();
    private readonly Queue<long> _points = new();

    private byte[][] _programAssociationPackets = [];
    private byte[][] _programMapPackets = [];

    /// <summary>
    /// Gets the byte boundary a join position has to respect. A transport stream is addressable
    /// at packet boundaries and nowhere else.
    /// </summary>
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
                return _programAssociationPackets.Length > 0 && _programMapPackets.Length > 0;
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
    /// <remarks>
    /// Taken as the whole run of packets the section occupied. A section that spans more than one
    /// packet is normal, and keeping only the first would hand a joining reader a table it cannot
    /// finish reading.
    /// </remarks>
    /// <param name="packets">The packets carrying the section.</param>
    public void RecordProgramAssociationTable(IReadOnlyList<byte[]> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);

        lock (_gate)
        {
            _programAssociationPackets = [.. packets];
        }
    }

    /// <summary>
    /// Records the most recent Program Map Table.
    /// </summary>
    /// <param name="packets">The packets carrying the section.</param>
    public void RecordProgramMapTable(IReadOnlyList<byte[]> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);

        lock (_gate)
        {
            _programMapPackets = [.. packets];
        }
    }

    /// <summary>
    /// Records that a decoder may start at a position.
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

    /// <summary>
    /// Records the tables and the access points of one chunk together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One call under one lock, because the two halves only mean anything as a pair. Publishing
    /// the access points first leaves a window in which a reader joins at a position described by
    /// tables it has not been given; publishing the tables first leaves the mirror image. Either
    /// way the reader is handed a picture and a map of a different programme.
    /// </para>
    /// <para>
    /// The access points are found by the conditioner, which is already parsing every packet, so
    /// they arrive as offsets rather than being searched for a second time.
    /// </para>
    /// </remarks>
    /// <param name="tables">The program tables valid for this chunk.</param>
    /// <param name="basePosition">The logical position the chunk was written at.</param>
    /// <param name="randomAccessOffsets">Access point offsets within the chunk.</param>
    public void Publish(
        ProgramTableSnapshot tables,
        long basePosition,
        IReadOnlyList<int>? randomAccessOffsets)
    {
        ArgumentNullException.ThrowIfNull(tables);

        lock (_gate)
        {
            if (tables.HasBoth)
            {
                _programAssociationPackets = [.. tables.ProgramAssociationPackets];
                _programMapPackets = [.. tables.ProgramMapPackets];
            }

            if (randomAccessOffsets is null)
            {
                return;
            }

            foreach (var offset in randomAccessOffsets)
            {
                if (_points.Count == MaximumPoints)
                {
                    _points.Dequeue();
                }

                _points.Enqueue(basePosition + offset);
            }
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
    /// Builds the bytes a joining reader has to be given before the buffer content, so its decoder
    /// can map the elementary streams.
    /// </summary>
    /// <returns>The tables as transport stream packets, empty when none have been seen.</returns>
    public byte[] CreateBootstrapPrefix()
    {
        lock (_gate)
        {
            var count = _programAssociationPackets.Length + _programMapPackets.Length;
            if (count == 0)
            {
                return [];
            }

            var prefix = new byte[count * TransportStreamPacket.Length];
            var written = 0;
            foreach (var packet in _programAssociationPackets)
            {
                packet.CopyTo(prefix, written);
                written += TransportStreamPacket.Length;
            }

            foreach (var packet in _programMapPackets)
            {
                packet.CopyTo(prefix, written);
                written += TransportStreamPacket.Length;
            }

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
