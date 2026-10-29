namespace GMessages.Presentation.Messaging;

/// <summary>
/// Says a message arrived (or changed status) on the phone.
/// </summary>
/// <param name="Message">The message.</param>
/// <param name="Conversation">The conversation it belongs to, or null when it could not be fetched.</param>
/// <param name="IsOld">True when the phone replayed it while catching up, so it should not notify.</param>
/// <param name="IsNew">False when this message ID was already raised once (a status update).</param>
public sealed class MessageArrivedEventArgs(ChatMessage message, ConversationInfo? conversation, bool isOld, bool isNew) : EventArgs
{
    /// <summary>Gets the message.</summary>
    public ChatMessage Message { get; } = message;

    /// <summary>Gets the conversation it belongs to, or null when it could not be fetched.</summary>
    public ConversationInfo? Conversation { get; } = conversation;

    /// <summary>Gets a value saying the phone replayed the message while catching up.</summary>
    public bool IsOld { get; } = isOld;

    /// <summary>Gets a value saying this is the first time the message ID was raised.</summary>
    public bool IsNew { get; } = isNew;
}
