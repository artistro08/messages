using Windows.System;

namespace GMessages.Presentation.Shortcuts;

/// <summary>
/// Maps key presses to <see cref="ShortcutCommand"/>s and lists the wired shortcuts for the help dialog.
/// </summary>
/// <remarks>
/// The key text and the action names come from the Google Messages shortcut list, with the exact words of the spec.
/// Only the wired shortcuts appear in <see cref="HelpRows"/>.
/// </remarks>
public static class ShortcutMap
{
    // The VirtualKey enum has no names for these OEM keys.
    private const VirtualKey Plus   = (VirtualKey)187;
    private const VirtualKey Period = (VirtualKey)190;
    private const VirtualKey Comma  = (VirtualKey)188;
    private const VirtualKey Slash  = (VirtualKey)191;

    /// <summary>
    /// Gets the wired shortcuts in the order of the spec, as text pairs of keys and action.
    /// </summary>
    public static IReadOnlyList<(string Keys, string Action)> HelpRows { get; } =
    [
        ("Ctrl + k", "Start new conversation"),
        ("Ctrl + Shift + k", "Focus on conversation list"),
        ("r", "Focus on message box"),
        ("Ctrl + Shift + a", "Attach files"),
        ("Ctrl + Shift + e", "Show/hide emoji picker"),
        ("Ctrl + Shift + p", "Show/hide photos picker"),
        ("Shift + +", "Open reactions selector"),
        ("Ctrl + j", "Focus on messages in conversation"),
        ("Escape", "Exit current conversation"),
        ("Ctrl + .", "Next conversation"),
        ("Ctrl + Shift + ,", "Previous conversation"),
        ("Ctrl + d", "Delete conversation"),
        ("Ctrl + Alt + h", "Archive conversation"),
        ("Ctrl + ,", "Open Settings"),
        ("Ctrl + s", "Show/hide sidebar"),
        ("Ctrl + Shift + o", "Show Details"),
        ("Ctrl + Enter", "Reply to message"),
        ("Ctrl + ?", "Open keyboard shortcut help"),
        ("Ctrl + Shift + z", "Undo archive"),
    ];

    /// <summary>
    /// Finds the command for a key press, or null when the press is not a shortcut.
    /// </summary>
    /// <remarks>
    /// A chord must match its modifiers exactly, so Ctrl + Alt + k is not a shortcut. The bare "r", Shift + + and
    /// Ctrl + Enter depend on <paramref name="focusInTextBox"/>. The method ignores them while the user types. The
    /// conversation shortcuts Ctrl + d, Ctrl + Alt + h and Ctrl + Shift + o also work while the user types. In the
    /// app, Ctrl + Shift + z undoes an archive, as the owner chose. While the user types, the method leaves the keys to
    /// the text box. Ctrl + ? is Ctrl + Shift + /. Ctrl + / also opens the help and has no help row of its own.
    /// Ctrl + Alt + h is not a shortcut while Right Alt is down. Windows reports AltGr as Left Ctrl + Right Alt, and
    /// AltGr + h types a character on some layouts.
    /// </remarks>
    /// <param name="key">The pressed key.</param>
    /// <param name="ctrl">Whether Ctrl is held.</param>
    /// <param name="shift">Whether Shift is held.</param>
    /// <param name="alt">Whether Alt is held.</param>
    /// <param name="focusInTextBox">Whether a text box has keyboard focus.</param>
    /// <param name="rightAlt">Whether the Right Alt key (AltGr) is held.</param>
    /// <returns>The matching command, or null.</returns>
    public static ShortcutCommand? Resolve(VirtualKey key, bool ctrl, bool shift, bool alt, bool focusInTextBox, bool rightAlt)
    {
        if (alt)
        {
            return ctrl && !shift && !rightAlt && key == VirtualKey.H ? ShortcutCommand.ArchiveConversation : null;
        }

        if (!ctrl && !shift)
        {
            return key switch
            {
                VirtualKey.Escape                 => ShortcutCommand.ExitConversation,
                VirtualKey.R when !focusInTextBox => ShortcutCommand.FocusMessageBox,
                _                                 => null,
            };
        }

        if (!ctrl && shift)
        {
            return key == Plus && !focusInTextBox ? ShortcutCommand.OpenReactions : null;
        }

        if (ctrl && !shift)
        {
            return key switch
            {
                VirtualKey.Enter when !focusInTextBox => ShortcutCommand.ReplyToMessage,
                VirtualKey.K                          => ShortcutCommand.StartChat,
                VirtualKey.J                          => ShortcutCommand.FocusMessages,
                VirtualKey.S                          => ShortcutCommand.ToggleSidebar,
                VirtualKey.D                          => ShortcutCommand.DeleteConversation,
                Period                                => ShortcutCommand.NextConversation,
                Comma                                 => ShortcutCommand.OpenSettings,
                Slash                                 => ShortcutCommand.ShowHelp,
                _                                     => null,
            };
        }

        if (ctrl && shift)
        {
            return key switch
            {
                VirtualKey.Z when !focusInTextBox => ShortcutCommand.UndoArchive,
                VirtualKey.K                      => ShortcutCommand.FocusConversationList,
                VirtualKey.E                      => ShortcutCommand.ToggleEmoji,
                VirtualKey.O                      => ShortcutCommand.ShowDetails,
                VirtualKey.A                      => ShortcutCommand.AttachFiles,
                VirtualKey.P                      => ShortcutCommand.AttachPhotos,
                Comma                             => ShortcutCommand.PreviousConversation,
                Slash                             => ShortcutCommand.ShowHelp,
                _                                 => null,
            };
        }

        return null;
    }
}
