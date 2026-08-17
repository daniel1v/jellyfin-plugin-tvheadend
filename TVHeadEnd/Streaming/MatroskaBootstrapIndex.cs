using System;
using System.Collections.Generic;

namespace TVHeadEnd.Streaming
{
    /// <summary>
    /// Finds the places a decoder may start in a Matroska stream, by walking its element tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matroska is not self-synchronising the way a transport stream is: nothing can be decoded
    /// without the initialisation header -- EBML, Segment, Tracks -- which is written once at
    /// the very start. In a ring buffer that header is overwritten the first time the buffer
    /// wraps, so it is captured while it goes past and prepended to every reader.
    /// </para>
    /// <para>
    /// Cluster positions are found by parsing, never by searching for the identifier as a byte
    /// sequence. Those four bytes occur constantly inside compressed video, and an earlier
    /// version that searched for them handed readers a position in the middle of a picture: the
    /// prepended header then met noise, FFmpeg resynchronised into a second set of tracks, and
    /// the result was ten streams and a packet size of half a gigabyte.
    /// </para>
    /// <para>
    /// Live Matroska writes the Segment, and usually every Cluster, with an unknown size, so
    /// the walk cannot always skip ahead. Where it cannot, the next Cluster is found by looking
    /// for a candidate and then checking that what follows really is one: a readable size, and a
    /// first child that belongs inside a Cluster. That check is what a coincidence in the video
    /// payload fails.
    /// </para>
    /// </remarks>
    public sealed class MatroskaBootstrapIndex : ILiveStreamBootstrap
    {
        private const uint EbmlHeaderId = 0x1A45DFA3;
        private const uint SegmentId = 0x18538067;
        private const uint ClusterId = 0x1F43B675;

        // The elements a Cluster may begin with. Checking for one of these is what separates a
        // real Cluster from four bytes that happen to look like its identifier.
        private const uint TimecodeId = 0xE7;
        private const uint SimpleBlockId = 0xA3;
        private const uint BlockGroupId = 0xA0;
        private const uint PrevSizeId = 0xAB;
        private const uint PositionId = 0xA7;

        /// <summary>
        /// A header larger than this is not a header any more; giving up keeps a stream that is
        /// not Matroska after all from consuming memory without bound.
        /// </summary>
        private const int MaximumHeaderLength = 4 * 1024 * 1024;

        /// <summary>
        /// Enough behind a candidate identifier to tell whether it really begins a cluster: the
        /// identifier, a size, and the identifier of the first child.
        /// </summary>
        private const int ClusterProbeLength = 8;

        private const int ClusterIdLength = 4;

        private const int MaximumPoints = 4096;

        private readonly object _gate = new();
        private readonly Queue<long> _clusters = new();
        private readonly List<byte> _header = [];

        private byte[] _window = new byte[128 * 1024];
        private int _windowLength;
        private long _windowStart;
        private bool _insideSegment;
        private bool _headerComplete;
        private bool _abandoned;
        private long _pendingSkip;

        /// <inheritdoc />
        /// <remarks>
        /// Matroska elements begin at any byte, so a join position must be used exactly.
        /// </remarks>
        public int Alignment => 1;

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
                if (_abandoned)
                {
                    return;
                }

                if (_windowLength == 0)
                {
                    _windowStart = basePosition;
                }

                Append(data);
                Parse();
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

        private void Append(ReadOnlySpan<byte> data)
        {
            if (_windowLength + data.Length > _window.Length)
            {
                Array.Resize(ref _window, Math.Max(_window.Length * 2, _windowLength + data.Length));
            }

            data.CopyTo(_window.AsSpan(_windowLength));
            _windowLength += data.Length;
        }

        /// <summary>
        /// Consumes everything ahead of the parse cursor, so the window holds only what is still
        /// needed.
        /// </summary>
        private void Consume(int count)
        {
            if (count <= 0)
            {
                return;
            }

            if (!_headerComplete && !_abandoned)
            {
                for (var i = 0; i < count; i++)
                {
                    _header.Add(_window[i]);
                }

                if (_header.Count > MaximumHeaderLength)
                {
                    _abandoned = true;
                    _header.Clear();
                }
            }

            _window.AsSpan(count, _windowLength - count).CopyTo(_window);
            _windowLength -= count;
            _windowStart += count;
        }

