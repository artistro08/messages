using GMessages.Presentation.Shortcuts;
using Windows.System;

namespace GMessages.Presentation.Tests.Shortcuts;

/// <summary>
/// Checks <see cref="ShortcutMap"/> key-to-command mapping and the help rows.
/// </summary>
[TestClass]
public sealed class ShortcutMapTests
{
    [TestMethod]
    [DataRow(VirtualKey.K, true, false, ShortcutCommand.StartChat)]
    [DataRow(VirtualKey.K, true, true, ShortcutCommand.FocusConversationList)]
    [DataRow(VirtualKey.R, false, false, ShortcutCommand.FocusMessageBox)]
    [DataRow(VirtualKey.E, true, true, ShortcutCommand.ToggleEmoji)]
    [DataRow(VirtualKey.J, true, false, ShortcutCommand.FocusMessages)]
    [DataRow(VirtualKey.Escape, false, false, ShortcutCommand.ExitConversation)]
    [DataRow((VirtualKey)190, true, false, ShortcutCommand.NextConversation)]
    [DataRow((VirtualKey)188, true, false, ShortcutCommand.PreviousConversation)]
    [DataRow(VirtualKey.S, true, false, ShortcutCommand.OpenSettings)]
    [DataRow((VirtualKey)191, true, true, ShortcutCommand.ShowHelp)]
    [DataRow((VirtualKey)187, false, true, ShortcutCommand.OpenReactions)]
    [DataRow(VirtualKey.Enter, true, false, ShortcutCommand.ReplyToMessage)]
    [DataRow(VirtualKey.D, true, false, ShortcutCommand.DeleteConversation)]
    [DataRow(VirtualKey.O, true, true, ShortcutCommand.ShowDetails)]
    public void Resolve_MapsKey(VirtualKey key, bool ctrl, bool shift, ShortcutCommand expected)
    {
        Assert.AreEqual(expected, ShortcutMap.Resolve(key, ctrl, shift, false, false));
    }

    [TestMethod]
    public void R_InTextBox_IsNull() => Assert.IsNull(ShortcutMap.Resolve(VirtualKey.R, false, false, false, true));

    [TestMethod]
    public void ShiftPlus_InTextBox_IsNull() => Assert.IsNull(ShortcutMap.Resolve((VirtualKey)187, false, true, false, true));

    [TestMethod]
    public void CtrlEnter_InTextBox_IsNull() => Assert.IsNull(ShortcutMap.Resolve(VirtualKey.Enter, true, false, false, true));

    [TestMethod]
    public void CtrlK_InTextBox_StillStartsChat() =>
        Assert.AreEqual(ShortcutCommand.StartChat, ShortcutMap.Resolve(VirtualKey.K, true, false, false, true));

    [TestMethod]
    public void Escape_InTextBox_StillExits() =>
        Assert.AreEqual(ShortcutCommand.ExitConversation, ShortcutMap.Resolve(VirtualKey.Escape, false, false, false, true));

    /// <summary>
    /// The conversation shortcuts work while typing too, and Ctrl + Alt + h is the only Alt chord.
    /// </summary>
    [TestMethod]
    [DataRow(VirtualKey.D, true, false, false, ShortcutCommand.DeleteConversation)]
    [DataRow(VirtualKey.H, true, false, true, ShortcutCommand.ArchiveConversation)]
    [DataRow(VirtualKey.O, true, true, false, ShortcutCommand.ShowDetails)]
    public void ConversationShortcuts_IgnoreTextFocus(VirtualKey key, bool ctrl, bool shift, bool alt, ShortcutCommand expected)
    {
        Assert.AreEqual(expected, ShortcutMap.Resolve(key, ctrl, shift, alt, false));
        Assert.AreEqual(expected, ShortcutMap.Resolve(key, ctrl, shift, alt, true));
    }

    [TestMethod]
    public void CtrlAltK_IsNotAShortcut() => Assert.IsNull(ShortcutMap.Resolve(VirtualKey.K, true, false, true, false));

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public void AltH_WithoutExactlyCtrl_IsNull(bool ctrl, bool shift) => Assert.IsNull(ShortcutMap.Resolve(VirtualKey.H, ctrl, shift, true, false));

    // Ruling R5-7: AltGr Is Left Ctrl + Right Alt, So With Right Alt Down The Key Is Left To The Layout
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CtrlRightAltH_IsNull(bool focusInTextBox) => Assert.IsNull(ShortcutMap.Resolve(VirtualKey.H, true, false, true, focusInTextBox, rightAlt: true));

    [TestMethod]
    public void CtrlH_WithoutAlt_IsNull() => Assert.IsNull(ShortcutMap.Resolve(VirtualKey.H, true, false, false, false));

    [TestMethod]
    public void Unmapped_IsNull() => Assert.IsNull(ShortcutMap.Resolve(VirtualKey.Z, true, false, false, false));

    [TestMethod]
    public void HelpRows_AreVerbatimSpecText()
    {
        (string, string)[] expected =
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
            ("Ctrl + d", "Delete conversation"),
            ("Ctrl + Alt + h", "Archive conversation"),
            ("Ctrl + s", "Open Settings"),
            ("Ctrl + Shift + o", "Show Details"),
            ("Ctrl + Enter", "Reply to message"),
            ("Ctrl + ?", "Open keyboard shortcut help"),
        ];

        Assert.HasCount(15, expected);
        CollectionAssert.AreEqual(expected, ShortcutMap.HelpRows.ToArray());
    }
}
