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
    /// the back button returns to the inbox.
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

        // Back To The Inbox
        Invoke("Main.SpamBack");
        WaitForRows(InboxRows);
        Wait.Until(() => App.TryFind("Main.ListTitle") is null, AppDriver.DefaultTimeout, () => "The Spam & blocked title stayed after going back.");
    }

    /// <summary>
    /// The profile menu's Settings opens the settings window.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Profile_Settings_OpensSettingsWindow()
    {
        await OpenMainAsync();

        InvokeProfileItem("Main.Profile.Settings");
        App.WaitForWindow(SettingsTitle, AppDriver.DefaultTimeout);
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
