namespace GMessages.Presentation.Messaging;

/// <summary>
/// Names what the owner can do to a whole conversation through <see cref="ChatStore.UpdateConversationAsync"/>.
/// </summary>
public enum ConversationAction
{
    /// <summary>Moves the conversation to the archive.</summary>
    Archive,

    /// <summary>Moves the conversation back to the inbox.</summary>
    Unarchive,

    /// <summary>Stops the notifications of the conversation.</summary>
    Mute,

    /// <summary>Turns the notifications of the conversation on again.</summary>
    Unmute,

    /// <summary>Blocks the conversation, which moves it to spam and blocked.</summary>
    Block,

    /// <summary>Blocks the conversation and reports it as spam.</summary>
    BlockAndReport,

    /// <summary>Unblocks the conversation, which moves it back to the inbox.</summary>
    Unblock,

    /// <summary>Moves the conversation to the trash, also on the phone.</summary>
    Delete,
}
