using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;
using GMessages.Presentation.Shortcuts;
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
    [TestMethod]
    public async Task CtrlK_OpensStartChat()
    {
        await OpenChatAsync("Row.c-team");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);

        App.Find("StartChat.Query");
        WaitForFocusIn("StartChat.Query");
    }

    /// <summary>
    /// "r" from the conversation list moves the focus to the message box and types nothing. An "r" typed in the box
    /// stays as text.
    /// </summary>
    [TestMethod]
    public async Task R_FocusesDraft_NotWhenTyping()
    {
        await OpenChatAsync("Row.c-ann");

        Keyboard.Type("r");
        WaitForFocusIn("Chat.Draft");

        Keyboard.Type("r");
        string last = "";
        Wait.Until(() => (last = DraftText()) == "r", AppDriver.DefaultTimeout, () => $"The message box read '{last}', not 'r'.");
    }

    /// <summary>
    /// Ctrl + . opens the next conversation and Ctrl + Shift + , opens the previous one. The list highlight follows.
    /// </summary>
    [TestMethod]
    public async Task CtrlPeriod_And_CtrlShiftComma_MoveSelection()
    {
        await OpenChatAsync("Row.c-team");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.OEM_PERIOD);
        App.WaitForText("Chat.Header", "Ann Lee");
        WaitForSelected("Row.c-ann");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.OEM_COMMA);
        WaitForSelected("Row.c-team");
        Wait.Until(() => !IsSelected("Row.c-ann"), AppDriver.DefaultTimeout, () => "Ann's row stayed highlighted.");
    }

    /// <summary>
    /// Escape closes the open conversation. The empty state shows and no row stays highlighted.
    /// </summary>
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
    /// Escape in the message box closes the chat too. The typed draft returns when the chat opens again.
    /// </summary>
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

        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        string last = "";
        Wait.Until(() => (last = DraftText()) == "keep me", AppDriver.DefaultTimeout, () => $"The draft read '{last}' after reopening, not 'keep me'.");
        WaitForFocusIn("Chat.Draft");
    }

    /// <summary>
    /// The arrow keys in the conversation list open each chat but keep the focus on the list. Enter then moves the
    /// focus to the message box.
    /// </summary>
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
    /// Ctrl + , opens the settings page.
    /// </summary>
    [TestMethod]
    public async Task CtrlComma_OpensSettings()
    {
        await OpenChatAsync("Row.c-team");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.OEM_COMMA);

        WaitForSettings();
    }

    /// <summary>
    /// Ctrl + s collapses the conversation list to avatars and expands it again, as the ☰ does.
    /// </summary>
    [TestMethod]
    public async Task CtrlS_TogglesSidebar()
    {
        await OpenChatAsync("Row.c-team");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S);
        Wait.Until(() => App.TryFind("Main.ConversationList") is null, AppDriver.DefaultTimeout, () => "Ctrl + s didn't collapse the list.");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S);
        App.Find("Main.ConversationList");
    }

    /// <summary>
    /// Ctrl + Shift + /, which is Ctrl + ?, opens the help dialog with every wired shortcut.
    /// Start new conversation is first, and its keys show as the caps Ctrl and K.
    /// </summary>
    [TestMethod]
    public async Task CtrlQuestion_ShowsHelpWithRows()
    {
        await OpenChatAsync("Row.c-team");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.OEM_2);

        AutomationElement dialog = App.Find("Shortcuts.Dialog");
        int rows = 0;
        int expected = ShortcutMap.HelpRows.Count;
        Wait.Until(() => (rows = dialog.FindAllDescendants().Count(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("Shortcuts.Action.", StringComparison.Ordinal))) == expected,
            AppDriver.DefaultTimeout, () => $"The dialog listed {rows} shortcuts, not {expected}.");
        App.WaitForText("Shortcuts.Action.0", "Start new conversation");
        App.WaitForText("Shortcuts.Key.0.0", "Ctrl");
        App.WaitForText("Shortcuts.Key.0.1", "K");
    }

    /// <summary>
    /// The help dialog has no button row. Its X closes it, and so does a click on the dimmed window around it. A click
    /// on the dialog itself does not close it.
    /// </summary>
    [TestMethod]
    public async Task HelpDialog_ClosesByXOrDim_NotByCard()
    {
        await OpenChatAsync("Row.c-team");
        Window window = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.OEM_2);
        Thread.Sleep(500);
        System.Drawing.Rectangle first = App.Find("Shortcuts.Close").BoundingRectangle;

        // On the first open, the X sits in the corner. Its right edge is past the last key cap of the first row.
        // The X once sat beside the heading until the dialog had opened before.
        System.Drawing.Rectangle lastCap = App.Find("Shortcuts.Key.0.1").BoundingRectangle;
        Assert.IsGreaterThanOrEqualTo(lastCap.Right - 4, first.Right, $"The X sat at {first.Right}, short of the corner ({lastCap.Right}).");
        App.Find("Shortcuts.Close").Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Shortcuts.Close");

        // The title row must stay at the same place on a second open. It once rose higher on each open.
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.OEM_2);
        Thread.Sleep(500);
        System.Drawing.Rectangle close = App.Find("Shortcuts.Close").BoundingRectangle;
        Assert.AreEqual(first.Y, close.Y, "The X moved between opens.");

        // A click on the heading of the card leaves the dialog open. A click on the dim area closes it.
        // The dim area is the left edge of the window, beside the card.
        System.Drawing.Rectangle win   = window.BoundingRectangle;
        FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(close.X - 120, close.Y + (close.Height / 2)));
        Thread.Sleep(500);
        App.Find("Shortcuts.Close");

        FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(win.X + 40, win.Y + (win.Height / 2)));
        WaitForGone("Shortcuts.Close");
    }

    /// <summary>
    /// Ctrl + j moves the focus to the messages in the open conversation.
    /// </summary>
    [TestMethod]
    public async Task CtrlJ_FocusesMessages()
    {
        await OpenChatAsync("Row.c-ann");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_J);

        WaitForFocusIn("Chat.Messages");
    }

    /// <summary>
    /// Ctrl + Shift + k moves the focus from the message box to the conversation list.
    /// </summary>
    [TestMethod]
    public async Task CtrlShiftK_FocusesList()
    {
        await OpenChatAsync("Row.c-ann");
        App.Find("Chat.Draft").Focus();
        WaitForFocusIn("Chat.Draft");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_K);

        WaitForFocusIn("Main.ConversationList");
    }

    // Opens the main window and the chat of one row, and brings the window to the front.
    // Opening the chat must put the cursor in the message box. The method then leaves the focus on the row.
    private async Task OpenChatAsync(string rowId)
    {
        await OpenMainAsync();
        App.Find(rowId).Patterns.SelectionItem.Pattern.Select();
        App.Find("Chat.Draft");
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
        WaitForFocusIn("Chat.Draft");
        App.Find(rowId).Focus();
        WaitForFocusIn(rowId);
    }

    private string DraftText() => App.TryFind("Chat.Draft")?.Patterns.Value.Pattern.Value.ValueOrDefault ?? "";

    private bool IsSelected(string rowId) => App.TryFind(rowId)?.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault == true;

    private void WaitForSelected(string rowId) =>
        Wait.Until(() => IsSelected(rowId), AppDriver.DefaultTimeout, () => $"{rowId} wasn't highlighted in the list.");
}
