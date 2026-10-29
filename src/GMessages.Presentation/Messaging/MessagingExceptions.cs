namespace GMessages.Presentation.Messaging;

/// <summary>
/// Thrown when a chat request needs the phone connection and there is none.
/// </summary>
public sealed class MessagingUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MessagingUnavailableException() : base("Not connected to the phone.")
    {
    }
}

/// <summary>
/// Thrown when the phone answers a send with a status other than success.
/// </summary>
public sealed class MessageSendException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="status">The status the phone answered with.</param>
    public MessageSendException(string status) : base($"The phone refused the message ({status}).")
    {
        Status = status;
    }

    /// <summary>Gets the status the phone answered with.</summary>
    public string Status { get; }
}
