using System;
using System.Collections.Generic;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Where in a buffered stream a decoder may be started, and the program tables it needs there.
/// </summary>
/// <remarks>
/// <para>
/// A consumer joining a channel that is already running cannot simply be pointed near the live
/// edge. It would land in the middle of a picture with no program tables to map the elementary
/// streams by, which is exactly the state a tuner hands over and which no decoder recovers from on
/// its own. This holds the random access points the writer passed, so a late reader can be given
/// the most recent one still inside the buffer window, preceded by the tables that were valid
/// there.
/// </para>
/// <para>
/// Tables and access points are one state, not two. They are written together and read together,
/// and the layout generation is what keeps them honest: points found under one program layout are
/// dropped the moment tables of the next arrive, so there is no ordering for a caller to get right
/// and no window in which a reader can be handed the new description and the old picture.
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
    private int _generation = -1;
    private long _generationStart;

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
    /// Records the tables of one chunk and the access points found in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One call under one lock, because the two halves only mean anything as a pair. Publishing
    /// the access points first leaves a window in which a reader joins at a position described by
    /// tables it has not been given; publishing the tables first leaves the mirror image. Either
    /// way the reader is handed a picture and a map of a different programme.
    /// </para>
    /// <para>
    /// Access points without tables are dropped rather than stored, because there is nothing they
    /// could be described by, and points from an earlier layout are dropped the moment tables of a
    /// later one arrive.
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
            if (!tables.HasBoth)
            {
                // Nothing here can be joined at: a position with no tables to describe it is the
                // state a tuner hands over, which is what this exists to spare a reader.
                return;
            }

            if (tables.Generation != _generation)
            {
                // The broadcaster changed what the stream contains. Every position found under
                // the layout before describes a picture these tables do not, and every byte
                // written before this chunk belongs to that older picture.
                _points.Clear();
                _generation = tables.Generation;

                // Where the layout actually began, which the conditioner knows because it saw the
                // table that changed it. Falling back to the start of the chunk would be a claim
                // that the change happened earlier than it did, and would let a reader be sent to
                // bytes of the programme before behind the new tables.
                _generationStart = tables.GenerationStartOffset >= 0
                    ? basePosition + tables.GenerationStartOffset
                    : basePosition;
            }

            _programAssociationPackets = [.. tables.ProgramAssociationPackets];
            _programMapPackets = [.. tables.ProgramMapPackets];

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
    /// Takes the whole of what a reader needs to start: the tables, and where to start reading.
    /// </summary>
    /// <remarks>
    /// One call, because the answer is one state. Asking for the tables and the position
    /// separately lets the writer publish a new layout between the two, and the reader then holds
    /// the tables of one programme and a position inside another -- the failure this index exists
    /// to make impossible, arrived at by using it in the obvious way.
    /// </remarks>
    /// <param name="oldestPosition">The oldest position the buffer still holds.</param>
    /// <returns>Where to start and what to send first.</returns>
    public StreamJoin CreateJoin(long oldestPosition)
    {
        lock (_gate)
        {
            while (_points.Count > 0 && _points.Peek() < oldestPosition)
            {
                _points.Dequeue();
            }

            long? position = null;
            foreach (var candidate in _points)
            {
                // The newest one: a late reader wants the least delay it can safely have, and
                // everything recorded has by definition already been written.
                if (position is null || candidate > position)
                {
                    position = candidate;
                }
            }

            if (position is { } recorded)
            {
                return StreamJoin.At(CreateBootstrapPrefixLocked(), recorded);
            }

            // No access point survives. Reading on from the oldest bytes is only sound while those
            // bytes belong to the layout the tables describe -- at the start of a stream they
            // always do, and after a program layout change they do not until the change itself has
            // scrolled past. Sending a reader there anyway is how it would be handed the new
            // tables in front of the previous programme, which is the pairing this whole index
            // exists to prevent.
            return oldestPosition >= _generationStart && _programAssociationPackets.Length > 0
                ? StreamJoin.FromOldest(CreateBootstrapPrefixLocked())
                : StreamJoin.NotYet;
        }
    }

    /// <summary>
    /// Builds the bytes a joining reader has to be given before the buffer content, so its
    /// decoder can map the elementary streams.
    /// </summary>
    /// <remarks>
    /// Only reachable through <see cref="CreateJoin"/>, and deliberately so. Offering the tables
    /// on their own is what let a caller take them separately from the position they belong to.
    /// </remarks>
    private byte[] CreateBootstrapPrefixLocked()
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
