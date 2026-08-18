using System;
using Tvheadend.Htsp.Protocol;

namespace Tvheadend.Htsp.Model;

/// <summary>
/// A change in a subscription's condition.
/// </summary>
/// <param name="SubscriptionId">The subscription.</param>
/// <param name="Status">
/// What is wrong, or <see langword="null"/> when the subscription has recovered.
/// </param>
/// <param name="SubscriptionError">
/// The machine-readable form, such as <c>noFreeAdapter</c>, <c>scrambled</c>, <c>badSignal</c>
/// or <c>tuningFailed</c>.
/// </param>
public sealed record HtspSubscriptionStatus(int SubscriptionId, string? Status, string? SubscriptionError)
{
    /// <summary>
    /// Reads a <c>subscriptionStatus</c> message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The parsed status.</returns>
    public static HtspSubscriptionStatus From(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new HtspSubscriptionStatus(
            message.GetInt32("subscriptionId") ?? 0,
            message.GetString("status"),
            message.GetString("subscriptionError"));
    }
}