        private void Parse()
        {
            while (_windowLength > 0)
            {
                // An element longer than the window left a remainder to step over. Until that is
                // gone the cursor is not at an element boundary, and reading an identifier here
                // would parse payload as structure.
                if (_pendingSkip > 0 && !DrainPendingSkip())
                {
                    return;
                }

                if (_windowLength == 0)
                {
                    return;
                }

                var window = _window.AsSpan(0, _windowLength);

                // Both readers refuse rather than guess when the window is short, so the loop
                // simply waits for the next chunk. A fixed minimum here would stop the walk while
                // a whole element was still sitting in front of it.
                if (!EbmlReader.TryReadId(window, out var id, out var idLength)
                    || !EbmlReader.TryReadSize(window[idLength..], out var size, out var sizeLength))
                {
                    return;
                }

                var headerLength = idLength + sizeLength;

                if (id == ClusterId)
                {
                    RecordCluster(_windowStart);

                    // The header ends where the first cluster begins.
                    _headerComplete = true;

                    if (size == EbmlReader.UnknownSize)
                    {
                        // Nothing to skip past; find where the next one begins instead.
                        Consume(headerLength);
                        if (!SkipToNextCluster())
                        {
                            return;
                        }

                        continue;
                    }

                    if (!TryConsumeWhole(headerLength, size))
                    {
                        return;
                    }

                    continue;
                }

                // The Segment holds the clusters, so descend into it rather than over it. The
                // EBML header and the Segment's own children are skipped by their size.
                if (id == SegmentId)
                {
                    _insideSegment = true;
                    Consume(headerLength);
                    continue;
                }

                if (size == EbmlReader.UnknownSize)
                {
                    // Only the Segment and Clusters are expected to be unsized. Anything else
                    // cannot be walked over, so fall back to looking for the next cluster.
                    Consume(headerLength);
                    if (!SkipToNextCluster())
                    {
                        return;
                    }

                    continue;
                }

                if (id != EbmlHeaderId && !_insideSegment)
                {
                    // Not Matroska after all.
                    _abandoned = true;
                    _header.Clear();
                    return;
                }

                if (!TryConsumeWhole(headerLength, size))
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Consumes an element whose length is known, if all of it is in the window.
        /// </summary>
        private bool TryConsumeWhole(int headerLength, long size)
        {
            var total = headerLength + size;
            if (total > _windowLength)
            {
                // Drop what is certainly payload and remember the rest is still to come.
                var dropped = _windowLength;
                Consume(dropped);
                _pendingSkip = total - dropped;
                return DrainPendingSkip();
            }

            Consume((int)total);
            return true;
        }

        private bool DrainPendingSkip()
        {
            while (_pendingSkip > 0)
            {
                if (_windowLength == 0)
                {
                    return false;
                }

                var take = (int)Math.Min(_pendingSkip, _windowLength);
                Consume(take);
                _pendingSkip -= take;
            }

            return true;
        }

        /// <summary>
        /// Advances to the next element that really is a Cluster.
        /// </summary>
        /// <remarks>
        /// A candidate is accepted only when the bytes after its identifier read as a size and
        /// the element that follows is one a Cluster may begin with. Four bytes of video payload
        /// that happen to match the identifier practically never satisfy both.
        /// </remarks>
        private bool SkipToNextCluster()
        {
            if (_pendingSkip > 0 && !DrainPendingSkip())
            {
                return false;
            }

            var window = _window.AsSpan(0, _windowLength);
            var searched = 0;

            while (true)
            {
                var index = window[searched..].IndexOf<byte>([0x1F, 0x43, 0xB6, 0x75]);
                if (index < 0)
                {
                    break;
                }

                var at = searched + index;
                if (at + ClusterProbeLength > window.Length)
                {
                    // A candidate, but not enough behind it to tell whether it is real. Keep it
                    // and decide once the next chunk arrives.
                    Consume(at);
                    return false;
                }

                if (IsCluster(window[at..]))
                {
                    Consume(at);
                    return true;
                }

                searched = at + 1;
            }

            // No candidate at all: keep only what a split identifier could still span.
            var keep = Math.Min(_windowLength, ClusterIdLength - 1);
            Consume(_windowLength - keep);
            return false;
        }

        private static bool IsCluster(ReadOnlySpan<byte> data)
        {
            if (!EbmlReader.TryReadId(data, out var id, out var idLength) || id != ClusterId)
            {
                return false;
            }

            if (!EbmlReader.TryReadSize(data[idLength..], out _, out var sizeLength))
            {
                return false;
            }

            if (!EbmlReader.TryReadId(data[(idLength + sizeLength)..], out var childId, out _))
            {
                return false;
            }

            return childId is TimecodeId or SimpleBlockId or BlockGroupId or PrevSizeId or PositionId;
        }

        private void RecordCluster(long position)
        {
            if (_clusters.Count == MaximumPoints)
            {
                _clusters.Dequeue();
            }

            _clusters.Enqueue(position);
        }
    }
}
