namespace Tvheadend.Htsp;

/// <summary>
/// How a subscription is opened.
/// </summary>
public sealed class HtspSubscriptionOptions
{
    /// <summary>
    /// Gets the subscription weight, which decides who loses a tuner when they are contended.
    /// Zero leaves it to the server.
    /// </summary>
    public long Weight { get; init; }

    /// <summary>
    /// Gets the TVHeadend stream profile, or <see langword="null"/> for the server default.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>
    /// Gets the server-side queue depth, or <see langword="null"/> for the default.
    /// </summary>
    public long? QueueDepth { get; init; }

    /// <summary>
    /// Gets a value indicating whether every stream index is filtered out as soon as the
    /// subscription exists.
    /// </summary>
    /// <remarks>
    /// For a subscription taken out to observe a stream rather than to receive it. TVHeadend
    /// applies the filter at the point where it would serialise a packet, so its parser, its
    /// timestamp fixer and its global header collector all keep running and every
    /// <c>subscriptionStart</c>, status and stop message still arrives -- only the audio and
    /// video payload is never put on the socket.
    /// </remarks>
    public bool DisableAllStreams { get; init; }
}
