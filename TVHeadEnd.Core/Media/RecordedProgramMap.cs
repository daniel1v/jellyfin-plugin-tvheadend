using System;
using System.IO;

namespace TVHeadEnd.Core.Media;

/// <summary>
/// Reads the program map out of a recorded transport stream.
/// </summary>
/// <remarks>
/// <para>
/// A recording made with TVHeadend's <c>pass</c> profile is the broadcast itself, tables included.
/// The live path reads those tables as the stream arrives; a recording has them sitting in the
/// sample already fetched to analyse it, and nothing but a read of that file is needed to learn
/// the same things about it.
/// </para>
/// <para>
/// Deliberately small and stateless. The live path's conditioner follows a stream that can change
/// programme underneath it and has to answer what is on air now; this answers one question once,
/// about bytes that will never change again.
/// </para>
/// </remarks>
public static class RecordedProgramMap
{
    /// <summary>
    /// How much of the sample is searched for the tables.
    /// </summary>
    /// <remarks>
    /// The tables repeat every few hundred milliseconds, so they are at the very front of any
    /// recording. A bounded search keeps a file that is not a transport stream, or one whose
    /// opening is damaged, from being read end to end for nothing.
    /// </remarks>
    private const int SearchLimit = 4 * 1024 * 1024;

    /// <summary>
    /// Finds the program map in a recorded transport stream.
    /// </summary>
    /// <param name="path">The sample file.</param>
    /// <returns>
    /// The program map, or <see langword="null"/> when the file is not a transport stream or
    /// carries no complete pair of tables in the part searched.
    /// </returns>
    public static ProgramMapTable? ReadFrom(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            TransportStreamPacket.Length,
            FileOptions.SequentialScan);

        return ReadFrom(file);
    }

    /// <summary>
    /// Finds the program map in a recorded transport stream.
    /// </summary>
    /// <param name="stream">The bytes of the recording, from its beginning.</param>
    /// <returns>
    /// The program map, or <see langword="null"/> when the bytes are not a transport stream or
    /// carry no complete pair of tables in the part searched.
    /// </returns>
    public static ProgramMapTable? ReadFrom(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var association = new PsiSectionAssembler();
        var map = new PsiSectionAssembler();
        var packet = new byte[TransportStreamPacket.Length];
        var programMapPid = -1;
        var read = 0;

        while (read < SearchLimit && TransportStreamPacket.ReadFrom(stream, packet))
        {
            read += packet.Length;

            if (packet[0] != TransportStreamPacket.SyncByte)
            {
                // Not a transport stream, or no longer aligned to one. Either way nothing after
                // this point can be trusted to be a packet boundary.
                return null;
            }

            var pid = TransportStreamPacket.ReadPid(packet);

            if (pid == TransportStreamPacket.ProgramAssociationTablePid && association.Accept(packet))
            {
                foreach (var section in association.Completed)
                {
                    if (ProgramAssociationTable.Parse(section.Bytes) is { } table)
                    {
                        programMapPid = table.ProgramMapPid;
                    }
                }
            }
            else if (pid >= 0 && pid == programMapPid && map.Accept(packet))
            {
                foreach (var section in map.Completed)
                {
                    if (ProgramMapTable.Parse(section.Bytes) is { } table)
                    {
                        return table;
                    }
                }
            }
        }

        return null;
    }
}
