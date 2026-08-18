namespace TVHeadEnd.Streaming;

/// <summary>
/// One elementary stream as the Program Map Table announces it.
/// </summary>
/// <param name="StreamType">The MPEG stream type.</param>
/// <param name="Pid">The PID it is carried on.</param>
public sealed record ProgramMapEntry(byte StreamType, int Pid)
{
    /// <summary>
    /// Gets a value indicating whether the stream type names video.
    /// </summary>
    public bool IsVideo => TransportStreamPacket.IsVideoStreamType(StreamType);
}
