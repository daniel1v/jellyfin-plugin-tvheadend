namespace TVHeadEnd.Streaming;

/// <summary>
/// One PSI section, together with the transport stream packets it arrived in.
/// </summary>
/// <remarks>
/// The two are kept together because a joining reader has to be given bytes that parse back to the
/// table the plugin acted on. Counting the packets separately from the parsing is how they came to
/// disagree.
/// </remarks>
internal sealed class PsiSection
{
    internal PsiSection(byte[] bytes, byte[][] packets)
    {
        Bytes = bytes;
        Packets = packets;
    }

    /// <summary>
    /// Gets the section itself.
    /// </summary>
    internal byte[] Bytes { get; }

    /// <summary>
    /// Gets the packets it arrived in.
    /// </summary>
    internal byte[][] Packets { get; }
}
