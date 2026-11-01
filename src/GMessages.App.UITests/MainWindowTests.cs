using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using GMessages.App.UITests.Driver;
using GMessages.App.UITests.FakeGoogle;
using GMessages.Presentation;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.App.UITests;

/// <summary>
/// The main window's shell: the conversation list, the ☰ toggle, the profile menu, Spam &amp; blocked, live updates
/// and the narrow one-pane layout.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class MainWindowTests : UiTest
{
    // The Seeded Inbox: Pinned Team First, Then Ann
    private static readonly string[] InboxRows = ["Row.c-team", "Row.c-ann"];

    /// <summary>
    /// The inbox shows pinned Team above Ann, never the spam conversation, and Ann's unread row says so.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// An unread row's snippet and time are fully bright (primary text): Ann's unread time and snippet stand out from
    /// their background more than read Team's secondary ones.
    /// </summary>
    /// <returns>A task for the test.</returns>
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

    // How Far A Text's Strongest Pixel Stands From The Background (Its Corner Pixel), 0 To 255
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
    /// A live low battery alert from the phone shows the warning banner, and the restored alert hides it.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// Closing the battery banner hides it, and the next low alert (no restored between) shows it again.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// Ann's row shows the photo the phone has for her; Team's members have none, so its avatar keeps the initials.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Avatars_AnnShowsPhoto_TeamShowsInitials()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);

        Wait.Until(() => HasPhoto("Row.c-ann"), AppDriver.DefaultTimeout, () => "Ann's row never showed her photo.");
        Assert.IsFalse(HasPhoto("Row.c-team"), "Team's row showed a photo nobody has.");
    }

    /// <summary>
    /// The title bar's profile button shows the phone owner's photo right after a fresh pair, and again when the
    /// window opens from the tray with the list already cached (no row changes then, so the photo was never asked for).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Profile_ShowsOwnersPhoto_AfterPairAndOnReopen()
    {
        // Fresh Pair
        WaitForPairing();
        await SignInAndWaitForEmojiAsync();
        Server.Phone.Tap(true);
        App.Find("Main.ConversationList");
        Wait.Until(ProfileHasPhoto, AppDriver.DefaultTimeout, () => "The profile button didn't show the owner's photo after pairing.");

        // Reopen From The Tray
        CloseMainWindow();
        Tray.LeftClick();
        App.Find("Main.ConversationList");
        Wait.Until(ProfileHasPhoto, AppDriver.DefaultTimeout, () => "The profile button didn't show the owner's photo when the window opened again.");
    }

    /// <summary>
    /// ☰ hides the conversation list, and a second click brings it back.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// The title bar row and the caption buttons are the same height, and the profile picture sits right beside
    /// Minimize.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task TitleBar_ProfileBesideCaptionButtons_SameHeight()
    {
        await OpenMainAsync();

        System.Drawing.Rectangle menu     = App.Find("Main.MenuButton").BoundingRectangle;
        System.Drawing.Rectangle profile  = App.Find("Main.ProfileButton").BoundingRectangle;
        System.Drawing.Rectangle minimize = App.Find("Minimize").BoundingRectangle;

        Assert.IsTrue(Math.Abs(minimize.Height - menu.Height) <= 2, $"Caption buttons are {minimize.Height} px high, ☰ is {menu.Height} px.");
        Assert.IsTrue(Math.Abs(minimize.Height - profile.Height) <= 2, $"Caption buttons are {minimize.Height} px high, the profile button is {profile.Height} px.");
        Assert.IsTrue(Math.Abs(minimize.Left - profile.Right) <= 2, $"The profile button ends {minimize.Left - profile.Right} px left of Minimize.");
    }

    /// <summary>
    /// The profile menu's Spam &amp; blocked swaps the list for the spam conversations under a titled header, and
    /// the title bar's back button, in place of ☰, returns to the inbox.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Profile_SpamBlocked_ShowsSpamList_BackReturns()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);

        // Spam And Blocked
        InvokeProfileItem("Main.Profile.SpamBlocked");
        App.WaitForText("Main.ListTitle", ChatCopy.SpamAndBlocked);
        WaitForRows(["Row.c-spam"]);
        AssertBackInTitleBar("Main.SpamBack");

        // Back To The Inbox
        Invoke("Main.SpamBack");
        WaitForRows(InboxRows);
        Wait.Until(() => App.TryFind("Main.ListTitle") is null, AppDriver.DefaultTimeout, () => "The Spam & blocked title stayed after going back.");
        WaitForGone("Main.SpamBack");
        App.Find("Main.MenuButton");
    }

    /// <summary>
    /// The profile menu's Settings opens the settings page over the chats in the same window; the title bar's back
    /// button, in place of ☰, returns to them.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// Closing the window with an inbox chat open and opening it again from the tray shows that chat again; a chat
    /// opened from Spam &amp; blocked doesn't come back.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Reopen_RestoresLastInboxChat_NotSpamChat()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);

        // Inbox Chat: Back After A Reopen
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Ann Lee");
        CloseMainWindow();
        Tray.LeftClick();
        App.WaitForText("Chat.Header", "Ann Lee");

        // Spam Chat: Not Back
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
    /// The edge between the list and the chat resizes the list (arrow keys here, as the mouse drag shares the same path);
    /// the width comes back in the next window. ☰ hides the edge with the list.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ListSplitter_ResizesList_WidthKeptForNextWindow()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);
        int before = ListWidth();

        // Two Steps Right: The List Grows By 32 Effective Pixels
        App.Find("Main.ListSplitter").Focus();
        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RIGHT);
        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RIGHT);
        double scale = DpiSetup.ScaleOf(App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout));
        Wait.Until(() => Math.Abs(ListWidth() - before - (32 * scale)) <= 2, AppDriver.DefaultTimeout, () => $"The list is {ListWidth()} px wide, not {before} + 32 effective px.");
        int widened = ListWidth();

        // Next Window: Same Width
        CloseMainWindow();
        Tray.LeftClick();
        WaitForRows(InboxRows);
        Wait.Until(() => Math.Abs(ListWidth() - widened) <= 2, AppDriver.DefaultTimeout, () => $"The reopened list is {ListWidth()} px wide, not {widened}.");

        // ☰ Takes The Edge Away With The List
        Invoke("Main.MenuButton");
        Wait.Until(() => App.TryFind("Main.ListSplitter") is null, AppDriver.DefaultTimeout, () => "The resize edge stayed with the list collapsed.");

        int ListWidth() => App.Find("Main.ConversationList").BoundingRectangle.Width;
    }

    /// <summary>
    /// The settings page's About names the app, its version and its maker, and links to the repository.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// Start chat floats at the bottom right over the conversation list; scrolling the list down hides it and scrolling
    /// back up shows it again.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task StartChat_FloatsOverList_HidesOnScrollDown_ShowsOnScrollUp()
    {
        // Enough Conversations To Scroll
        for (int i = 0; i < 30; i++)
        {
            string id = "c-more-" + i;
            Server.Phone.Chats.Add(FakeChats.NewConversation(id, "More " + i, false, ConversationStatus.Active, false, false, FakeChats.Person("p-" + id, "More " + i, "+1555100" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))));
        }

        await OpenMainAsync();
        AutomationElement list  = App.Find("Main.ConversationList");
        AutomationElement start = App.Find("Main.StartChat");
        Wait.Until(() => list.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Length > 20, AppDriver.DefaultTimeout, () => "The extra conversations never showed.");

        // Over The List, At Its Bottom Right
        System.Drawing.Rectangle over   = list.BoundingRectangle;
        System.Drawing.Rectangle button = start.BoundingRectangle;
        Assert.IsTrue(over.Contains(button), $"Start chat ({button}) isn't over the list ({over}).");
        Assert.IsTrue(over.Right - button.Right < 40 && over.Bottom - button.Bottom < 40, $"Start chat ({button}) isn't at the list's bottom right ({over}).");

        // The Standard Accent Button's Size (About 32 Effective Pixels Tall), Not The Old 48 Pixel Pill
        double scale = DpiSetup.ScaleOf(App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout));
        Assert.IsTrue(button.Height / scale < 40, $"Start chat is {button.Height / scale:0} effective pixels tall, not the standard button's size.");

        // Down Hides It, Up Shows It
        list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 50);
        Wait.Until(() => StartChatStatus() == "Hidden", AppDriver.DefaultTimeout, () => "Scrolling down didn't hide Start chat.");
        list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 10);
        Wait.Until(() => StartChatStatus() != "Hidden", AppDriver.DefaultTimeout, () => "Scrolling up didn't show Start chat.");

        string StartChatStatus() => App.Find("Main.StartChat").Properties.ItemStatus.ValueOrDefault ?? "";
    }

    /// <summary>
    /// The list loads a page at a time: with 60 more conversations the oldest isn't there at first, and scrolling to
    /// the bottom loads the next pages until it shows.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ConversationList_ScrollToBottom_LoadsMorePages()
    {
        // 60 Conversations Older Than The Seeded Ones, Newest First By Number
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

        // Each Scroll To The Bottom Realizes The Last Row, Which Asks For The Next Page
        Wait.Until(() =>
        {
            list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
            return App.TryFind("Row.c-page-59") is not null;
        }, AppDriver.DefaultTimeout, () => "Scrolling to the bottom never loaded the oldest conversation.");
    }

    /// <summary>
    /// A live message for an older conversation updates its row's snippet and moves it above Ann, right below
    /// pinned Team.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task LiveConversationUpdate_MovesRowToTop()
    {
        // Dee: Older Than Every Seeded Conversation, So Her Row Starts Last
        Server.Phone.Chats.Add(FakeChats.NewConversation("c-dee", "Dee", false, ConversationStatus.Active, false, false, FakeChats.Person("p-dee", "Dee", "+15550000004")));
        await OpenMainAsync();
        WaitForRows(["Row.c-team", "Row.c-ann", "Row.c-dee"]);

        Server.Phone.PushIncoming("c-dee", "yo");

        Wait.Until(() => App.TryFind("Row.c-dee") is { } row && AppDriver.Search(row, e => AppDriver.SafeName(e) == "yo") is not null,
            AppDriver.DefaultTimeout, () => "Dee's row never showed the snippet 'yo'.");
        WaitForRows(["Row.c-team", "Row.c-dee", "Row.c-ann"]);
    }

    /// <summary>
    /// A new message in the open conversation lifts its row (with the list's motion, animations permitting; no flash) and
    /// the row stays selected, with its chat still open.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task LiveMessage_OpenRowMovesToTop_StaysSelected()
    {
        // Dee: Older Than Every Seeded Conversation, So Her Row Starts Last
        Server.Phone.Chats.Add(FakeChats.NewConversation("c-dee", "Dee", false, ConversationStatus.Active, false, false, FakeChats.Person("p-dee", "Dee", "+15550000004")));
        await OpenMainAsync();
        WaitForRows(["Row.c-team", "Row.c-ann", "Row.c-dee"]);

        // Open Dee's Chat
        App.Find("Row.c-dee").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Dee");

        // A New Message: Her Row Lifts Below Pinned Team And Stays Selected
        Server.Phone.PushIncoming("c-dee", "yo");
        WaitForRows(["Row.c-team", "Row.c-dee", "Row.c-ann"]);
        Wait.Until(() => App.TryFind("Row.c-dee") is { } row && row.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault,
            AppDriver.DefaultTimeout, () => "Dee's row lost the selection when it moved.");
        Assert.IsFalse(App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault, "Ann's row took the selection.");
        App.WaitForText("Chat.Header", "Dee");
    }

    /// <summary>
    /// A narrow window shows one pane: picking a conversation hides the list and shows the chat.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task NarrowWindow_ShowsOnePane()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);

        // Narrow
        Window window = App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout);
        window.Patterns.Transform.Pattern.Resize(600, 700);

        // No ☰: There Is No List To Hide While One Pane Shows
        WaitForGone("Main.MenuButton");

        // Pick Ann: The List Gives Way To The Chat, With Back In The Title Bar
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => App.TryFind("Main.ConversationList") is null, AppDriver.DefaultTimeout, () => "The list still showed beside the chat in a narrow window.");
        App.Find("Chat.Header");
        AssertBackInTitleBar("Chat.Back");

        // Back: The List Again, Still Without ☰ Or Back
        Invoke("Chat.Back");
        App.Find("Main.ConversationList");
        Wait.Until(() => App.TryFind("Chat.Back") is null, AppDriver.DefaultTimeout, () => "Back stayed in the title bar on the list.");
        WaitForGone("Main.MenuButton");

        // Start Chat Gets The Same Back
        Invoke("Main.StartChat");
        App.Find("StartChat.Query");
        AssertBackInTitleBar("Chat.Back");
    }

    /// <summary>
    /// The list's first row starts level with the open chat's card, the rounded island around the header and the
    /// messages (owner: "the chat list doesn't line up with the message window"). The chat pane (<c>Chat.Pane</c>)
    /// fills the island inside its 1 px border, so its top is the card's top.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ConversationList_FirstRow_LinesUpWithChatIsland()
    {
        await OpenMainAsync();
        WaitForRows(InboxRows);

        // Open A Chat: The Card Shows Its Header And Messages
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Ann Lee");

        int card_top = App.Find("Chat.Pane").BoundingRectangle.Top;
        int row_top  = App.Find("Row.c-team").BoundingRectangle.Top;
        Assert.IsTrue(Math.Abs(row_top - card_top) <= 2, $"The first row starts at y {row_top}, the chat card at y {card_top}.");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private void Invoke(string automationId) => App.Find(automationId).Patterns.Invoke.Pattern.Invoke();

    // Back Shows In The Title Bar Row, Where ☰ Was, And ☰ Is Gone
    private void AssertBackInTitleBar(string automationId)
    {
        System.Drawing.Rectangle back      = App.Find(automationId).BoundingRectangle;
        System.Drawing.Rectangle title_bar = App.Find("Main.TitleBar").BoundingRectangle;
        Assert.IsTrue(title_bar.Contains(back), $"Back ({back}) isn't in the title bar ({title_bar}).");
        WaitForGone("Main.MenuButton");
    }

    // Opens The Profile Flyout, Then Picks One Of Its Items
    private void InvokeProfileItem(string automationId)
    {
        Invoke("Main.ProfileButton");
        Invoke(automationId);
    }

    // True When An Avatar Under The Row Says It Shows A Photo
    private bool HasPhoto(string rowId) =>
        App.TryFind(rowId) is { } row && AppDriver.Search(row, e => e.Properties.ItemStatus.ValueOrDefault == "Photo") is not null;

    // True When The Profile Button Says It Shows The Owner's Photo
    private bool ProfileHasPhoto() =>
        App.TryFind("Main.ProfileButton") is { } button && button.Properties.ItemStatus.ValueOrDefault == "Photo";

    // The List's Rows, Top To Bottom, By AutomationId
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
            // The List Changed Mid-Walk; Read Again
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
