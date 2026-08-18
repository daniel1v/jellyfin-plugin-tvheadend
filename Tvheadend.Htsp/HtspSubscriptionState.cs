namespace Tvheadend.Htsp;

/// <summary>
/// Where a subscription has got to.
/// </summary>
public enum HtspSubscriptionState
{
    /// <summary>
    /// The server has accepted the subscription but has not described the stream yet.
    /// </summary>
    Starting,

    /// <summary>
    /// The server has described the stream.
    /// </summary>
    Running,

    /// <summary>
    /// The server has stopped the subscription.
    /// </summary>
    Stopped,

    /// <summary>
    /// The connection failed underneath the subscription.
    /// </summary>
    Faulted,
}
