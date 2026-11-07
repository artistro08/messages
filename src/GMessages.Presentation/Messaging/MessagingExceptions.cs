namespace GMessages.Presentation.Messaging;

/// <summary>
/// The exception that is thrown when a chat request needs the phone connection and there is none.
/// </summary>
public sealed class MessagingUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MessagingUnavailableException() : base("Not connected to the phone.")
    {
    }
}

/// <summary>
/// The exception that is thrown when the phone answers a send with a status other than success.
/// </summary>
public sealed class MessageSendException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="status">The status that the phone answered with.</param>
    public MessageSendException(string status) : base($"The phone refused the message ({status}).")
    {
        Status = status;
    }

    /// <summary>Gets the status that the phone answered with.</summary>
    public string Status { get; }
}

/// <summary>
/// The exception that is thrown when a request asks for media that is not yet on the phone servers. The media has no
/// media ID, thumbnail or inline data.
/// </summary>
public sealed class MediaPendingException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MediaPendingException() : base("The attachment hasn't arrived yet.")
    {
    }
}

/// <summary>
/// The exception that is thrown when an attachment is over <see cref="ChatStore.MaxAttachmentBytes"/>, before any
/// upload starts.
/// </summary>
public sealed class AttachmentTooBigException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AttachmentTooBigException() : base("The attachment is over the size limit.")
    {
    }
}
