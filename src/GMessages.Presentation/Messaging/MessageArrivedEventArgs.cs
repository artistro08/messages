namespace GMessages.Presentation.Messaging;

/// <summary>
/// Carries a message that arrived on the phone or changed status.
/// </summary>
/// <param name="conversation">The conversation of the message, or null when the store could not get it.</param>
/// <param name="isOld">True when the phone sent the message again during a catch-up, so it must not notify.</param>
/// <param name="isNew">True for a new message: a first raise, under 1 day old, and not an echo of an update.</param>
public sealed class MessageArrivedEventArgs(ChatMessage message, ConversationInfo? conversation, bool isOld, bool isNew) : EventArgs
{
    /// <summary>Gets the message.</summary>
    public ChatMessage Message { get; } = message;

    /// <summary>Gets the conversation of the message, or null when the store could not get it.</summary>
    public ConversationInfo? Conversation { get; } = conversation;

    /// <summary>Gets whether the phone sent the message again during a catch-up.</summary>
    public bool IsOld { get; } = isOld;

    /// <summary>
    /// Gets whether the message is new. A new message has its first raise, is under 1 day old, and is not an echo.
    /// </summary>
    public bool IsNew { get; } = isNew;
}
