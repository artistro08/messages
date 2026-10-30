using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace GMessages.App.UITests;

/// <summary>
/// The first-version keyboard shortcuts in the main window, and the Ctrl + ? help dialog.
/// </summary>
/// <remarks>
/// Each test opens a row and brings the main window to the front first, so it takes the key presses.
/// </remarks>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class ShortcutTests : UiTest
{
    /// <summary>
    /// Ctrl + k opens Start chat with the cursor in its search box.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CtrlK_OpensStartChat()
    {
        await OpenChatAsync("Row.c-team");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);

        App.Find("StartChat.Query");
        WaitForFocusIn("StartChat.Query");
    }

    /// <summary>
    /// "r" from the conversation list moves to the message box without typing; an "r" typed in the box stays as text.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task R_FocusesDraft_NotWhenTyping()
    {
        await OpenChatAsync("Row.c-ann");

        // From The List: Focus Moves, Nothing Typed
        Keyboard.Type("r");
        WaitForFocusIn("Chat.Draft");

        // In The Box: Just Text
        Keyboard.Type("r");
        string last = "";
        Wait.Until(() => (last = DraftText()) == "r", AppDriver.DefaultTimeout, () => $"The message box read '{last}', not 'r'.");
    }

    /// <summary>
    /// Ctrl + . opens the next conversation and Ctrl + , the previous one, and the list's highlight follows.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CtrlPeriod_And_CtrlComma_MoveSelection()
    {
        await OpenChatAsync("Row.c-team");

        // Next: Team To Ann
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.OEM_PERIOD);
        App.WaitForText("Chat.Header", "Ann Lee");
        WaitForSelected("Row.c-ann");

        // Previous: Back To Team
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.OEM_COMMA);
        WaitForSelected("Row.c-team");
        Wait.Until(() => !IsSelected("Row.c-ann"), AppDriver.DefaultTimeout, () => "Ann's row stayed highlighted.");
    }

    /// <summary>
    /// Escape closes the open conversation, back to the empty state with no row highlighted.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Escape_ExitsConversation()
    {
        await OpenChatAsync("Row.c-ann");

        Keyboard.Type(VirtualKeyShort.ESCAPE);

        App.Find("Main.EmptyState");
        Wait.Until(() => App.TryFind("Chat.Draft") is null, AppDriver.DefaultTimeout, () => "The chat stayed open after Escape.");
        Wait.Until(() => !IsSelected("Row.c-ann"), AppDriver.DefaultTimeout, () => "Ann's row stayed highlighted after Escape.");
    }

    /// <summary>
    /// Escape in the message box closes the chat too, and the typed draft comes back when the chat opens again.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Escape_InDraft_ExitsAndKeepsDraft()
    {
        await OpenChatAsync("Row.c-ann");
        App.Find("Chat.Draft").Focus();
        WaitForFocusIn("Chat.Draft");
        Keyboard.Type("keep me");
        Wait.Until(() => DraftText() == "keep me", AppDriver.DefaultTimeout, () => "The draft never read 'keep me'.");

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Wait.Until(() => App.TryFind("Chat.Draft") is null, AppDriver.DefaultTimeout, () => "The chat stayed open after Escape in the message box.");

        // Opened Again: The Draft Is Back, With The Cursor In The Box
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        string last = "";
        Wait.Until(() => (last = DraftText()) == "keep me", AppDriver.DefaultTimeout, () => $"The draft read '{last}' after reopening, not 'keep me'.");
        WaitForFocusIn("Chat.Draft");
    }

    /// <summary>
    /// Arrowing through the conversation list opens each chat but keeps the focus on the list; Enter then moves to the
    /// message box.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ArrowKeys_BrowseList_EnterGoesToDraft()
    {
        await OpenChatAsync("Row.c-team");

        Keyboard.Type(VirtualKeyShort.DOWN);
        App.WaitForText("Chat.Header", "Ann Lee");
        Thread.Sleep(500);
        WaitForFocusIn("Row.c-ann");

        Keyboard.Type(VirtualKeyShort.RETURN);
        WaitForFocusIn("Chat.Draft");
    }

    /// <summary>
    /// Ctrl + s opens the settings page.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CtrlS_OpensSettings()
    {
        await OpenChatAsync("Row.c-team");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S);

        WaitForSettings();
    }

    /// <summary>
    /// Ctrl + ? (Ctrl + Shift + /) opens the help dialog listing the fifteen wired shortcuts, Ctrl + k first.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CtrlQuestion_ShowsHelpWithRows()
    {
        await OpenChatAsync("Row.c-team");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.OEM_2);

        AutomationElement dialog = App.Find("Shortcuts.Dialog");
        int rows = 0;
        Wait.Until(() => (rows = dialog.FindAllDescendants().Count(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("Shortcuts.Keys.", StringComparison.Ordinal))) == 15,
            AppDriver.DefaultTimeout, () => $"The dialog listed {rows} shortcuts, not 15.");
        App.WaitForText("Shortcuts.Keys.0", "Ctrl + k");
        App.WaitForText("Shortcuts.Action.0", "Start new conversation");
    }

    /// <summary>
    /// Ctrl + j moves focus to the messages in the open conversation.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CtrlJ_FocusesMessages()
    {
        await OpenChatAsync("Row.c-ann");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_J);

        WaitForFocusIn("Chat.Messages");
    }

    /// <summary>
    /// Ctrl + Shift + k moves focus from the message box to the conversation list.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CtrlShiftK_FocusesList()
    {
        await OpenChatAsync("Row.c-ann");
        App.Find("Chat.Draft").Focus();
        WaitForFocusIn("Chat.Draft");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_K);

        WaitForFocusIn("Main.ConversationList");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Opens The Main Window And A Row's Chat, Brings The Window To The Front, Checks That Opening The Chat Put The Cursor
    // In The Message Box, Then Leaves The Focus On That Row
    private async Task OpenChatAsync(string rowId)
    {
        await OpenMainAsync();
        App.Find(rowId).Patterns.SelectionItem.Pattern.Select();
        App.Find("Chat.Draft");
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
        WaitForFocusIn("Chat.Draft");
        App.Find(rowId).Focus();
        WaitForFocusIn(rowId);
    }

    private string DraftText() => App.TryFind("Chat.Draft")?.Patterns.Value.Pattern.Value.ValueOrDefault ?? "";

    private bool IsSelected(string rowId) => App.TryFind(rowId)?.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault == true;

    private void WaitForSelected(string rowId) =>
        Wait.Until(() => IsSelected(rowId), AppDriver.DefaultTimeout, () => $"{rowId} wasn't highlighted in the list.");
}
