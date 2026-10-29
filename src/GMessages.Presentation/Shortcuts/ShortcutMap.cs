using Windows.System;

namespace GMessages.Presentation.Shortcuts;

/// <summary>
/// Maps key presses to <see cref="ShortcutCommand"/>s and lists the wired shortcuts for the help dialog.
/// </summary>
/// <remarks>
/// Key text and action names are the Google Messages shortcut list, kept verbatim from the spec. Only the shortcuts
/// that are wired appear in <see cref="HelpRows"/>.
/// </remarks>
public static class ShortcutMap
{
    // Punctuation keys the Windows.System.VirtualKey enum has no names for (OEM period, comma, slash, plus).
    private const VirtualKey Plus   = (VirtualKey)187;
    private const VirtualKey Period = (VirtualKey)190;
    private const VirtualKey Comma  = (VirtualKey)188;
    private const VirtualKey Slash  = (VirtualKey)191;

    /// <summary>
    /// The wired shortcuts, in the spec's order, as (keys, action) text.
    /// </summary>
    public static IReadOnlyList<(string Keys, string Action)> HelpRows { get; } =
    [
        ("Ctrl + k", "Start new conversation"),
        ("Ctrl + Shift + k", "Focus on conversation list"),
        ("r", "Focus on message box"),
        ("Ctrl + Shift + e", "Show/hide emoji picker"),
        ("Shift + +", "Open reactions selector"),
        ("Ctrl + j", "Focus on messages in conversation"),
        ("Escape", "Exit current conversation"),
        ("Ctrl + .", "Next conversation"),
        ("Ctrl + ,", "Previous conversation"),
        ("Ctrl + s", "Open Settings"),
        ("Ctrl + Enter", "Reply to message"),
        ("Ctrl + ?", "Open keyboard shortcut help"),
    ];

    /// <summary>
    /// Finds the command for a key press, or null when the press is not a shortcut.
    /// </summary>
    /// <remarks>
    /// Chords must match their modifiers exactly, so Ctrl + Alt + k is not a shortcut. The bare "r", Shift + + and
    /// Ctrl + Enter depend on <paramref name="focusInTextBox"/>; they are ignored while typing.
    /// </remarks>
    /// <param name="key">The pressed key.</param>
    /// <param name="ctrl">Whether Ctrl is held.</param>
    /// <param name="shift">Whether Shift is held.</param>
    /// <param name="alt">Whether Alt is held.</param>
    /// <param name="focusInTextBox">Whether a text box has keyboard focus.</param>
    /// <returns>The matching command, or null.</returns>
    public static ShortcutCommand? Resolve(VirtualKey key, bool ctrl, bool shift, bool alt, bool focusInTextBox)
    {
        // Guard Alt, no shortcut uses it
        if (alt)
        {
            return null;
        }

        // Bare Keys
        if (!ctrl && !shift)
        {
            return key switch
            {
                VirtualKey.Escape => ShortcutCommand.ExitConversation,
                VirtualKey.R when !focusInTextBox => ShortcutCommand.FocusMessageBox,
                _ => null,
            };
        }

        // Shift Chords
        if (!ctrl && shift)
        {
            return key == Plus && !focusInTextBox ? ShortcutCommand.OpenReactions : null;
        }

        // Ctrl Chords
        if (ctrl && !shift)
        {
            return key switch
            {
                VirtualKey.Enter when !focusInTextBox => ShortcutCommand.ReplyToMessage,
                VirtualKey.K => ShortcutCommand.StartChat,
                VirtualKey.J => ShortcutCommand.FocusMessages,
                VirtualKey.S => ShortcutCommand.OpenSettings,
                Period       => ShortcutCommand.NextConversation,
                Comma        => ShortcutCommand.PreviousConversation,
                _ => null,
            };
        }

        // Ctrl + Shift Chords
        if (ctrl && shift)
        {
            return key switch
            {
                VirtualKey.K => ShortcutCommand.FocusConversationList,
                VirtualKey.E => ShortcutCommand.ToggleEmoji,
                Slash        => ShortcutCommand.ShowHelp,
                _ => null,
            };
        }

        return null;
    }
}
