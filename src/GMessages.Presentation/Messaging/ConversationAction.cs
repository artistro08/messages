namespace GMessages.Presentation.Messaging;

/// <summary>
/// What the owner can do to a whole conversation (see <see cref="ChatStore.UpdateConversationAsync"/>).
/// </summary>
public enum ConversationAction
{
    /// <summary>Move it to the archive.</summary>
    Archive,

    /// <summary>Move it back to the inbox.</summary>
    Unarchive,

    /// <summary>Stop its notifications.</summary>
    Mute,

    /// <summary>Turn its notifications back on.</summary>
    Unmute,

    /// <summary>Block it; it moves to spam and blocked.</summary>
    Block,

    /// <summary>Block it and report it as spam.</summary>
    BlockAndReport,

    /// <summary>Unblock it; it moves back to the inbox.</summary>
    Unblock,

    /// <summary>Move it to the trash, on the phone too.</summary>
    Delete,
}
