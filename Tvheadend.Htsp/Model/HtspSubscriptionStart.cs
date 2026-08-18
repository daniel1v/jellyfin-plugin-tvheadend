using System;
using System.Collections.Generic;
using System.Linq;
using Tvheadend.Htsp.Protocol;

namespace Tvheadend.Htsp.Model;

/// <summary>
/// TVHeadend's description of a running stream.
/// </summary>
/// <remarks>
/// Sent once the server can describe the stream, and again whenever the broadcast changes shape.
/// For a service carrying video the server withholds it entirely until it has parsed a frame
/// size, so a description that exists always has usable geometry.
/// </remarks>
public sealed record HtspSubscriptionStart
{
    /// <summary>
    /// Gets the subscription this describes.
    /// </summary>
    public required int SubscriptionId { get; init; }

    /// <summary>
    /// Gets the elementary streams, in the order the server listed them.
    /// </summary>
    public required IReadOnlyList<HtspStreamInfo> Streams { get; init; }

    /// <summary>
    /// Gets where the stream is coming from.
    /// </summary>
    public required HtspSourceInfo SourceInfo { get; init; }

    /// <summary>
    /// Gets the first video stream, or <see langword="null"/> for a radio service.
    /// </summary>
    public HtspStreamInfo? Video => Streams.FirstOrDefault(stream => stream.IsVideo);

    /// <summary>
    /// Reads a <c>subscriptionStart</c> message.
    /// </summary>
    /// <remarks>
    /// The message also carries a <c>meta</c> field, which is deliberately not read. The server
    /// adds it to the message rather than to the stream it belongs to, from inside the loop over
    /// the streams, so it ends up being the global header of whichever stream happened to be
    /// described last and there is no way to tell which that was. A field whose owner cannot be
    /// determined is worse than no field.
    /// </remarks>
    /// <param name="message">The message.</param>
    /// <returns>The parsed description.</returns>
    public static HtspSubscriptionStart From(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var streams = new List<HtspStreamInfo>();
        foreach (var entry in message.GetMapList("streams"))
        {
            streams.Add(HtspStreamInfo.From(entry));
        }

        return new HtspSubscriptionStart
        {
            SubscriptionId = message.GetInt32("subscriptionId") ?? 0,
            Streams = streams,
            SourceInfo = HtspSourceInfo.From(message.GetMap("sourceinfo")),
        };
    }
}
