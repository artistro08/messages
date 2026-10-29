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
    public void Resolve_MapsKey(VirtualKey key, bool ctrl, bool shift, ShortcutCommand expected)
    {
        Assert.AreEqual(expected, ShortcutMap.Resolve(key, ctrl, shift, false, false));
    }

    [TestMethod]
    public void R_InTextBox_IsNull() => Assert.IsNull(ShortcutMap.Resolve(VirtualKey.R, false, false, false, true));

    [TestMethod]
    public void CtrlK_InTextBox_StillStartsChat() =>
        Assert.AreEqual(ShortcutCommand.StartChat, ShortcutMap.Resolve(VirtualKey.K, true, false, false, true));

    [TestMethod]
    public void Escape_InTextBox_StillExits() =>
        Assert.AreEqual(ShortcutCommand.ExitConversation, ShortcutMap.Resolve(VirtualKey.Escape, false, false, false, true));

    [TestMethod]
    public void ExtraModifier_IsNull() => Assert.IsNull(ShortcutMap.Resolve(VirtualKey.K, true, false, true, false));

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
            ("Ctrl + j", "Focus on messages in conversation"),
            ("Escape", "Exit current conversation"),
            ("Ctrl + .", "Next conversation"),
            ("Ctrl + ,", "Previous conversation"),
            ("Ctrl + s", "Open Settings"),
            ("Ctrl + ?", "Open keyboard shortcut help"),
        ];

        CollectionAssert.AreEqual(expected, ShortcutMap.HelpRows.ToArray());
    }
}
