using System;
using Tvheadend.Htsp.Protocol;

namespace Tvheadend.Htsp.Model;

/// <summary>
/// The server has ended a subscription.
/// </summary>
/// <param name="SubscriptionId">The subscription.</param>
/// <param name="Status">Why it ended, or <see langword="null"/> when it ended normally.</param>
/// <param name="SubscriptionError">The machine-readable form.</param>
public sealed record HtspSubscriptionStop(int SubscriptionId, string? Status, string? SubscriptionError)
{
    /// <summary>
    /// Reads a <c>subscriptionStop</c> message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The parsed stop.</returns>
    public static HtspSubscriptionStop From(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new HtspSubscriptionStop(
            message.GetInt32("subscriptionId") ?? 0,
            message.GetString("status"),
            message.GetString("subscriptionError"));
    }
}
