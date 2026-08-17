using System;
using System.Collections.Generic;
using System.IO;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Finds the places a decoder may start in a Matroska stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matroska is not self-synchronising the way a transport stream is: nothing can be decoded
    /// without the initialisation header -- EBML, Segment, Tracks -- which is written once at the
    /// very start. In a ring buffer that header is overwritten the first time the buffer wraps,
    /// and every reader joining afterwards would land inside a cluster with no track definitions.
    /// Measured on the real streams that is about eight minutes at 8.5 Mbit/s.
    /// </para>
    /// <para>
    /// So the header is captured while it goes past and kept, and cluster starts are indexed as
    /// entry points. A cluster carries its own timecode and begins a new set of blocks, which
    /// makes it the Matroska equivalent of a random access point.
    /// </para>
    /// </remarks>
    public sealed class MatroskaBootstrapIndex : ILiveStreamBootstrap
    {
        /// <summary>
        /// A header larger than this is not a header any more; giving up keeps a stream that is
        /// not Matroska after all from consuming memory without bound.
        /// </summary>
        private const int MaximumHeaderLength = 1024 * 1024;

        /// <summary>
        /// At roughly one cluster every few seconds this covers far more than any buffer window.
        /// </summary>
        private const int MaximumPoints = 4096;

        /// <summary>
        /// The EBML identifier of a Cluster element.
        /// </summary>
        private static readonly byte[] ClusterId = [0x1F, 0x43, 0xB6, 0x75];

        private readonly object _gate = new();
        private readonly Queue<long> _clusters = new();
        private readonly List<byte> _header = [];
        private readonly byte[] _carry = new byte[3];

        private int _carryLength;
        private bool _headerComplete;
        private bool _headerAbandoned;

        /// <summary>
        /// Gets a value indicating whether the initialisation header has been captured.
        /// </summary>
        public bool HasHeader
        {
            get
            {
                lock (_gate)
                {
                    return _headerComplete;
                }
            }
        }

        /// <summary>
        /// Gets the number of cluster starts currently remembered.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _clusters.Count;
                }
            }
        }

        /// <inheritdoc />
        public void Record(long basePosition, ReadOnlySpan<byte> data, IReadOnlyList<int>? randomAccessOffsets)
        {
            if (data.IsEmpty)
            {
                return;
            }

            lock (_gate)
            {
                foreach (var offset in FindClusterStarts(data))
                {
                    var position = basePosition + offset;
                    if (position < 0)
                    {
                        continue;
                    }

                    if (!_headerComplete && !_headerAbandoned)
                    {
                        // Everything ahead of the first cluster is the header.
                        AppendHeader(data[..Math.Clamp(offset, 0, data.Length)]);
                        _headerComplete = true;
                    }

                    if (_clusters.Count == MaximumPoints)
                    {
                        _clusters.Dequeue();
                    }

                    _clusters.Enqueue(position);
                }

                if (!_headerComplete && !_headerAbandoned)
                {
                    AppendHeader(data);
                    if (_header.Count > MaximumHeaderLength)
                    {
                        _headerAbandoned = true;
                        _header.Clear();
                    }
                }
            }
        }

        /// <inheritdoc />
        public bool TryGetJoinPosition(long oldestPosition, out long position)
        {
            lock (_gate)
            {
                while (_clusters.Count > 0 && _clusters.Peek() < oldestPosition)
                {
                    _clusters.Dequeue();
                }

                if (_clusters.Count == 0 || !_headerComplete)
                {
                    position = 0;
                    return false;
                }

                position = long.MinValue;
                foreach (var candidate in _clusters)
                {
                    if (candidate > position)
                    {
                        position = candidate;
                    }
                }

                return true;
            }
        }

        /// <inheritdoc />
        public byte[] CreateBootstrapPrefix()
        {
            lock (_gate)
            {
                return _headerComplete ? [.. _header] : [];
            }
        }

        /// <inheritdoc />
        public void Reset()
        {
            lock (_gate)
            {
                _clusters.Clear();
            }
        }

        private void AppendHeader(ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                _header.Add(value);
            }
        }

        /// <summary>
        /// Returns the offsets of every cluster identifier in the chunk, carrying the last few
        /// bytes over so an identifier split across two chunks is still found.
        /// </summary>
        private List<int> FindClusterStarts(ReadOnlySpan<byte> data)
        {
            var found = new List<int>();

            if (_carryLength > 0)
            {
                Span<byte> seam = stackalloc byte[_carry.Length + ClusterId.Length];
                var taken = Math.Min(ClusterId.Length, data.Length);
                _carry.AsSpan(0, _carryLength).CopyTo(seam);
                data[..taken].CopyTo(seam[_carryLength..]);

                var seamIndex = seam[..(_carryLength + taken)].IndexOf(ClusterId);
                if (seamIndex >= 0)
                {
                    // Negative when the identifier began in the previous chunk. The offset stays
                    // negative on purpose: added to this chunk's base position it still names the
                    // right absolute place, which is where the cluster actually starts.
                    found.Add(seamIndex - _carryLength);
                }
            }

            var searched = 0;
            while (searched < data.Length)
            {
                var index = data[searched..].IndexOf(ClusterId);
                if (index < 0)
                {
                    break;
                }

                var absolute = searched + index;
                if (!found.Contains(absolute))
                {
                    found.Add(absolute);
                }

                searched = absolute + ClusterId.Length;
            }

            var kept = Math.Min(_carry.Length, data.Length);
            data[^kept..].CopyTo(_carry);
            _carryLength = kept;

            return found;
        }
    }
}
