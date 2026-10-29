namespace GMessages.Presentation.Messaging;

/// <summary>
/// Says someone in a conversation started or stopped typing.
/// </summary>
/// <param name="conversationId">The conversation ID.</param>
/// <param name="participantId">Who is typing.</param>
/// <param name="isTyping">True when they started, false when they stopped.</param>
public sealed class TypingEventArgs(string conversationId, string participantId, bool isTyping) : EventArgs
{
    /// <summary>Gets the conversation ID.</summary>
    public string ConversationId { get; } = conversationId;

    /// <summary>Gets the participant ID of who is typing.</summary>
    public string ParticipantId { get; } = participantId;

    /// <summary>Gets a value saying they started (true) or stopped (false) typing.</summary>
    public bool IsTyping { get; } = isTyping;
}
