using System;

namespace Tvheadend.Htsp;

/// <summary>
/// TVHeadend refused the credentials, or refused the operation to this user.
/// </summary>
public sealed class HtspAuthenticationException : HtspException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HtspAuthenticationException"/> class.
    /// </summary>
    public HtspAuthenticationException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HtspAuthenticationException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public HtspAuthenticationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HtspAuthenticationException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public HtspAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
