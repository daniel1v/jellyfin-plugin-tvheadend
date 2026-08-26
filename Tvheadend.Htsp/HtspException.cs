using System;

namespace Tvheadend.Htsp;

/// <summary>
/// The base of every failure this client reports.
/// </summary>
public class HtspException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HtspException"/> class.
    /// </summary>
    public HtspException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HtspException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public HtspException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HtspException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public HtspException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
