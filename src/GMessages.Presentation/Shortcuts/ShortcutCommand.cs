namespace GMessages.Presentation.Shortcuts;

/// <summary>
/// An app action a keyboard shortcut can trigger.
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
}
