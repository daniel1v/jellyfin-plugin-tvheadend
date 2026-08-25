namespace TVHeadEnd.Streaming;

/// <summary>
/// A place in the conditioned output a decoder may begin, and what beginning there is worth.
/// </summary>
/// <remarks>
/// <para>
/// Positions are counted from the first byte the conditioner emitted, which is the same origin the
/// ring buffer counts from, because everything the conditioner emits is written to the ring and
/// nothing else is. That is what lets a point found in one chunk be raised to a stronger guarantee
/// in a later one: the access unit a random access point opens usually ends in the PES after it,
/// so its worth is only known once bytes beyond it have gone past.
/// </para>
/// <para>
/// A point is published as soon as it is seen, at the guarantee the broadcast itself makes. It is
/// published again, at the same position, if reading its access unit proves it worth more.
/// </para>
/// </remarks>
/// <param name="Position">Where in the conditioned output the point is.</param>
/// <param name="Guarantee">What a decoder starting there is promised.</param>
public readonly record struct StreamAccessPoint(long Position, RandomAccessGuarantee Guarantee);
