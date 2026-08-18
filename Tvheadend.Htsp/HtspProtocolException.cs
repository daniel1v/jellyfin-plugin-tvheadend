using System;

namespace Tvheadend.Htsp;

/// <summary>
/// The peer sent something that is not valid HTSP.
/// </summary>
/// <remarks>
/// Always fatal to the connection: the framing is a byte stream with no resynchronisation point,
/// so once the reader is out of step with it nothing later can be trusted either.
/// </remarks>
public sealed class HtspProtocolException : HtspException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HtspProtocolException"/> class.
    /// </summary>
    public HtspProtocolException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HtspProtocolException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public HtspProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HtspProtocolException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public HtspProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
