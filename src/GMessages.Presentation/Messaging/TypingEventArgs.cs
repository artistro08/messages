namespace GMessages.Presentation.Messaging;

/// <summary>
/// Tells that a person in a conversation started or stopped typing.
/// </summary>
/// <param name="conversationId">The conversation ID.</param>
/// <param name="participantId">The participant ID of the person who types.</param>
/// <param name="isTyping">True when the person started, false when the person stopped.</param>
public sealed class TypingEventArgs(string conversationId, string participantId, bool isTyping) : EventArgs
{
    /// <summary>Gets the conversation ID.</summary>
    public string ConversationId { get; } = conversationId;

    /// <summary>Gets the participant ID of the person who types.</summary>
    public string ParticipantId { get; } = participantId;

    /// <summary>Gets whether the person started typing: true when started, false when stopped.</summary>
    public bool IsTyping { get; } = isTyping;
}
