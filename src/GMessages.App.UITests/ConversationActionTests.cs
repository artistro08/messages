using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace GMessages.App.UITests;

/// <summary>
/// Conversation actions from the list: the row menu (right-click, the hover ⋮, Shift + F10), archive and the Archived
/// view, mute, block &amp; report spam, move to trash, their confirm dialogs and the failure banner.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class ConversationActionTests : UiTest
{
    private const string AnnPhone = "+15550000001";

    // A Row's Right Padding: The Most An Unhovered Row's Time May Sit In From The Row's Right Edge
    private const int RowRightPadding = 16;

    // How Long A Message That Must Not Notify Is Watched For A Toast
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The row menu's Archive moves Ann out of the inbox; Archived (from the profile menu) lists her, and her Unarchive
    /// brings her back to the inbox.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task RowMenu_Archive_MovesToArchived_ThenUnarchive()
    {
        await OpenListAsync();

        // Archive
        PickFromRowMenu("Row.c-ann", "Conversation.Archive");
        WaitForGone("Row.c-ann");
        WaitForUpdates(1);
        Assert.AreEqual(ConversationStatus.Archived, Server.Phone.ConversationUpdates[0].UpdateData.Status);

        // Archived Lists Her
        Invoke("Main.ProfileButton");
        Invoke("Main.Profile.Archived");
        App.WaitForText("Main.ListTitle", ChatCopy.Archived);
        App.Find("Main.ArchivedBack");

        // Unarchive Brings Her Back
        PickFromRowMenu("Row.c-ann", "Conversation.Unarchive");
        WaitForGone("Row.c-ann");
        WaitForUpdates(2);
        Assert.AreEqual(ConversationStatus.Active, Server.Phone.ConversationUpdates[1].UpdateData.Status);

        Invoke("Main.ArchivedBack");
        WaitForGone("Main.ListTitle");
        App.Find("Row.c-ann");
    }

    /// <summary>
    /// With the mouse over Ann's row, its ⋮ opens her menu.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task RowHover_MoreButton_OpensMenu()
    {
        await OpenListAsync();

        // The Pointer Onto The Row, Then Onto The ⋮ (Collapsed Until Then, So It Takes No Clicks)
        System.Drawing.Rectangle row = App.Find("Row.c-ann").BoundingRectangle;
        PointerInput.Hover(row);
        AutomationElement more = ShownMore("Row.More.c-ann");
        PointerInput.Hover(more.BoundingRectangle);

        // Settle Wait For The ⋮'s Fade In (167 ms), Which Turns On Its Clicks
        Thread.Sleep(500);

        Assert.AreEqual(ChatCopy.MoreOptions, AppDriver.SafeName(more));
        more.Click();

        App.Find("Conversation.Mute");
    }

    /// <summary>
    /// Moving from Ann's name onto blank space beside the time keeps the ⋮ (the name's exit isn't the row's): a click on
    /// it still opens her menu instead of opening her chat.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task RowHover_NameToBlankSpace_KeepsMoreButton()
    {
        await OpenListAsync();

        AutomationElement row   = App.Find("Row.c-ann");
        AutomationElement? name = null;
        Wait.Until(() => (name = AppDriver.Search(row, e => e.ControlType == ControlType.Text && AppDriver.SafeName(e) == "Ann Lee")) is not null,
            AppDriver.DefaultTimeout, () => "Ann's row shows no name.");

        // Name, Then Blank Space Left Of The ⋮ On The Lower Line (Under The Time, Where No Icon Shows)
        PointerInput.Hover(name!.BoundingRectangle);
        AutomationElement more             = ShownMore("Row.More.c-ann");
        System.Drawing.Rectangle more_rect = more.BoundingRectangle;
        System.Drawing.Rectangle row_rect  = row.BoundingRectangle;
        PointerInput.Hover(new System.Drawing.Rectangle(more_rect.Left - 20, row_rect.Bottom - 16, 2, 2));

        // Settle Wait For Any Fade (167 ms) Before The Click
        Thread.Sleep(500);
        more.Click();

        App.Find("Conversation.Mute");
        Assert.IsNull(App.TryFind("Chat.Draft"), "The click opened Ann's chat: the ⋮ had hidden.");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// Leaving the window straight from Ann's row hides the ⋮ (the exit reports a point inside the row, so a bounds check
    /// would keep it): a click where it was opens her chat, not her menu.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task RowHover_LeaveWindow_HidesMoreButton()
    {
        await OpenListAsync();

        System.Drawing.Rectangle row = App.Find("Row.c-ann").BoundingRectangle;
        System.Drawing.Rectangle win = App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).BoundingRectangle;
        PointerInput.Hover(row);
        System.Drawing.Rectangle more_rect = ShownMore("Row.More.c-ann").BoundingRectangle;

        PointerInput.LeaveLeft(win);

        // A Click Where The ⋮ Was, Once It Has Collapsed
        WaitForGone("Row.More.c-ann");
        FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(more_rect.X + (more_rect.Width / 2), more_rect.Y + (more_rect.Height / 2)));

        App.Find("Chat.Draft");
        Assert.IsNull(App.TryFind("Conversation.Mute"), "The ⋮ stayed after the pointer left the window.");
    }

    /// <summary>
    /// With no row hovered, Ann's time sits at the row's right edge (within the row's right padding) and her ⋮ is
    /// collapsed (no space, and not in the UI Automation tree).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Row_TimeAtRightEdge()
    {
        await OpenListAsync();
        WidenList();

        System.Drawing.Rectangle row  = App.Find("Row.c-ann").BoundingRectangle;
        System.Drawing.Rectangle time = App.Find("Row.Time.c-ann").BoundingRectangle;

        Assert.IsLessThanOrEqualTo(RowRightPadding, row.Right - time.Right, $"Ann's time ends at {time.Right}, not at the row's right edge ({row.Right}).");
        Assert.IsNull(App.TryFind("Row.More.c-ann"), "The hidden ⋮ isn't collapsed.");
    }

    /// <summary>
    /// With no row hovered, Team's and Ann's times (names of different lengths) end at the same x.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Row_TimesRightAlign_WhateverTheName()
    {
        await OpenListAsync();
        WidenList();

        int team = App.Find("Row.Time.c-team").BoundingRectangle.Right;
        int ann  = App.Find("Row.Time.c-ann").BoundingRectangle.Right;

        Assert.IsLessThanOrEqualTo(1, Math.Abs(team - ann), $"Team's time ends at {team}, Ann's at {ann}.");
    }

    /// <summary>
    /// Hovering Ann's row gives her ⋮ its space at the right edge and slides only her time left to make room; Team's
    /// time stays put. Once the pointer leaves the window, the ⋮ collapses and Ann's time is back at the right edge.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task RowHover_TimeMovesLeftForMore()
    {
        await OpenListAsync();

        System.Drawing.Rectangle row  = App.Find("Row.c-ann").BoundingRectangle;
        System.Drawing.Rectangle win  = App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).BoundingRectangle;
        AutomationElement ann_time    = App.Find("Row.Time.c-ann");
        AutomationElement team_time   = App.Find("Row.Time.c-team");
        int ann_right                 = ann_time.BoundingRectangle.Right;
        int team_right                = team_time.BoundingRectangle.Right;

        // Hovered: Only Ann's Time Slides Left, Until It Ends Where The ⋮ Starts
        PointerInput.Hover(row);
        System.Drawing.Rectangle hovered_more = ShownMore("Row.More.c-ann").BoundingRectangle;
        Wait.Until(() => ann_time.BoundingRectangle.Right <= hovered_more.Left, AppDriver.DefaultTimeout,
            () => $"Ann's time ({ann_time.BoundingRectangle}) still overlaps the ⋮ ({hovered_more}).");
        int hovered_right = ann_time.BoundingRectangle.Right;
        Assert.IsGreaterThanOrEqualTo(32, ann_right - hovered_right, $"Ann's time only moved from {ann_right} to {hovered_right}.");
        Assert.IsLessThanOrEqualTo(1, Math.Abs(team_time.BoundingRectangle.Right - team_right), "Team's time moved while Ann's row was hovered.");

        // Left: The ⋮ Collapses Once The Time Is Back At The Right Edge
        PointerInput.LeaveLeft(win);
        WaitForGone("Row.More.c-ann");
        Assert.IsLessThanOrEqualTo(1, Math.Abs(ann_time.BoundingRectangle.Right - ann_right), "Ann's time didn't slide back.");
    }

    /// <summary>
    /// Mute sends the phone a mute, the row shows the muted icon, and the menu then offers Unmute instead.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Mute_ShowsIcon_MenuSaysUnmute()
    {
        await OpenListAsync();

        PickFromRowMenu("Row.c-ann", "Conversation.Mute");
        WaitForUpdates(1);
        Assert.AreEqual(ConversationMuteStatus.Mute, Server.Phone.ConversationUpdates[0].UpdateData.Mute);
        Assert.AreEqual(ChatCopy.Muted, AppDriver.SafeName(App.Find("Row.Muted.c-ann")));

        App.Find("Row.c-ann").RightClick();
        App.Find("Conversation.Unmute");
        Assert.IsNull(App.TryFind("Conversation.Mute"), "The menu still offered Mute for a muted chat.");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// A message in a muted chat shows no toast but still makes the row unread; a message in another chat still toasts.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task MutedChat_NoToast_RowUnread()
    {
        var toasts = new ToastDriver(App.Automation);
        await OpenListAsync();

        // Read Ann's Chat, Then Leave It, So No Chat Is On Screen
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        App.Find("Chat.Draft");
        Wait.Until(() => !AppDriver.SafeName(App.TryFind("Row.c-ann")).Contains("Unread", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => "Ann's row stayed unread after opening her chat.");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        WaitForGone("Chat.Draft");

        // Mute, Then A Message
        PickFromRowMenu("Row.c-ann", "Conversation.Mute");
        App.Find("Row.Muted.c-ann");
        Server.Phone.PushIncoming("c-ann", "quiet please");

        toasts.AssertNone("quiet please", QuietPeriod);
        string last = "";
        Wait.Until(() => (last = AppDriver.SafeName(App.TryFind("Row.c-ann"))).EndsWith(", Unread", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => $"Ann's row read '{last}', not unread.");

        // A Live Message Elsewhere Still Toasts, So The Quiet Check Meant Something
        Server.Phone.PushIncoming("c-team", "still here");
        toasts.WaitFor(AppDriver.DefaultTimeout, "Team", "Bo: still here");
    }

    /// <summary>
    /// Block &amp; report spam asks first, with Report spam checked; Block sends a block-and-report with Ann's number,
    /// and she moves to Spam &amp; blocked.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task BlockAndReport_AsksThenMovesToSpamBlocked()
    {
        await OpenListAsync();

        PickFromRowMenu("Row.c-ann", "Conversation.BlockAndReport");
        AutomationElement dialog = App.Find("Dialog.Block");
        Assert.AreEqual(ToggleState.On, App.Find("Dialog.ReportSpam").Patterns.Toggle.Pattern.ToggleState.Value);
        DialogButton(dialog, ChatCopy.Block).Patterns.Invoke.Pattern.Invoke();

        WaitForUpdates(1);
        UpdateConversationRequest request = Server.Phone.ConversationUpdates[0];
        Assert.AreEqual(ConversationActionStatus.BlockAndReport, request.Action);
        Assert.AreEqual(AnnPhone, request.DeleteData.Phone);
        WaitForGone("Row.c-ann");

        Invoke("Main.ProfileButton");
        Invoke("Main.Profile.SpamBlocked");
        App.WaitForText("Main.ListTitle", ChatCopy.SpamAndBlocked);
        App.Find("Row.c-ann");
    }

    /// <summary>
    /// Clearing Report spam before Block sends a plain block.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task BlockWithoutReport_SendsBlock()
    {
        await OpenListAsync();

        PickFromRowMenu("Row.c-ann", "Conversation.BlockAndReport");
        AutomationElement dialog = App.Find("Dialog.Block");
        AutomationElement report = App.Find("Dialog.ReportSpam");
        report.Patterns.Toggle.Pattern.Toggle();
        Wait.Until(() => report.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.Off, AppDriver.DefaultTimeout,
            () => "Report spam stayed checked.");
        DialogButton(dialog, ChatCopy.Block).Patterns.Invoke.Pattern.Invoke();

        WaitForUpdates(1);
        Assert.AreEqual(ConversationActionStatus.Block, Server.Phone.ConversationUpdates[0].Action);
        WaitForGone("Row.c-ann");
    }

    /// <summary>
    /// A group's menu has no Block &amp; report spam.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Team_HasNoBlockItem()
    {
        await OpenListAsync();

        App.Find("Row.c-team").RightClick();
        App.Find("Conversation.Archive");
        App.Find("Conversation.Mute");
        App.Find("Conversation.MoveToTrash");
        Assert.IsNull(App.TryFind("Conversation.BlockAndReport"), "Team's menu offered Block & report spam.");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// Move to trash asks first: Cancel sends nothing and keeps the row; Move to trash sends a delete and the row goes.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task MoveToTrash_AsksThenRemovesRow()
    {
        await OpenListAsync();

        // Cancel
        PickFromRowMenu("Row.c-ann", "Conversation.MoveToTrash");
        DialogButton(App.Find("Dialog.Trash"), ChatCopy.Cancel).Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Dialog.Trash");
        Thread.Sleep(500);
        Assert.IsEmpty(Server.Phone.ConversationUpdates, "Cancel still sent the phone an update.");
        App.Find("Row.c-ann");

        // Move To Trash
        PickFromRowMenu("Row.c-ann", "Conversation.MoveToTrash");
        DialogButton(App.Find("Dialog.Trash"), ChatCopy.MoveToTrash).Patterns.Invoke.Pattern.Invoke();
        WaitForUpdates(1);
        Assert.AreEqual(ConversationActionStatus.Delete, Server.Phone.ConversationUpdates[0].Action);
        WaitForGone("Row.c-ann");
    }

    /// <summary>
    /// Shift + F10 on the focused row opens its menu.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ShiftF10_OnFocusedRow_OpensMenu()
    {
        await OpenListAsync();

        App.Find("Row.c-ann").Focus();
        Wait.Until(() => App.TryFind("Row.c-ann")?.Properties.HasKeyboardFocus.ValueOrDefault == true, AppDriver.DefaultTimeout,
            () => "Ann's row never took focus.");
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.F10);

        App.Find("Conversation.Archive");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// When the phone refuses, the failure banner shows and Ann's row stays; closing the banner hides it.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task PhoneRefuses_ShowsError_RowStays()
    {
        await OpenListAsync();
        Server.Phone.FailConversationUpdates = true;

        PickFromRowMenu("Row.c-ann", "Conversation.Archive");

        AutomationElement banner = App.Find("Main.ActionError");
        Wait.Until(() => AppDriver.Search(banner, e => AppDriver.SafeName(e) == ChatCopy.ActionFailed) is not null, AppDriver.DefaultTimeout,
            () => $"The banner didn't read '{ChatCopy.ActionFailed}'.");
        WaitForUpdates(1);
        App.Find("Row.c-ann");

        AppDriver.Search(banner, e => e.Properties.AutomationId.ValueOrDefault == "CloseButton")!.Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Main.ActionError");
    }

    // =========================================================================
    // CHAT HEADER AND DETAILS
    // =========================================================================

    /// <summary>
    /// The chat header's ⋮ (named More options) opens Ann's menu with Details first, then her actions; a right-click on
    /// the header opens the same menu.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task HeaderMenu_HasDetailsAndActions()
    {
        await OpenChatAsync("Row.c-ann");

        // The ⋮
        AutomationElement more = App.Find("Chat.More");
        Assert.AreEqual(ChatCopy.MoreOptions, AppDriver.SafeName(more));
        more.Patterns.Invoke.Pattern.Invoke();
        foreach (string item in (string[])["Details", "Archive", "Mute", "BlockAndReport", "MoveToTrash"])
        {
            App.Find("Conversation." + item);
        }

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        WaitForGone("Conversation.Details");

        // Right-Click On The Header
        App.Find("Chat.Header").RightClick();
        App.Find("Conversation.Details");
        App.Find("Conversation.MoveToTrash");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// Move to trash from the header's menu, once confirmed, closes the chat: the empty state shows, the header goes and
    /// the keyboard focus moves to the conversation list.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task HeaderTrash_ClosesChat()
    {
        await OpenChatAsync("Row.c-ann");

        Invoke("Chat.More");
        Invoke("Conversation.MoveToTrash");
        DialogButton(App.Find("Dialog.Trash"), ChatCopy.MoveToTrash).Patterns.Invoke.Pattern.Invoke();

        WaitForUpdates(1);
        Assert.AreEqual(ConversationActionStatus.Delete, Server.Phone.ConversationUpdates[0].Action);
        App.Find("Main.EmptyState");
        WaitForGone("Chat.Header");
        WaitForFocusIn("Main.ConversationList");
    }

    /// <summary>
    /// Team's Details lists Bo and Cy with their numbers, shows Mute off, and offers no Block &amp; report spam.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Details_Team_ListsBoAndCy_NoBlock()
    {
        await OpenChatAsync("Row.c-team");

        Invoke("Chat.More");
        Invoke("Conversation.Details");

        AutomationElement dialog = App.Find("Dialog.Details");
        foreach (string text in (string[])[ChatCopy.People, "Bo", "+15550000002", "Cy", "+15550000003"])
        {
            Wait.Until(() => AppDriver.Search(dialog, e => AppDriver.SafeName(e) == text) is not null, AppDriver.DefaultTimeout,
                () => $"Details didn't show '{text}'.");
        }

        Assert.AreEqual(ToggleState.Off, App.Find("Details.Mute").Patterns.Toggle.Pattern.ToggleState.Value);
        Assert.IsNull(App.TryFind("Details.Block"), "Team's Details offered Block & report spam.");

        DialogButton(dialog, ChatCopy.Close).Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Dialog.Details");
    }

    /// <summary>
    /// Flipping Details' Mute on sends the phone a mute at once, and the row shows the muted icon.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Details_MuteToggle_SendsMute()
    {
        await OpenChatAsync("Row.c-ann");
        Invoke("Chat.More");
        Invoke("Conversation.Details");

        App.Find("Details.Mute").Patterns.Toggle.Pattern.Toggle();

        WaitForUpdates(1);
        Assert.AreEqual(ConversationMuteStatus.Mute, Server.Phone.ConversationUpdates[0].UpdateData.Mute);
        App.Find("Row.Muted.c-ann");
        Assert.AreEqual(ToggleState.On, App.Find("Details.Mute").Patterns.Toggle.Pattern.ToggleState.Value);
    }

    /// <summary>
    /// When the phone refuses the mute, Details' toggle flips back off without sending a second request.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Details_MuteFails_TogglesBack()
    {
        await OpenChatAsync("Row.c-ann");
        Server.Phone.FailConversationUpdates = true;
        Invoke("Chat.More");
        Invoke("Conversation.Details");

        AutomationElement toggle = App.Find("Details.Mute");
        toggle.Patterns.Toggle.Pattern.Toggle();

        WaitForUpdates(1);
        Wait.Until(() => toggle.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.Off, AppDriver.DefaultTimeout,
            () => "Mute stayed on after the phone refused.");
        Thread.Sleep(500);
        Assert.HasCount(1, Server.Phone.ConversationUpdates, "Flipping the toggle back sent the phone another update.");
    }

    /// <summary>
    /// Details' Block &amp; report spam closes Details, then asks as the menu does; Block sends a block-and-report.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Details_Block_ClosesThenAsks()
    {
        await OpenChatAsync("Row.c-ann");
        Invoke("Chat.More");
        Invoke("Conversation.Details");

        Invoke("Details.Block");
        AutomationElement dialog = App.Find("Dialog.Block");
        Assert.IsNull(App.TryFind("Dialog.Details"), "Details stayed open under the Block dialog.");
        DialogButton(dialog, ChatCopy.Block).Patterns.Invoke.Pattern.Invoke();

        WaitForUpdates(1);
        Assert.AreEqual(ConversationActionStatus.BlockAndReport, Server.Phone.ConversationUpdates[0].Action);
    }

    // =========================================================================
    // SHORTCUTS
    // =========================================================================

    /// <summary>
    /// Ctrl + Shift + o in the message box opens the open chat's Details.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CtrlShiftO_OpensDetails()
    {
        await OpenChatAsync("Row.c-ann");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_O);

        App.Find("Dialog.Details");
        App.Find("Details.Block");
    }

    /// <summary>
    /// Ctrl + d asks first; Move to trash sends a delete and closes the chat, and the keyboard focus moves to the
    /// conversation list.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CtrlD_AsksThenTrashes()
    {
        await OpenChatAsync("Row.c-ann");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_D);
        DialogButton(App.Find("Dialog.Trash"), ChatCopy.MoveToTrash).Patterns.Invoke.Pattern.Invoke();

        WaitForUpdates(1);
        Assert.AreEqual(ConversationActionStatus.Delete, Server.Phone.ConversationUpdates[0].Action);
        App.Find("Main.EmptyState");
        WaitForGone("Row.c-ann");
        WaitForFocusIn("Main.ConversationList");
    }

    /// <summary>
    /// A second Ctrl + d while the trash dialog is open opens no second dialog: once Cancel closes it, none is left and
    /// nothing was sent.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CtrlD_Twice_OneDialog()
    {
        await OpenChatAsync("Row.c-ann");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_D);
        AutomationElement dialog = App.Find("Dialog.Trash");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_D);
        Thread.Sleep(500);

        DialogButton(dialog, ChatCopy.Cancel).Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Dialog.Trash");
        Thread.Sleep(500);
        Assert.IsNull(App.TryFind("Dialog.Trash"), "A second trash dialog showed after the first closed.");
        Assert.IsEmpty(Server.Phone.ConversationUpdates, "Cancel still sent the phone an update.");
        App.Find("Chat.Draft");
    }

    /// <summary>
    /// Ctrl + Alt + h in the message box archives the open chat, which closes it.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CtrlAltH_Archives()
    {
        await OpenChatAsync("Row.c-ann");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_H);

        WaitForUpdates(1);
        Assert.AreEqual(ConversationStatus.Archived, Server.Phone.ConversationUpdates[0].UpdateData.Status);
        App.Find("Main.EmptyState");
        WaitForGone("Row.c-ann");
    }

    /// <summary>
    /// Ctrl + Alt + h on an open spam chat does nothing, as its header menu offers no Archive. Ctrl + Shift + o then
    /// opens its Details, so the keys did reach the app.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CtrlAltH_SpamChat_SendsNothing()
    {
        await OpenListAsync();
        Invoke("Main.ProfileButton");
        Invoke("Main.Profile.SpamBlocked");
        App.WaitForText("Main.ListTitle", ChatCopy.SpamAndBlocked);
        App.Find("Row.c-spam").Patterns.SelectionItem.Pattern.Select();
        App.Find("Chat.Header");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_H);
        Thread.Sleep(500);
        Assert.IsEmpty(Server.Phone.ConversationUpdates, "Ctrl + Alt + h sent the phone an update for a spam chat.");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_O);
        App.Find("Dialog.Details");
        App.Find("Row.c-spam");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Opens A Row's Chat From The List; Opening It Puts The Cursor In The Message Box, Where Keys Then Go
    private async Task OpenChatAsync(string rowId)
    {
        await OpenListAsync();
        App.Find(rowId).Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => App.TryFind("Chat.Draft")?.Properties.HasKeyboardFocus.ValueOrDefault == true, AppDriver.DefaultTimeout,
            () => "The message box never took focus.");
    }

    // Pairs, Opens The Window And Brings It To The Front (Mouse And Keys Go To The Foreground Window)
    private async Task OpenListAsync()
    {
        await OpenMainAsync();
        App.Find("Row.c-ann");
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
    }

    // Right-Clicks A Row, Then Picks One Of Its Menu Items
    private void PickFromRowMenu(string rowId, string itemId)
    {
        App.Find(rowId).RightClick();
        Invoke(itemId);
    }

    // A Row's ⋮ Once The Hover Has Shown It (Collapsed Until Then, So Not Found) And Given It Its Space
    private AutomationElement ShownMore(string automationId)
    {
        AutomationElement? more = null;
        Wait.Until(() => (more = App.TryFind(automationId)) is not null && more.BoundingRectangle.Width > 0, AppDriver.DefaultTimeout,
            () => $"The hovered row's ⋮ ('{automationId}') never showed.");
        return more!;
    }

    // Widens The List With Its Resize Edge (Six Steps Of 16 Effective Pixels), So A Row That Only Sizes To Its Text Ends
    // Short Of The List's Right Edge
    private void WidenList()
    {
        App.Find("Main.ListSplitter").Focus();
        for (int i = 0; i < 6; i++)
        {
            Keyboard.Type(VirtualKeyShort.RIGHT);
        }

        Thread.Sleep(500);
    }

    private void Invoke(string automationId) => App.Find(automationId).Patterns.Invoke.Pattern.Invoke();

    private void WaitForUpdates(int count) =>
        Wait.Until(() => Server.Phone.ConversationUpdates.Count == count, AppDriver.DefaultTimeout,
            () => $"The phone got {Server.Phone.ConversationUpdates.Count} conversation updates, not {count}.");

    // A Dialog's Button, By Its Text
    private static AutomationElement DialogButton(AutomationElement dialog, string name)
    {
        AutomationElement? button = null;
        Wait.Until(() => (button = AppDriver.Search(dialog, e => e.ControlType == ControlType.Button && AppDriver.SafeName(e) == name)) is not null,
            AppDriver.DefaultTimeout, () => $"The dialog has no '{name}' button.");
        return button!;
    }
}
