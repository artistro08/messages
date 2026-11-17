using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.App.UITests.FakeGoogle;
using GMessages.Presentation;
using GMessages.Presentation.Shortcuts;
using GMessages.Protocol.Proto;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace GMessages.App.UITests;

/// <summary>
/// The first-version keyboard shortcuts in the main window, and the Ctrl + ? help dialog.
/// </summary>
/// <remarks>
/// Most tests open a row and bring the main window to the front first, so it takes the key presses. The "right after
/// start" tests click nothing first. The window must take the keys with no focused element.
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
    /// A window that opens narrow right after the app starts takes Ctrl + 2 with no click first. The 2nd conversation
    /// opens.
    /// </summary>
    [TestMethod]
    public async Task CtrlNumber_RightAfterStart_NarrowWindow_OpensConversation()
    {
        await OpenMainAsync();
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).Patterns.Transform.Pattern.Resize(600, 700);
        WaitForGone("Main.MenuButton");
        CloseMainWindow();
        await RestartToFrontAsync();
        WaitForGone("Main.MenuButton");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_2);

        App.WaitForText("Chat.Header", "Ann Lee");
    }

    /// <summary>
    /// A click on a blank part of the window, with nothing to focus there, still lets Ctrl + 1 open the 1st
    /// conversation.
    /// </summary>
    [TestMethod]
    public async Task CtrlNumber_AfterClickOnBlankArea_OpensConversation()
    {
        await OpenMainAsync();
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
        App.Find("Main.EmptyState").Click();
        Thread.Sleep(500);

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);

        WaitForSelected("Row.c-team");
        App.WaitForText("Chat.Header", "Team");
    }

    /// <summary>
    /// After a click on a blank part of the window, Down puts the focus on the list.
    /// </summary>
    [TestMethod]
    public async Task Down_AfterClickOnBlankArea_FocusesList()
    {
        await OpenMainAsync();
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
        App.Find("Main.EmptyState").Click();
        Thread.Sleep(500);

        Press(VirtualKeyShort.DOWN);

        WaitForFocusIn("Row.c-team");
    }

    /// <summary>
    /// A narrow window right after the app starts takes Down with no click first. The focus goes to the 1st row, and
    /// no chat opens.
    /// </summary>
    [TestMethod]
    public async Task Down_RightAfterStart_NarrowWindow_FocusesListOnly()
    {
        await OpenMainAsync();
        NarrowWindow();
        CloseMainWindow();
        await RestartToFrontAsync();
        WaitForGone("Main.MenuButton");

        Press(VirtualKeyShort.DOWN);

        WaitForFocusIn("Row.c-team");
        Thread.Sleep(500);
        Assert.IsNull(App.TryFind("Chat.Header"), "Down opened a chat in a narrow window.");
    }

    /// <summary>
    /// A wide window right after the app starts takes Ctrl + . with no click first. The 1st conversation opens.
    /// </summary>
    [TestMethod]
    public async Task CtrlPeriod_RightAfterStart_OpensFirstConversation()
    {
        await OpenMainAsync();
        await RestartToFrontAsync();

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.OEM_PERIOD);

        WaitForSelected("Row.c-team");
        App.WaitForText("Chat.Header", "Team");
    }

    /// <summary>
    /// In a narrow window, Escape leaves a pinned chat and the focus goes to its tile. The Right key moves the focus
    /// to the next tile and opens nothing. Enter then opens that tile.
    /// </summary>
    [TestMethod]
    public async Task RightKey_PinnedGrid_MovesFocusOnly_EnterOpens()
    {
        AddChat("c-max", "Max Pal", "+15550000007", pinned: true);
        await OpenMainAsync();
        ShowPinnedGrid();
        NarrowWindow();
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        App.Find("Chat.Header");
        Press(VirtualKeyShort.ESCAPE);
        WaitForGone("Chat.Header");
        AutomationElement[] tiles = App.Find("Main.PinnedGrid").FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem));
        Assert.AreEqual(2, tiles.Length, "The grid didn't show 2 tiles.");
        PointerInput.Hover(App.Find("Main.ConversationList").BoundingRectangle);
        Wait.Until(() => tiles[0].Properties.HasKeyboardFocus.ValueOrDefault, AppDriver.DefaultTimeout, () => "Escape didn't put the focus on the tile of the chat.");

        // The list comes back as the chat closes, and the 2nd tile can still move. The read of the 1st tile gives it
        // time. The corner of the 1st tile does not move.
        System.Drawing.Rectangle first    = FillSpot(tiles[0].BoundingRectangle);
        System.Drawing.Color firstFocused = SettledPixel(first);
        System.Drawing.Rectangle second   = FillSpot(tiles[1].BoundingRectangle);
        System.Drawing.Color secondRest   = SettledPixel(second);

        Press(VirtualKeyShort.RIGHT);

        Wait.Until(() => tiles[1].Properties.HasKeyboardFocus.ValueOrDefault, AppDriver.DefaultTimeout, () => "The Right key didn't move the focus to the next tile.");
        System.Drawing.Color firstRest     = SettledPixel(first);
        System.Drawing.Color secondFocused = SettledPixel(second);
        Assert.IsNull(App.TryFind("Chat.Header"), "The Right key opened a chat.");
        TestContext.WriteLine($"Tiles: 1st focused {firstFocused}, at rest {firstRest}; 2nd focused {secondFocused}, at rest {secondRest}.");
        Assert.IsFalse(Near(firstFocused, firstRest), $"The focused 1st tile showed no fill: {firstFocused} focused, {firstRest} at rest.");
        Assert.IsFalse(Near(secondFocused, secondRest), $"The focused 2nd tile showed no fill: {secondFocused} focused, {secondRest} at rest.");
        Press(VirtualKeyShort.ENTER);
        App.Find("Chat.Header");
    }

    /// <summary>
    /// With the pinned grid on, Down on a tile goes to the first row and opens it. Up and Down open the rows. Escape
    /// puts the focus on the row of the chat, not on the first tile. Up on the first row goes to the grid and opens
    /// nothing.
    /// </summary>
    [TestMethod]
    public async Task ArrowKeys_PinnedGridOn_MoveBetweenGridAndRows()
    {
        AddChat("c-max", "Max Pal", "+15550000007", pinned: true);
        AddChat("c-zoe", "Zoe Ray", "+15550000008", pinned: false);
        await OpenMainAsync();
        ShowPinnedGrid();
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
        App.Find("Row.c-team").Patterns.SelectionItem.Pattern.Select();
        WaitForFocusIn("Chat.Draft");
        Press(VirtualKeyShort.ESCAPE);
        WaitForFocusIn("Row.c-team");

        Press(VirtualKeyShort.DOWN);
        App.WaitForText("Chat.Header", "Ann Lee");
        WaitForFocusIn("Row.c-ann");

        Press(VirtualKeyShort.DOWN);
        App.WaitForText("Chat.Header", "Zoe Ray");
        WaitForFocusIn("Row.c-zoe");

        Press(VirtualKeyShort.ESCAPE);
        WaitForGone("Chat.Header");
        WaitForFocusIn("Row.c-zoe");

        Press(VirtualKeyShort.UP);
        App.WaitForText("Chat.Header", "Ann Lee");
        WaitForFocusIn("Row.c-ann");

        Press(VirtualKeyShort.UP);
        WaitForFocusIn("Main.PinnedGrid");
        App.WaitForText("Chat.Header", "Ann Lee");
    }

    /// <summary>
    /// With the pinned grid on in a wide window, Ctrl + 1 and Escape still work after the arrow keys moved through
    /// the rows and into the grid.
    /// </summary>
    [TestMethod]
    public async Task ArrowKeys_PinnedGridOn_ThenCtrlNumberAndEscape_Work()
    {
        AddChat("c-max", "Max Pal", "+15550000007", pinned: true);
        AddChat("c-zoe", "Zoe Ray", "+15550000008", pinned: false);
        await OpenMainAsync();
        ShowPinnedGrid();
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        App.WaitForText("Chat.Header", "Team");
        Press(VirtualKeyShort.ESCAPE);
        WaitForGone("Chat.Header");

        // The focus is on a row after the arrow keys.
        Press(VirtualKeyShort.DOWN);
        App.WaitForText("Chat.Header", "Ann Lee");
        Press(VirtualKeyShort.DOWN);
        App.WaitForText("Chat.Header", "Zoe Ray");
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        App.WaitForText("Chat.Header", "Team");
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_4);
        App.WaitForText("Chat.Header", "Zoe Ray");
        Press(VirtualKeyShort.ESCAPE);
        WaitForGone("Chat.Header");

        // The focus is on a tile after the arrow keys.
        Press(VirtualKeyShort.UP);
        App.WaitForText("Chat.Header", "Ann Lee");
        Press(VirtualKeyShort.UP);
        WaitForFocusIn("Main.PinnedGrid");
        Press(VirtualKeyShort.RIGHT);
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_4);
        App.WaitForText("Chat.Header", "Zoe Ray");
        Press(VirtualKeyShort.ESCAPE);
        WaitForGone("Chat.Header");

        Assert.IsFalse(ReadLog().Contains("stopped by an open popup", StringComparison.Ordinal), "A popup stopped a shortcut.");
    }

    /// <summary>
    /// With the pinned grid on in a narrow window, Up on the first row goes to the first tile of the bottom row, the
    /// tile above the row. It does not go back to the last tile that had the focus.
    /// </summary>
    [TestMethod]
    public async Task UpOnFirstRow_PinnedGridOn_GoesToTileAbove()
    {
        AddChat("c-max", "Max Pal", "+15550000007", pinned: true);
        await OpenMainAsync();
        ShowPinnedGrid();
        NarrowWindow();
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        App.WaitForText("Chat.Header", "Team");
        Press(VirtualKeyShort.ESCAPE);
        WaitForFocusIn("Row.c-team");
        Press(VirtualKeyShort.RIGHT);
        WaitForFocusIn("Row.c-max");
        Press(VirtualKeyShort.DOWN);
        WaitForFocusIn("Row.c-ann");

        Press(VirtualKeyShort.UP);
        WaitForFocusIn("Row.c-team");
    }

    /// <summary>
    /// Ctrl + 1 opens a chat and the focus goes to the message box. An arrow key there does not stop the next Ctrl
    /// shortcuts.
    /// </summary>
    [TestMethod]
    [DataRow(VirtualKeyShort.DOWN)]
    [DataRow(VirtualKeyShort.UP)]
    [DataRow(VirtualKeyShort.LEFT)]
    [DataRow(VirtualKeyShort.RIGHT)]
    public async Task CtrlNumber_ThenArrowKey_CtrlShortcutsStillWork(VirtualKeyShort arrow)
    {
        AddChat("c-max", "Max Pal", "+15550000007", pinned: true);
        await OpenMainAsync();
        ShowPinnedGrid();
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        App.WaitForText("Chat.Header", "Team");
        WaitForFocusIn("Chat.Draft");

        Press(arrow);
        Thread.Sleep(500);
        AutomationElement? focused = App.Automation.FocusedElement();
        TestContext.WriteLine($"After the arrow key: focus on '{focused?.Properties.AutomationId.ValueOrDefault}' ({focused?.Properties.ControlType.ValueOrDefault}).");

        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_2);
        App.WaitForText("Chat.Header", "Max Pal");
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_3);
        App.WaitForText("Chat.Header", "Ann Lee");
    }

    /// <summary>
    /// With the pinned grid on in a narrow window, the arrow keys move the focus between the tiles and the rows and
    /// open no chat. Enter opens the chat with the focus. Escape, Ctrl + 1 and Ctrl + 3 work after that.
    /// </summary>
    [TestMethod]
    public async Task ArrowKeys_NarrowWindow_PinnedGridOn_OpenNothing_EnterOpens()
    {
        AddChat("c-max", "Max Pal", "+15550000007", pinned: true);
        AddChat("c-zoe", "Zoe Ray", "+15550000008", pinned: false);
        await OpenMainAsync();
        ShowPinnedGrid();
        NarrowWindow();
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        App.WaitForText("Chat.Header", "Team");
        Press(VirtualKeyShort.ESCAPE);
        WaitForGone("Chat.Header");

        Press(VirtualKeyShort.DOWN);
        WaitForFocusIn("Row.c-ann");
        Press(VirtualKeyShort.DOWN);
        WaitForFocusIn("Row.c-zoe");
        Press(VirtualKeyShort.UP);
        WaitForFocusIn("Row.c-ann");
        Press(VirtualKeyShort.UP);
        WaitForFocusIn("Row.c-team");
        Press(VirtualKeyShort.DOWN);
        WaitForFocusIn("Row.c-ann");
        Thread.Sleep(500);
        Assert.IsNull(App.TryFind("Chat.Header"), "An arrow key opened a chat in a narrow window.");

        Press(VirtualKeyShort.DOWN);
        WaitForFocusIn("Row.c-zoe");
        Press(VirtualKeyShort.ENTER);
        App.WaitForText("Chat.Header", "Zoe Ray");
        Press(VirtualKeyShort.ESCAPE);
        WaitForFocusIn("Row.c-zoe");
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        App.WaitForText("Chat.Header", "Team");
        Press(VirtualKeyShort.ESCAPE);
        WaitForGone("Chat.Header");
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_3);
        App.WaitForText("Chat.Header", "Ann Lee");
    }

    /// <summary>
    /// The app starts with a narrow window and the pinned grid on, and nothing is clicked. Ctrl + 1 opens the first
    /// tile and Escape leaves it. Down and Enter then open the first row. Ctrl + 1 and Escape still work. The log tells
    /// where Escape and Ctrl + 1 went.
    /// </summary>
    [TestMethod]
    public async Task RightAfterStart_NarrowWindow_DownIntoRows_ThenShortcutsWork()
    {
        AddChat("c-max", "Max Pal", "+15550000007", pinned: true);
        AddChat("c-zoe", "Zoe Ray", "+15550000008", pinned: false);
        await OpenMainAsync();
        ShowPinnedGrid();
        NarrowWindow();
        CloseMainWindow();
        await RestartToFrontAsync();
        WaitForGone("Main.MenuButton");
        App.Find("Main.PinnedGrid");

        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        App.WaitForText("Chat.Header", "Team");
        Press(VirtualKeyShort.ESCAPE);
        WaitForGone("Chat.Header");
        Press(VirtualKeyShort.DOWN);
        WaitForFocusIn("Row.c-ann");
        Press(VirtualKeyShort.ENTER);
        App.WaitForText("Chat.Header", "Ann Lee");
        WaitForFocusIn("Chat.Draft");

        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        App.WaitForText("Chat.Header", "Team");
        Press(VirtualKeyShort.ESCAPE);
        WaitForGone("Chat.Header");
        Press(VirtualKeyShort.DOWN);
        WaitForFocusIn("Row.c-ann");
        Press(VirtualKeyShort.ENTER);
        App.WaitForText("Chat.Header", "Ann Lee");
        Press(VirtualKeyShort.ESCAPE);
        WaitForGone("Chat.Header");
    }

    /// <summary>
    /// In a narrow window the chat covers the list. Down and Enter open the next chat and the focus goes to the
    /// message box. The focus ring of Windows never shows on Back.
    /// </summary>
    [TestMethod]
    public async Task ArrowKeys_NarrowWindow_FocusDraft_NoRingOnBack()
    {
        await OpenMainAsync();
        NarrowWindow();
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        System.Drawing.Rectangle back = App.Find("Chat.Back").BoundingRectangle;
        Press(VirtualKeyShort.ESCAPE);
        WaitForFocusIn("Row.c-team");

        Thread.Sleep(500);

        AssertNoRingAtBack(back, () =>
        {
            Press(VirtualKeyShort.DOWN);
            WaitForFocusIn("Row.c-ann");
            Press(VirtualKeyShort.ENTER);
        });

        App.WaitForText("Chat.Header", "Ann Lee");
        WaitForFocusIn("Chat.Draft");
    }

    /// <summary>
    /// The app starts in a narrow window, and a click on the blank list activates it. The first Ctrl + 1 opens the chat
    /// with the focus in the message box. The focus ring of Windows never shows on Back.
    /// </summary>
    [TestMethod]
    public async Task CtrlNumber_RightAfterStart_NarrowWindow_NoRingOnBack()
    {
        await OpenMainAsync();
        NarrowWindow();
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        System.Drawing.Rectangle back = App.Find("Chat.Back").BoundingRectangle;
        Press(VirtualKeyShort.ESCAPE);
        WaitForGone("Chat.Header");
        CloseMainWindow();
        await RestartToFrontAsync();
        WaitForGone("Main.MenuButton");

        // The click lands on the blank list under the last row, as the owner did to activate the window.
        System.Drawing.Rectangle last = App.Find("Row.c-ann").BoundingRectangle;
        FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(last.Left + (last.Width / 2), last.Bottom + 120));
        Thread.Sleep(500);

        AssertNoRingAtBack(back, () => Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1));

        App.WaitForText("Chat.Header", "Team");
        WaitForFocusIn("Chat.Draft");
    }

    // Runs the keys and reads the 6 px at the left of Back for 1 s after them. The ring of Back is outside the button.
    // Those pixels show the title bar, with or without Back.
    private void AssertNoRingAtBack(System.Drawing.Rectangle back, Action keys)
    {
        System.Drawing.Rectangle strip = new(back.Left - 6, back.Top + (back.Height / 2), 6, 1);
        int[] before   = Reds(strip);
        int most       = 0;
        int readings   = 0;
        using var done = new ManualResetEventSlim();
        var sampler    = new Thread(() =>
        {
            while (!done.IsSet)
            {
                int[] now = Reds(strip);
                most      = Math.Max(most, Enumerable.Range(0, now.Length).Max(i => Math.Abs(now[i] - before[i])));
                readings++;
                Thread.Sleep(1);
            }
        });
        sampler.Start();
        keys();
        Thread.Sleep(1000);
        done.Set();
        sampler.Join();

        TestContext.WriteLine($"Back: {readings} readings, most change {most}.");
        Assert.IsTrue(readings >= 30, $"Only {readings} readings in 1 s; too slow to see a short flash.");
        Assert.IsTrue(most <= 8, $"The focus ring showed at Back: a pixel at its left changed by {most}.");

        static int[] Reds(System.Drawing.Rectangle strip)
        {
            using FlaUI.Core.Capturing.CaptureImage capture = FlaUI.Core.Capturing.Capture.Rectangle(strip);
            return [.. Enumerable.Range(0, strip.Width).Select(x => (int)capture.Bitmap.GetPixel(x, 0).R)];
        }
    }

    /// <summary>
    /// Up in the message box goes to the newest message, and Down there comes back to the box. Up on the second line of
    /// the box moves the cursor first.
    /// </summary>
    [TestMethod]
    public async Task UpInMessageBox_GoesToNewestMessage_DownComesBack()
    {
        await OpenChatAsync("Row.c-team");
        Press(VirtualKeyShort.ENTER);
        WaitForFocusIn("Chat.Draft");
        FlaUI.Core.Input.Keyboard.Type("hi");
        Press(VirtualKeyShort.SHIFT, VirtualKeyShort.RETURN);
        FlaUI.Core.Input.Keyboard.Type("there");

        Press(VirtualKeyShort.UP);
        Thread.Sleep(300);
        WaitForFocusIn("Chat.Draft");

        Press(VirtualKeyShort.UP);
        WaitForFocusIn("Chat.Messages");

        Press(VirtualKeyShort.DOWN);
        WaitForFocusIn("Chat.Draft");
        Assert.IsTrue(DraftText().StartsWith("hi", StringComparison.Ordinal) && DraftText().EndsWith("there", StringComparison.Ordinal),
            $"The draft changed to '{DraftText()}'.");
    }

    /// <summary>
    /// Escape leaves a chat and the focus goes to its row. The row shows a fill until the focus moves to the next row.
    /// The fill of a pointer over the row goes with the pointer.
    /// </summary>
    [TestMethod]
    public async Task Escape_FocusedRow_ShowsFill_UntilFocusLeaves()
    {
        await OpenChatAsync("Row.c-team");
        System.Drawing.Rectangle row  = App.Find("Row.c-team").BoundingRectangle;
        System.Drawing.Rectangle spot = FillSpot(row);
        App.Find("Chat.Draft").Focus();
        WaitForFocusIn("Chat.Draft");

        Press(VirtualKeyShort.ESCAPE);
        PointerInput.Hover(App.Find("Main.EmptyState").BoundingRectangle);
        WaitForFocusIn("Row.c-team");
        System.Drawing.Color focused = SettledPixel(spot);

        Press(VirtualKeyShort.DOWN);
        App.WaitForText("Chat.Header", "Ann Lee");
        System.Drawing.Color rest = SettledPixel(spot);

        PointerInput.Hover(row);
        System.Drawing.Color hover = SettledPixel(spot);
        PointerInput.Hover(App.Find("Chat.Header").BoundingRectangle);
        System.Drawing.Color left = SettledPixel(spot);

        TestContext.WriteLine($"Row: focused {focused}, at rest {rest}, hover {hover}, pointer gone {left}.");
        Assert.IsFalse(Near(focused, rest), $"The focused row showed no fill: {focused} focused, {rest} at rest.");
        Assert.IsFalse(Near(hover, rest), $"The row under the pointer showed no fill: {hover} hover, {rest} at rest.");
        Assert.IsTrue(Near(left, rest), $"The hover fill stayed on the row: {left} with the pointer gone, {rest} at rest.");
    }

    // A pixel near the top left corner of a row or tile. It is inside the round corner of the fill, also at 200 %
    // scale, and clear of the picture and text.
    private static System.Drawing.Rectangle FillSpot(System.Drawing.Rectangle item) => new(item.Left + 12, item.Top + 8, 1, 1);

    // Adds a chat with 1 person to the phone, before the app opens.
    private void AddChat(string id, string name, string number, bool pinned) =>
        Server.Phone.Chats.Add(FakeChats.NewConversation(id, name, false, ConversationStatus.Active, pinned, false, FakeChats.Person("p-" + id, name, number)));

    // Turns on "Show pinned chats as a grid" in Settings and goes back to the chats.
    private void ShowPinnedGrid()
    {
        App.Find("Main.ProfileButton").Patterns.Invoke.Pattern.Invoke();
        App.Find("Main.Profile.Settings").Patterns.Invoke.Pattern.Invoke();
        App.Find("Settings.PinnedGrid").AsToggleButton().Toggle();
        App.Find("Settings.Back").Patterns.Invoke.Pattern.Invoke();
        App.Find("Main.PinnedGrid");
    }

    // Makes the window narrow, so it shows the list or the chat, and brings it to the front.
    private void NarrowWindow()
    {
        Window window = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);
        window.Patterns.Transform.Pattern.Resize(600, 700);
        WaitForGone("Main.MenuButton");
        window.SetForeground();
    }

    // Starts the app again and brings the window to the front. Nothing in the window is clicked, so no element has the
    // focus.
    private async Task RestartToFrontAsync()
    {
        await App.RestartAsync();
        App.Find("Row.c-ann");
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
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
