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

        // Pick Ann: The List Gives Way To The Chat
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => App.TryFind("Main.ConversationList") is null, AppDriver.DefaultTimeout, () => "The list still showed beside the chat in a narrow window.");
        App.Find("Chat.Header");
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
