namespace TVHeadEnd.Streaming;

/// <summary>
/// How strong a promise a place in the stream makes to a decoder starting there.
/// </summary>
/// <remarks>
/// <para>
/// Two different things that used to be treated as one. A broadcast marks random access points in
/// the adaptation field, and that mark is a true statement about the transport stream: a decoder
/// may begin there. It says nothing about which kind of picture begins there, and DVB H.264 uses
/// it for both -- measured on air, ZDF marks 26 of 38 access points on IDR pictures and the rest on
/// open-GOP I-frames, while Das Erste marks 41 without an IDR among them.
/// </para>
/// <para>
/// So the guarantee is named. The streaming layer records what each point actually offers and
/// knows nothing about who is asking; which guarantee a given decoder needs is decided outside it,
/// by the one part of the plugin that knows anything about the caller.
/// </para>
/// </remarks>
public enum RandomAccessGuarantee
{
    /// <summary>
    /// The broadcast marked this as a random access point. Enough for a decoder that recovers from
    /// an open GOP, which is every software decoder this plugin has met, FFmpeg included.
    /// </summary>
    DvbRandomAccess = 0,

    /// <summary>
    /// The access unit here was read and found to contain an IDR picture. Required by decoders
    /// that emit nothing until they have seen one.
    /// </summary>
    Idr = 1,
}
