using System;

namespace Tvheadend.Htsp;

/// <summary>
/// TVHeadend answered a request with an error.
/// </summary>
public sealed class HtspRequestException : HtspException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HtspRequestException"/> class.
    /// </summary>
    /// <param name="method">The method that failed.</param>
    /// <param name="error">What TVHeadend said about it.</param>
    public HtspRequestException(string method, string error)
        : base($"TVHeadend refused '{method}': {error}")
    {
        Method = method;
        Error = error;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HtspRequestException"/> class.
    /// </summary>
    public HtspRequestException()
    {
        Method = string.Empty;
        Error = string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HtspRequestException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public HtspRequestException(string message)
        : base(message)
    {
        Method = string.Empty;
        Error = message;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HtspRequestException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public HtspRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
        Method = string.Empty;
        Error = message;
    }

    /// <summary>
    /// Gets the method that failed.
    /// </summary>
    public string Method { get; }

    /// <summary>
    /// Gets what TVHeadend said about it.
    /// </summary>
    public string Error { get; }
}
