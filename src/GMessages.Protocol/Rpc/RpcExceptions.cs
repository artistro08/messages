namespace GMessages.Protocol.Rpc;

/// <summary>
/// The exception that is thrown when the phone does not answer a request within the hard timeout.
/// </summary>
/// <remarks>
/// Ported from <c>ErrPhoneNotResponding</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/session_handler.go">session_handler.go</see>.
/// The server accepted the request, so the phone can still do it when it comes back online.
/// </remarks>
public sealed class PhoneNotRespondingException : Exception
{
    /// <summary>
    /// Creates the exception with the default message.
    /// </summary>
    public PhoneNotRespondingException()
        : base("Phone did not respond to request.")
    {
    }

    /// <summary>
    /// Creates the exception with a custom message.
    /// </summary>
    /// <param name="message">The message.</param>
    public PhoneNotRespondingException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates the exception with a custom message and cause.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public PhoneNotRespondingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The exception that is thrown when no connection is open, or the connection closes while a request waits for an answer.
/// </summary>
/// <remarks>
/// Ported from <c>ErrConnectionClosed</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/session_handler.go">session_handler.go</see>.
/// </remarks>
public sealed class ConnectionClosedException : Exception
{
    /// <summary>
    /// Creates the exception with the default message.
    /// </summary>
    public ConnectionClosedException()
        : base("Client disconnected before response was received.")
    {
    }

    /// <summary>
    /// Creates the exception with a custom message.
    /// </summary>
    /// <param name="message">The message.</param>
    public ConnectionClosedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates the exception with a custom message and cause.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ConnectionClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
