using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using GMessages.App.UITests.Driver;
using GMessages.App.UITests.FakeGoogle;
using GMessages.Presentation;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.App.UITests;

/// <summary>
/// The shell of the main window. The tests cover the conversation list, the ☰ toggle and the profile menu.
/// They also cover Spam &amp; blocked, live updates and the narrow one-pane layout.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class MainWindowTests : UiTest
{
    // The seeded inbox: pinned Team first, then Ann.
    private static readonly string[] InboxRows = ["Row.c-team", "Row.c-ann"];

    /// <summary>
    /// The inbox shows pinned Team above Ann and never the spam conversation. The unread row of Ann is marked unread.
    /// </summary>
    [TestMethod]
    public async Task Open_ShowsInboxRows_PinnedFirst_UnreadBold()
    {
        await OpenMainAsync();

        WaitForRows(InboxRows);
        Assert.IsNull(App.TryFind("Row.c-spam"), "The spam conversation showed in the inbox.");
        StringAssert.Contains(AppDriver.SafeName(App.Find("Row.c-ann")), "Unread", "Ann's unread row isn't marked unread.");
        Assert.IsFalse(AppDriver.SafeName(App.Find("Row.c-team")).Contains("Unread", StringComparison.Ordinal), "Team's read row is marked unread.");
        App.WaitForText("Main.EmptyState", ChatCopy.StartAConversation);
        App.Find("Main.StartChat");
    }

    /// <summary>
    /// The snippet and time of an unread row use the primary text color. The unread time and snippet of Ann
    /// contrast more with the background. The read row of Team uses secondary text.
    /// </summary>
    [TestMethod]
    public async Task UnreadRow_SnippetAndTimeArePrimaryText()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);

        foreach (string part in (ReadOnlySpan<string>)["Row.Time.", "Row.Snippet."])
        {
            double unread = Contrast(App.Find(part + "c-ann").BoundingRectangle);
            double read   = Contrast(App.Find(part + "c-team").BoundingRectangle);
            Assert.IsTrue(unread > read + 20, $"{part} of the unread row (contrast {unread:F0}) isn't brighter than the read row's ({read:F0}).");
        }
    }

    /// <summary>
    /// The content of the window stays the same while the window is inactive. The tray menu takes the activation.
    /// Only the title bar dims, as in the apps of Windows. The owner reported a bug. Fading the content faded each
    /// element on its own. Overlapping elements then showed through each other. A photo over its letter and
    /// Start chat over the rows are examples.
    /// </summary>
    [TestMethod]
    public async Task Inactive_ContentDoesNotDim()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);
        System.Drawing.Rectangle time = App.Find("Row.Time.c-ann").BoundingRectangle;
        Thread.Sleep(300);
        double active = Contrast(time);

        Tray.RightClick();
        App.Find("TrayMenu.Open");
        Thread.Sleep(500);
        double inactive = Contrast(time);
        Assert.IsTrue(inactive > active * 0.9, $"The content dimmed while inactive (contrast {active:F0} active, {inactive:F0} inactive).");

        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Tray.CloseOverflow();
    }

    // Returns how far the strongest pixel of a text is from the background, 0 to 255.
    // The background is the corner pixel.
    private static double Contrast(System.Drawing.Rectangle area)
    {
        using FlaUI.Core.Capturing.CaptureImage capture = FlaUI.Core.Capturing.Capture.Rectangle(area);
        System.Drawing.Bitmap bitmap = capture.Bitmap;
        double background            = Luma(bitmap.GetPixel(0, 0));
        double most                  = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                most = Math.Max(most, Math.Abs(Luma(bitmap.GetPixel(x, y)) - background));
            }
        }

        return most;
    }

    private static double Luma(System.Drawing.Color c) => (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);

    /// <summary>
    /// A live low battery alert from the phone shows the warning banner. The restored alert hides it.
    /// </summary>
    [TestMethod]
    public async Task BatteryLow_ShowsBanner_RestoredHides()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);
        Assert.IsNull(App.TryFind("Main.BatteryLow"), "The battery banner showed before any alert.");

        Server.Phone.PushAlert(AlertType.MobileBatteryLow);
        App.Find("Main.BatteryLow");

        Server.Phone.PushAlert(AlertType.MobileBatteryRestored);
        Wait.Until(() => App.TryFind("Main.BatteryLow") is null, AppDriver.DefaultTimeout, () => "The restored alert didn't hide the battery banner.");
    }

    /// <summary>
    /// Closing the battery banner hides it. The next low alert shows it again, with no restored alert between.
    /// </summary>
    [TestMethod]
    public async Task BatteryLow_Closed_ReturnsOnNextLow()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);
        Server.Phone.PushAlert(AlertType.MobileBatteryLow);

        App.Find("Main.BatteryLow").FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"))?.Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => App.TryFind("Main.BatteryLow") is null, AppDriver.DefaultTimeout, () => "Closing the battery banner didn't hide it.");

        Server.Phone.PushAlert(AlertType.MobileBatteryLow);
        App.Find("Main.BatteryLow");
    }

    /// <summary>
    /// The row of Ann shows the photo that the phone has for her. The members of Team have no photo, so its avatar
    /// keeps the initials.
    /// </summary>
    [TestMethod]
    public async Task Avatars_AnnShowsPhoto_TeamShowsInitials()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);

        Wait.Until(() => HasPhoto("Row.c-ann"), AppDriver.DefaultTimeout, () => "Ann's row never showed her photo.");
        Assert.IsFalse(HasPhoto("Row.c-team"), "Team's row showed a photo nobody has.");
    }

    /// <summary>
    /// The profile button in the title bar shows the photo of the phone owner right after a fresh pair. It shows the
    /// photo again when the window opens from the tray with the list already cached. No row changes then, so the app
    /// never asked for the photo.
    /// </summary>
    [TestMethod]
    public async Task Profile_ShowsOwnersPhoto_AfterPairAndOnReopen()
    {
        WaitForPairing();
        await SignInAndWaitForEmojiAsync();
        Server.Phone.Tap(true);
        App.Find("Main.ConversationList");
        Wait.Until(ProfileHasPhoto, AppDriver.DefaultTimeout, () => "The profile button didn't show the owner's photo after pairing.");

        CloseMainWindow();
        Tray.LeftClick();
        App.Find("Main.ConversationList");
        Wait.Until(ProfileHasPhoto, AppDriver.DefaultTimeout, () => "The profile button didn't show the owner's photo when the window opened again.");
    }

    /// <summary>
    /// The ☰ button hides the conversation list. A second click shows it again.
    /// </summary>
    [TestMethod]
    public async Task MenuButton_CollapsesAndExpandsList()
    {
        await OpenMainAsync();

        Invoke("Main.MenuButton");
        Wait.Until(() => App.TryFind("Main.ConversationList") is null, AppDriver.DefaultTimeout, () => "☰ didn't collapse the list.");

        Invoke("Main.MenuButton");
        App.Find("Main.ConversationList");
        WaitForRows(InboxRows);
    }

    /// <summary>
    /// The profile picture is as tall as the caption buttons and sits right beside Minimize. The ☰ button belongs to
    /// the title bar. It has an inset from the window edge and is centered in the bar, as in the WinUI Gallery.
    /// </summary>
    [TestMethod]
    public async Task TitleBar_ProfileBesideCaptionButtons_MenuInset()
    {
        await OpenMainAsync();

        System.Drawing.Rectangle menu     = App.Find("Main.MenuButton").BoundingRectangle;
        System.Drawing.Rectangle profile  = App.Find("Main.ProfileButton").BoundingRectangle;
        System.Drawing.Rectangle minimize = App.Find("Minimize").BoundingRectangle;
        System.Drawing.Rectangle titleBar = App.Find("Main.TitleBar").BoundingRectangle;

        // The test measures from the title bar and not from the window. The bounds of the window include the
        // invisible resize border of Windows.
        Assert.IsTrue(menu.Height < minimize.Height, $"☰ ({menu.Height} px) fills the bar ({minimize.Height} px), with no inset.");
        Assert.IsTrue(Math.Abs((menu.Top + menu.Bottom) - (minimize.Top + minimize.Bottom)) <= 4, "☰ isn't centered in the title bar.");
        Assert.IsTrue(menu.Left - titleBar.Left >= 3, $"☰ starts {menu.Left - titleBar.Left} px from the title bar's edge.");
        Assert.IsTrue(Math.Abs(minimize.Height - profile.Height) <= 2, $"Caption buttons are {minimize.Height} px high, the profile button is {profile.Height} px.");
        Assert.IsTrue(Math.Abs(minimize.Left - profile.Right) <= 2, $"The profile button ends {minimize.Left - profile.Right} px left of Minimize.");
    }

    /// <summary>
    /// The Spam &amp; blocked item of the profile menu replaces the list with the spam conversations under a titled
    /// header. The back button in the title bar replaces ☰ and returns to the inbox.
    /// </summary>
    [TestMethod]
    public async Task Profile_SpamBlocked_ShowsSpamList_BackReturns()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);

        InvokeProfileItem("Main.Profile.SpamBlocked");
        App.WaitForText("Main.ListTitle", ChatCopy.SpamAndBlocked);
        WaitForRows(["Row.c-spam"]);
        AssertBackInTitleBar("Main.SpamBack");

        Invoke("Main.SpamBack");
        WaitForRows(InboxRows);
        Wait.Until(() => App.TryFind("Main.ListTitle") is null, AppDriver.DefaultTimeout, () => "The Spam & blocked title stayed after going back.");
        WaitForGone("Main.SpamBack");
        App.Find("Main.MenuButton");
    }

    /// <summary>
    /// The Settings item of the profile menu opens the settings page over the chats in the same window. The back
    /// button in the title bar replaces ☰ and returns to the chats.
    /// </summary>
    [TestMethod]
    public async Task Profile_Settings_OpensSettingsPage_BackReturns()
    {
        await OpenMainAsync();

        InvokeProfileItem("Main.Profile.Settings");
        WaitForSettings();
        WaitForGone("Main.ConversationList");
        AssertBackInTitleBar("Settings.Back");

        App.Find("Settings.Back").Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Settings.Back");
        WaitForRows(InboxRows);
        App.Find("Main.MenuButton");
    }

    /// <summary>
    /// The pinned grid is off by default. Turning it on in Settings moves pinned Team from the rows to a tile above
    /// them. The tile opens its chat. Turning it off puts Team back at the top of the rows.
    /// </summary>
    [TestMethod]
    public async Task PinnedGrid_Switch_MovesPinnedChatIntoTiles()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);
        Assert.IsNull(App.TryFind("Main.PinnedGrid"), "The pinned grid showed with the setting off.");

        SetPinnedGrid(ToggleState.On);
        WaitForRows(["Row.c-ann"]);
        AutomationElement tile = App.Find("Main.PinnedGrid").FindFirstDescendant(cf => cf.ByAutomationId("Row.c-team"))
            ?? throw new AssertFailedException("Team has no tile in the pinned grid.");

        // The unread badge shows a short message as text. After a second message, it shows the count.
        Server.Phone.PushIncoming("c-team", "Dinner?");
        App.WaitForText("Pinned.Badge.c-team", "Dinner?");
        Server.Phone.PushIncoming("c-team", "See you at seven");
        App.WaitForText("Pinned.Badge.c-team", "2");

        // A tile opens its chat. The chat is then read, so the badge goes.
        tile.Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Team");
        WaitForGone("Pinned.Badge.c-team");

        SetPinnedGrid(ToggleState.Off);
        WaitForRows(InboxRows);
        WaitForGone("Main.PinnedGrid");
    }

    // Sets the pinned grid switch in Settings through the profile menu and goes back to the chats.
    private void SetPinnedGrid(ToggleState wanted)
    {
        InvokeProfileItem("Main.Profile.Settings");
        WaitForSettings();
        ToggleButton toggle = App.Find("Settings.PinnedGrid").AsToggleButton();
        if (toggle.ToggleState != wanted)
        {
            toggle.Toggle();
        }

        Wait.Until(() => App.Find("Settings.PinnedGrid").AsToggleButton().ToggleState == wanted, AppDriver.DefaultTimeout,
            () => $"The pinned grid switch isn't {wanted}.");
        App.Find("Settings.Back").Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Settings.Back");
    }

    /// <summary>
    /// Closing the window with an inbox chat open and opening it again from the tray shows that chat again. A chat
    /// that the user opened from Spam &amp; blocked does not return.
    /// </summary>
    [TestMethod]
    public async Task Reopen_RestoresLastInboxChat_NotSpamChat()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);

        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Ann Lee");
        CloseMainWindow();
        Tray.LeftClick();
        App.WaitForText("Chat.Header", "Ann Lee");

        InvokeProfileItem("Main.Profile.SpamBlocked");
        WaitForRows(["Row.c-spam"]);
        App.Find("Row.c-spam").Patterns.SelectionItem.Pattern.Select();
        App.Find("Chat.Header");
        CloseMainWindow();
        Tray.LeftClick();
        WaitForRows(InboxRows);
        App.WaitForText("Main.EmptyState", ChatCopy.StartAConversation);
    }

    /// <summary>
    /// The edge between the list and the chat resizes the list. The test uses the arrow keys, because the mouse drag
    /// uses the same path. The next window keeps the width. The ☰ button hides the edge with the list.
    /// </summary>
    [TestMethod]
    public async Task ListSplitter_ResizesList_WidthKeptForNextWindow()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);
        int before = ListWidth();

        // Two steps right grow the list by 32 effective pixels.
        App.Find("Main.ListSplitter").Focus();
        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RIGHT);
        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RIGHT);
        double scale = DpiSetup.ScaleOf(App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout));
        Wait.Until(() => Math.Abs(ListWidth() - before - (32 * scale)) <= 2, AppDriver.DefaultTimeout, () => $"The list is {ListWidth()} px wide, not {before} + 32 effective px.");
        int widened = ListWidth();

        CloseMainWindow();
        Tray.LeftClick();
        WaitForRows(InboxRows);
        Wait.Until(() => Math.Abs(ListWidth() - widened) <= 2, AppDriver.DefaultTimeout, () => $"The reopened list is {ListWidth()} px wide, not {widened}.");

        Invoke("Main.MenuButton");
        Wait.Until(() => App.TryFind("Main.ListSplitter") is null, AppDriver.DefaultTimeout, () => "The resize edge stayed with the list collapsed.");

        int ListWidth() => App.Find("Main.ConversationList").BoundingRectangle.Width;
    }

    /// <summary>
    /// The About section of the settings page names the app, its version and its maker, and links to the repository.
    /// </summary>
    [TestMethod]
    public async Task Settings_About_NamesAppVersionMakerAndRepository()
    {
        await OpenMainAsync();

        InvokeProfileItem("Main.Profile.Settings");
        WaitForSettings();

        App.WaitForText("Settings.AppName", AppInfo.DisplayName);
        App.WaitForText("Settings.Version", "Version " + AppInfo.Version);
        StringAssert.Contains(AppDriver.SafeName(App.Find("Settings.Author")), "artistro08", "About doesn't name the maker.");
        App.Find("Settings.Repository");
    }

    /// <summary>
    /// Start chat floats at the bottom right over the conversation list. A scroll down hides it, and a scroll up
    /// shows it again.
    /// </summary>
    [TestMethod]
    public async Task StartChat_FloatsOverList_HidesOnScrollDown_ShowsOnScrollUp()
    {
        // The list needs enough conversations to scroll.
        for (int i = 0; i < 30; i++)
        {
            string id = "c-more-" + i;
            Server.Phone.Chats.Add(FakeChats.NewConversation(id, "More " + i, false, ConversationStatus.Active, false, false, FakeChats.Person("p-" + id, "More " + i, "+1555100" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))));
        }

        await OpenMainAsync();
        AutomationElement list  = App.Find("Main.ConversationList");
        AutomationElement start = App.Find("Main.StartChat");
        Wait.Until(() => list.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Length > 20, AppDriver.DefaultTimeout, () => "The extra conversations never showed.");

        System.Drawing.Rectangle over   = list.BoundingRectangle;
        System.Drawing.Rectangle button = start.BoundingRectangle;
        Assert.IsTrue(over.Contains(button), $"Start chat ({button}) isn't over the list ({over}).");
        Assert.IsTrue(over.Right - button.Right < 40 && over.Bottom - button.Bottom < 40, $"Start chat ({button}) isn't at the list's bottom right ({over}).");

        // The button has the standard accent button size of about 32 effective pixels.
        // It is not the old pill of 48 pixels.
        double scale = DpiSetup.ScaleOf(App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout));
        Assert.IsTrue(button.Height / scale < 40, $"Start chat is {button.Height / scale:0} effective pixels tall, not the standard button's size.");

        list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 50);
        Wait.Until(() => StartChatStatus() == "Hidden", AppDriver.DefaultTimeout, () => "Scrolling down didn't hide Start chat.");
        list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 10);
        Wait.Until(() => StartChatStatus() != "Hidden", AppDriver.DefaultTimeout, () => "Scrolling up didn't show Start chat.");

        string StartChatStatus() => App.Find("Main.StartChat").Properties.ItemStatus.ValueOrDefault ?? "";
    }

    /// <summary>
    /// The list loads one page at a time. With 60 more conversations, the oldest is not there at first. A scroll to
    /// the bottom loads the next pages until the oldest shows.
    /// </summary>
    [TestMethod]
    public async Task ConversationList_ScrollToBottom_LoadsMorePages()
    {
        // 60 conversations that are older than the seeded ones. A lower number means a newer conversation.
        const int Count = 60;
        for (int i = 0; i < Count; i++)
        {
            string id                         = "c-page-" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            Conversation chat                 = FakeChats.NewConversation(id, "Page " + i, false, ConversationStatus.Active, false, false, FakeChats.Person("p-" + id, "Page " + i, "+1555200" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)));
            chat.LastMessageTimestamp         = (Count - i) * 1_000_000L;
            Server.Phone.Chats.Add(chat);
        }

        await OpenMainAsync();
        AutomationElement list = App.Find("Main.ConversationList");
        App.Find("Row.c-page-00");
        Assert.IsNull(App.TryFind("Row.c-page-59"), "The oldest conversation showed before scrolling, so the list didn't page.");

        // Each scroll to the bottom realizes the last row, and that row asks for the next page.
        // The test notes the size of the footer ring when it catches the ring.
        System.Drawing.Rectangle? ring = null;
        Wait.Until(() =>
        {
            list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
            if (App.TryFind("Main.ListLoadingMore") is { } shown && shown.BoundingRectangle is { Width: > 0 } box)
            {
                ring = box;
            }

            return App.TryFind("Row.c-page-59") is not null;
        }, AppDriver.DefaultTimeout, () => "Scrolling to the bottom never loaded the oldest conversation.");

        // At a standard display scale, the ring is 24 px with a whole 3 px stroke. It is not the old 20 px ring with a
        // 2.5 px stroke.
        if (ring is { } seen)
        {
            double scale = seen.Width / 24.0;
            Assert.IsTrue(Math.Abs((scale * 4) - Math.Round(scale * 4)) < 0.12, $"The loading ring is {seen.Width} px, not 24 px at a standard scale.");
        }
    }

    /// <summary>
    /// A live message for an older conversation updates the snippet of its row. The row moves above Ann, right below
    /// pinned Team.
    /// </summary>
    [TestMethod]
    public async Task LiveConversationUpdate_MovesRowToTop()
    {
        // Dee is older than every seeded conversation, so her row starts last.
        Server.Phone.Chats.Add(FakeChats.NewConversation("c-dee", "Dee", false, ConversationStatus.Active, false, false, FakeChats.Person("p-dee", "Dee", "+15550000004")));
        await OpenMainAsync();
        WaitForRows(["Row.c-team", "Row.c-ann", "Row.c-dee"]);

        Server.Phone.PushIncoming("c-dee", "yo");

        Wait.Until(() => App.TryFind("Row.c-dee") is { } row && AppDriver.Search(row, e => AppDriver.SafeName(e) == "yo") is not null,
            AppDriver.DefaultTimeout, () => "Dee's row never showed the snippet 'yo'.");
        WaitForRows(["Row.c-team", "Row.c-dee", "Row.c-ann"]);
    }

    /// <summary>
    /// A new message in the open conversation lifts its row with no flash. If animations are on, the row moves with
    /// the motion of the list. The row stays selected and its chat stays open.
    /// </summary>
    [TestMethod]
    public async Task LiveMessage_OpenRowMovesToTop_StaysSelected()
    {
        // Dee is older than every seeded conversation, so her row starts last.
        Server.Phone.Chats.Add(FakeChats.NewConversation("c-dee", "Dee", false, ConversationStatus.Active, false, false, FakeChats.Person("p-dee", "Dee", "+15550000004")));
        await OpenMainAsync();
        WaitForRows(["Row.c-team", "Row.c-ann", "Row.c-dee"]);

        App.Find("Row.c-dee").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Dee");

        Server.Phone.PushIncoming("c-dee", "yo");
        WaitForRows(["Row.c-team", "Row.c-dee", "Row.c-ann"]);
        Wait.Until(() => App.TryFind("Row.c-dee") is { } row && row.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault,
            AppDriver.DefaultTimeout, () => "Dee's row lost the selection when it moved.");
        Assert.IsFalse(App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault, "Ann's row took the selection.");
        App.WaitForText("Chat.Header", "Dee");
    }

    /// <summary>
    /// The window stops shrinking at its minimum size. The minimum width is the narrowest chat list, 300 px.
    /// The minimum height is 500 px. Both values use the display scale and add the frame.
    /// </summary>
    [TestMethod]
    public async Task Window_ResizeBelowMinimum_StopsAtMinimum()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);
        Window window = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);
        double scale  = App.Find("Main.TitleBar").BoundingRectangle.Height / 48.0;

        window.Patterns.Transform.Pattern.Resize(100, 100);
        Thread.Sleep(500);

        System.Drawing.Rectangle box = window.BoundingRectangle;
        Assert.IsTrue(box.Width >= (300 * scale) - 1 && box.Width <= (300 * scale) + (40 * scale), $"The window shrank to {box.Width} px wide, not about {300 * scale:F0} px (scale {scale:F2}).");
        Assert.IsTrue(box.Height >= (500 * scale) - 1 && box.Height <= (500 * scale) + (40 * scale), $"The window shrank to {box.Height} px tall, not about {500 * scale:F0} px (scale {scale:F2}).");
        App.Find("Main.ConversationList");
    }

    /// <summary>
    /// A narrow window shows one pane. A pick of a conversation hides the list and shows the chat.
    /// </summary>
    [TestMethod]
    public async Task NarrowWindow_ShowsOnePane()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);

        Window window = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);
        window.Patterns.Transform.Pattern.Resize(600, 700);

        // One pane shows, so there is no list to hide and no ☰ button.
        WaitForGone("Main.MenuButton");

        // The chat replaces the list, and Back shows in the title bar.
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => App.TryFind("Main.ConversationList") is null, AppDriver.DefaultTimeout, () => "The list still showed beside the chat in a narrow window.");
        App.Find("Chat.Header");
        AssertBackInTitleBar("Chat.Back");

        // Back shows the list again, still without the ☰ button and without Back.
        Invoke("Chat.Back");
        App.Find("Main.ConversationList");
        Wait.Until(() => App.TryFind("Chat.Back") is null, AppDriver.DefaultTimeout, () => "Back stayed in the title bar on the list.");
        WaitForGone("Main.MenuButton");

        // Start chat gets the same Back.
        Invoke("Main.StartChat");
        App.Find("StartChat.Query");
        AssertBackInTitleBar("Chat.Back");
    }

    /// <summary>
    /// The first row of the list starts level with the card of the open chat. The card is the rounded island around
    /// the header and the messages. The owner reported that the chat list did not line up with the message window.
    /// The chat pane, <c>Chat.Pane</c>, fills the island inside its 1 px border. Its top is therefore the top of the
    /// card.
    /// </summary>
    [TestMethod]
    public async Task ConversationList_FirstRow_LinesUpWithChatIsland()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);

        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Ann Lee");

        int cardTop  = App.Find("Chat.Pane").BoundingRectangle.Top;
        int rowTop   = App.Find("Row.c-team").BoundingRectangle.Top;
        Assert.IsTrue(Math.Abs(rowTop - cardTop) <= 2, $"The first row starts at y {rowTop}, the chat card at y {cardTop}.");
    }

    private void Invoke(string automationId) => App.Find(automationId).Patterns.Invoke.Pattern.Invoke();

    // Back shows in the title bar row, where the ☰ button was, and the ☰ button is gone.
    private void AssertBackInTitleBar(string automationId)
    {
        System.Drawing.Rectangle back      = App.Find(automationId).BoundingRectangle;
        System.Drawing.Rectangle titleBar  = App.Find("Main.TitleBar").BoundingRectangle;
        Assert.IsTrue(titleBar.Contains(back), $"Back ({back}) isn't in the title bar ({titleBar}).");
        WaitForGone("Main.MenuButton");
    }

    // Opens the profile flyout and picks one of its items.
    private void InvokeProfileItem(string automationId)
    {
        Invoke("Main.ProfileButton");
        Invoke(automationId);
    }

    // Returns true when an avatar under the row reports that it shows a photo.
    private bool HasPhoto(string rowId) =>
        App.TryFind(rowId) is { } row && AppDriver.Search(row, e => e.Properties.ItemStatus.ValueOrDefault == "Photo") is not null;

    // Returns true when the profile button reports that it shows the photo of the owner.
    private bool ProfileHasPhoto() =>
        App.TryFind("Main.ProfileButton") is { } button && button.Properties.ItemStatus.ValueOrDefault == "Photo";

    // Returns the AutomationId of each row of the list, from top to bottom.
    private string[] RowIds()
    {
        AutomationElement? list = App.TryFind("Main.ConversationList");
        if (list is null)
        {
            return [];
        }

        try
        {
            return [.. list.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
                .Select(e => e.Properties.AutomationId.ValueOrDefault ?? "")
                .Where(id => id.StartsWith("Row.", StringComparison.Ordinal))];
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            // The list changed during the walk. The caller reads again.
            return [];
        }
    }

    private void WaitForRows(string[] expected)
    {
        string[] last = [];
        Wait.Until(() => (last = RowIds()).SequenceEqual(expected), AppDriver.DefaultTimeout,
            () => $"Rows were [{string.Join(", ", last)}], not [{string.Join(", ", expected)}].");
    }
}
