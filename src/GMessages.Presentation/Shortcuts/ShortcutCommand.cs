namespace GMessages.Presentation.Shortcuts;

/// <summary>
/// Specifies an app action that a keyboard shortcut can start.
/// </summary>
public enum ShortcutCommand
{
    /// <summary>Opens the start-a-conversation flow.</summary>
    StartChat,

    /// <summary>Moves focus to the conversation list.</summary>
    FocusConversationList,

    /// <summary>Moves focus to the message box.</summary>
    FocusMessageBox,

    /// <summary>Shows or hides the emoji picker.</summary>
    ToggleEmoji,

    /// <summary>Moves focus to the messages in the open conversation.</summary>
    FocusMessages,

    /// <summary>Closes the open conversation.</summary>
    ExitConversation,

    /// <summary>Opens the next conversation in the list.</summary>
    NextConversation,

    /// <summary>Opens the previous conversation in the list.</summary>
    PreviousConversation,

    /// <summary>Opens Settings.</summary>
    OpenSettings,

    /// <summary>Opens the keyboard shortcut help.</summary>
    ShowHelp,

    /// <summary>Opens the reaction picker on the focused message.</summary>
    OpenReactions,

    /// <summary>Starts a reply to the focused message.</summary>
    ReplyToMessage,

    /// <summary>Asks to move the open conversation to trash.</summary>
    DeleteConversation,

    /// <summary>Archives the open conversation.</summary>
    ArchiveConversation,

    /// <summary>Opens the open conversation's Details dialog.</summary>
    ShowDetails,

    /// <summary>Opens the file picker to attach any files to the open conversation.</summary>
    AttachFiles,

    /// <summary>Opens the file picker with a filter for photos.</summary>
    AttachPhotos,

    /// <summary>Restores the conversation that the user archived last in this session.</summary>
    UndoArchive,

    /// <summary>
    /// Collapses the conversation list to avatars, or expands it again, as the ☰ of the title bar does.
    /// </summary>
    ToggleSidebar,
}
