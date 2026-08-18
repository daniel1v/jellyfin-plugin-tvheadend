using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TVHeadEnd.Streaming;

/// <summary>
/// The Program Map Table of the stream actually being delivered.
/// </summary>
/// <remarks>
/// <para>
/// The ground truth for how the delivered transport stream is laid out, and therefore for the
/// order FFmpeg will number its streams in. libavformat creates one stream per entry as it walks
/// this table, so an entry's position here is the index every later <c>-map</c> argument will
/// mean.
/// </para>
/// <para>
/// This is read from the bytes that arrive rather than from anything TVHeadend reports about the
/// service, because the two can differ: the <c>pass</c> muxer rewrites the table down to the
/// streams the subscription actually carries when it is configured to, and leaves the
/// broadcaster's own table in place when it is not.
/// </para>
/// </remarks>
/// <param name="ProgramNumber">The program this table describes.</param>
/// <param name="PcrPid">The PID carrying the program clock reference.</param>
/// <param name="Entries">The elementary streams, in the order the table lists them.</param>
public sealed record ProgramMapTable(int ProgramNumber, int PcrPid, IReadOnlyList<ProgramMapEntry> Entries)
{
    private const byte TableIdProgramMap = 0x02;

    /// <summary>
    /// Gets the PID of the first video stream, or -1 when the program carries none.
    /// </summary>
    public int VideoPid => Entries.FirstOrDefault(entry => entry.IsVideo)?.Pid ?? -1;

    /// <summary>
    /// Gets the stream type of the first video stream, or zero when the program carries none.
    /// </summary>
    public byte VideoStreamType => Entries.FirstOrDefault(entry => entry.IsVideo)?.StreamType ?? 0;

    /// <summary>
    /// Parses a complete PMT section.
    /// </summary>
    /// <param name="section">The reassembled section.</param>
    /// <returns>The table, or <see langword="null"/> when the section is not a usable PMT.</returns>
    public static ProgramMapTable? Parse(ReadOnlySpan<byte> section)
    {
        if (section.Length < 13 || section[0] != TableIdProgramMap)
        {
            return null;
        }

        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];

        // The declared length has to fit what was collected, and the last four bytes are the CRC
        // rather than table content.
        var end = 3 + sectionLength - 4;
        if (end > section.Length || end < 12)
        {
            return null;
        }

        var programNumber = (section[3] << 8) | section[4];
        var pcrPid = ((section[8] & 0x1F) << 8) | section[9];
        var programInfoLength = ((section[10] & 0x0F) << 8) | section[11];

        var offset = 12 + programInfoLength;
        if (offset > end)
        {
            return null;
        }

        var entries = new List<ProgramMapEntry>();
        while (offset + 5 <= end)
        {
            var streamType = section[offset];
            var pid = ((section[offset + 1] & 0x1F) << 8) | section[offset + 2];
            var infoLength = ((section[offset + 3] & 0x0F) << 8) | section[offset + 4];

            entries.Add(new ProgramMapEntry(streamType, pid));
            offset += 5 + infoLength;
        }

        return new ProgramMapTable(programNumber, pcrPid, entries);
    }

    /// <summary>
    /// Gets the PIDs the table announces.
    /// </summary>
    /// <returns>The PIDs.</returns>
    public IReadOnlySet<int> GetPids() => Entries.Select(entry => entry.Pid).ToHashSet();

    /// <summary>
    /// Renders the layout for a log line.
    /// </summary>
    /// <returns>A short description.</returns>
    public string Describe()
        => string.Join(
            ", ",
            Entries.Select((entry, index) => string.Create(
                CultureInfo.InvariantCulture,
                $"{index}:type=0x{entry.StreamType:x2}/pid={entry.Pid}")));
}
