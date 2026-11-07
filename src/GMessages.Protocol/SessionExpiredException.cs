namespace GMessages.Protocol;

/// <summary>
/// The exception that is thrown when the server refuses the token refresh permanently. The user must pair again.
/// </summary>
/// <remarks>
/// Replaces the error that Go's <c>Connect</c> returns when <c>isFatalRefreshError</c> is true in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>.
/// That is HTTP 401, 403 or 404, or the Google errors "invalid credentials" and "entity not found". The inner
/// exception is the error of the server.
/// </remarks>
public sealed class SessionExpiredException : Exception
{
    /// <summary>
    /// Creates the exception with the default message.
    /// </summary>
    public SessionExpiredException()
        : base("The session has expired; pair again.")
    {
    }

    /// <summary>
    /// Creates the exception with a custom message.
    /// </summary>
    /// <param name="message">The message.</param>
    public SessionExpiredException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates the exception with a custom message and cause.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SessionExpiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
