using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace GMessages.App.UITests;

/// <summary>
/// Conversation actions from the list. The row menu opens with a right-click, the hover ⋮ button or Shift + F10.
/// The tests cover archive and the Archived view, mute, block &amp; report spam and move to trash.
/// They also cover the confirm dialogs and the failure banner.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class ConversationActionTests : UiTest
{
    private const string AnnPhone = "+15550000001";

    // The right padding of a row. A row without a hover has its time at most this far from the right edge.
    private const int RowRightPadding = 16;

    // The time that a test watches for a toast from a message that must not notify.
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The Archive item of the row menu moves Ann out of the inbox. The Archived item of the profile menu lists her.
    /// Her Unarchive item returns her to the inbox.
    /// </summary>
    [TestMethod]
    public async Task RowMenu_Archive_MovesToArchived_ThenUnarchive()
    {
        await OpenListAsync();

        PickFromRowMenu("Row.c-ann", "Conversation.Archive");
        WaitForGone("Row.c-ann");
        WaitForUpdates(1);
        Assert.AreEqual(ConversationStatus.Archived, Server.Phone.ConversationUpdates[0].UpdateData.Status);

        Invoke("Main.ProfileButton");
        Invoke("Main.Profile.Archived");
        App.WaitForText("Main.ListTitle", ChatCopy.Archived);
        App.Find("Main.ArchivedBack");

        PickFromRowMenu("Row.c-ann", "Conversation.Unarchive");
        WaitForGone("Row.c-ann");
        WaitForUpdates(2);
        Assert.AreEqual(ConversationStatus.Active, Server.Phone.ConversationUpdates[1].UpdateData.Status);

        Invoke("Main.ArchivedBack");
        WaitForGone("Main.ListTitle");
        App.Find("Row.c-ann");
    }

    /// <summary>
    /// Archiving Ann shows "Chat archived" with Undo, and the focus stays where it was. Undo unarchives her on the
    /// phone, her row returns to the inbox and the bar goes.
    /// </summary>
    [TestMethod]
    public async Task Archive_Undo_PutsTheRowBack()
    {
        await OpenListAsync();

        PickFromRowMenu("Row.c-ann", "Conversation.Archive");
        WaitForGone("Row.c-ann");
        AutomationElement bar = App.Find("Main.Undo");
        Assert.AreEqual(ChatCopy.ConversationArchived, bar.Name);
        App.Find("Main.Undo.Button");
        string focused = App.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault ?? string.Empty;
        Assert.AreNotEqual("Main.Undo.Button", focused, "The Undo bar took the focus.");

        Invoke("Main.Undo.Button");
        WaitForUpdates(2);
        Assert.AreEqual(ConversationStatus.Active, Server.Phone.ConversationUpdates[1].UpdateData.Status);
        App.Find("Row.c-ann");
        WaitForGone("Main.Undo");
    }

    /// <summary>
    /// With the mouse over the row of Ann, its ⋮ button opens her menu.
    /// </summary>
    [TestMethod]
    public async Task RowHover_MoreButton_OpensMenu()
    {
        await OpenListAsync();

        // The pointer moves onto the row and then onto the ⋮ button. The button is collapsed until then,
        // so it takes no clicks.
        System.Drawing.Rectangle row = App.Find("Row.c-ann").BoundingRectangle;
        PointerInput.Hover(row);
        AutomationElement more = ShownMore("Row.More.c-ann");
        PointerInput.Hover(more.BoundingRectangle);

        // The wait lets the ⋮ button fade in, which takes 167 ms. The button takes clicks only after the fade-in ends.
        Thread.Sleep(500);

        Assert.AreEqual(ChatCopy.MoreOptions, AppDriver.SafeName(more));
        more.Click();

        App.Find("Conversation.Mute");
    }

    /// <summary>
    /// The pointer moves from the name of Ann onto blank space beside the time, and the ⋮ button stays. The exit from
    /// the name is not the exit from the row. A click on the button opens her menu and not her chat.
    /// </summary>
    [TestMethod]
    public async Task RowHover_NameToBlankSpace_KeepsMoreButton()
    {
        await OpenListAsync();

        AutomationElement row   = App.Find("Row.c-ann");
        AutomationElement? name = null;
        Wait.Until(() => (name = AppDriver.Search(row, e => e.ControlType == ControlType.Text && AppDriver.SafeName(e) == "Ann Lee")) is not null,
            AppDriver.DefaultTimeout, () => "Ann's row shows no name.");

        // The pointer moves from the name to blank space left of the ⋮ button on the lower line.
        // That space is under the time, where no icon shows.
        PointerInput.Hover(name!.BoundingRectangle);
        AutomationElement more             = ShownMore("Row.More.c-ann");
        System.Drawing.Rectangle moreRect  = more.BoundingRectangle;
        System.Drawing.Rectangle rowRect   = row.BoundingRectangle;
        PointerInput.Hover(new System.Drawing.Rectangle(moreRect.Left - 20, rowRect.Bottom - 16, 2, 2));

        // The wait lets any fade end before the click. The fade takes 167 ms.
        Thread.Sleep(500);
        more.Click();

        App.Find("Conversation.Mute");
        Assert.IsNull(App.TryFind("Chat.Draft"), "The click opened Ann's chat: the ⋮ had hidden.");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// A pointer that leaves the window straight from the row of Ann hides the ⋮ button. The exit reports a point
    /// inside the row, so a bounds check would keep the button. A click where it was opens her chat, not her menu.
    /// </summary>
    [TestMethod]
    public async Task RowHover_LeaveWindow_HidesMoreButton()
    {
        await OpenListAsync();

        System.Drawing.Rectangle row = App.Find("Row.c-ann").BoundingRectangle;
        System.Drawing.Rectangle win = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).BoundingRectangle;
        PointerInput.Hover(row);
        System.Drawing.Rectangle moreRect = ShownMore("Row.More.c-ann").BoundingRectangle;

        PointerInput.LeaveLeft(win);

        // The click comes where the ⋮ button was, after it has collapsed.
        WaitForGone("Row.More.c-ann");
        FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(moreRect.X + (moreRect.Width / 2), moreRect.Y + (moreRect.Height / 2)));

        App.Find("Chat.Draft");
        Assert.IsNull(App.TryFind("Conversation.Mute"), "The ⋮ stayed after the pointer left the window.");
    }

    /// <summary>
    /// With no row hovered, the time of Ann sits at the right edge of the row, inside the right padding.
    /// Her ⋮ button is collapsed. It takes no space and is not in the UI Automation tree.
    /// </summary>
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
    /// With no row hovered, the times of Team and Ann end at the same x position. The names have different lengths.
    /// </summary>
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
    /// A hover on the row of Ann gives her ⋮ button its space at the right edge. Only her time slides left to make
    /// room, and the time of Team stays. When the pointer leaves the window, the button collapses and the time of Ann
    /// returns to the right edge.
    /// </summary>
    [TestMethod]
    public async Task RowHover_TimeMovesLeftForMore()
    {
        await OpenListAsync();

        System.Drawing.Rectangle row  = App.Find("Row.c-ann").BoundingRectangle;
        System.Drawing.Rectangle win  = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).BoundingRectangle;
        AutomationElement annTime     = App.Find("Row.Time.c-ann");
        AutomationElement teamTime    = App.Find("Row.Time.c-team");
        int annRight                  = annTime.BoundingRectangle.Right;
        int teamRight                 = teamTime.BoundingRectangle.Right;

        // The time of Ann slides left until it ends where the ⋮ button starts.
        PointerInput.Hover(row);
        System.Drawing.Rectangle hoveredMore = ShownMore("Row.More.c-ann").BoundingRectangle;
        Wait.Until(() => annTime.BoundingRectangle.Right <= hoveredMore.Left, AppDriver.DefaultTimeout,
            () => $"Ann's time ({annTime.BoundingRectangle}) still overlaps the ⋮ ({hoveredMore}).");
        int hoveredRight = annTime.BoundingRectangle.Right;
        Assert.IsGreaterThanOrEqualTo(32, annRight - hoveredRight, $"Ann's time only moved from {annRight} to {hoveredRight}.");
        Assert.IsLessThanOrEqualTo(1, Math.Abs(teamTime.BoundingRectangle.Right - teamRight), "Team's time moved while Ann's row was hovered.");

        PointerInput.LeaveLeft(win);
        WaitForGone("Row.More.c-ann");
        Assert.IsLessThanOrEqualTo(1, Math.Abs(annTime.BoundingRectangle.Right - annRight), "Ann's time didn't slide back.");
    }

    /// <summary>
    /// Mute sends the phone a mute and the row shows the muted icon. The menu then offers Unmute instead.
    /// </summary>
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
    /// The bell in the chat header mutes the chat. The phone gets a mute and the row shows the muted icon.
    /// The bell then reads Unmute and unmutes the chat again.
    /// </summary>
    [TestMethod]
    public async Task HeaderBell_MutesThenUnmutes()
    {
        await OpenChatAsync("Row.c-ann");
        Assert.AreEqual(ChatCopy.Mute, AppDriver.SafeName(App.Find("Chat.Mute")));

        Invoke("Chat.Mute");
        WaitForUpdates(1);
        Assert.AreEqual(ConversationMuteStatus.Mute, Server.Phone.ConversationUpdates[0].UpdateData.Mute);
        App.Find("Row.Muted.c-ann");
        Wait.Until(() => AppDriver.SafeName(App.Find("Chat.Mute")) == ChatCopy.Unmute, AppDriver.DefaultTimeout, () => "The bell didn't change to Unmute.");

        Invoke("Chat.Mute");
        WaitForUpdates(2);
        Assert.AreEqual(ConversationMuteStatus.Unmute, Server.Phone.ConversationUpdates[1].UpdateData.Mute);
    }

    /// <summary>
    /// A message in a muted chat shows no toast but still makes the row unread. A message in another chat still toasts.
    /// </summary>
    [TestMethod]
    public async Task MutedChat_NoToast_RowUnread()
    {
        var toasts = new ToastDriver(App.Automation);
        await OpenListAsync();

        // The test reads the chat of Ann and then leaves it, so no chat is on screen.
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        App.Find("Chat.Draft");
        Wait.Until(() => !AppDriver.SafeName(App.TryFind("Row.c-ann")).Contains("Unread", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => "Ann's row stayed unread after opening her chat.");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        WaitForGone("Chat.Draft");

        PickFromRowMenu("Row.c-ann", "Conversation.Mute");
        App.Find("Row.Muted.c-ann");
        Server.Phone.PushIncoming("c-ann", "quiet please");

        toasts.AssertNone("quiet please", QuietPeriod);
        string last = "";
        Wait.Until(() => (last = AppDriver.SafeName(App.TryFind("Row.c-ann"))).EndsWith(", Unread", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => $"Ann's row read '{last}', not unread.");

        // A live message in another chat must still toast. This proves that the quiet check was meaningful.
        Server.Phone.PushIncoming("c-team", "still here");
        toasts.WaitFor(AppDriver.DefaultTimeout, "Team", "Bo: still here");
    }

    /// <summary>
    /// Block &amp; report spam asks for confirmation first, with Report spam checked. Block sends a block-and-report
    /// with the number of Ann, and she moves to Spam &amp; blocked.
    /// </summary>
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
    /// A cleared Report spam box before Block sends a plain block.
    /// </summary>
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
    /// The menu of a group has no Block &amp; report spam item.
    /// </summary>
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
    /// Move to trash asks for confirmation first. Cancel sends nothing and keeps the row. Move to trash sends a delete
    /// and the row goes.
    /// </summary>
    [TestMethod]
    public async Task MoveToTrash_AsksThenRemovesRow()
    {
        await OpenListAsync();

        PickFromRowMenu("Row.c-ann", "Conversation.MoveToTrash");
        DialogButton(App.Find("Dialog.Trash"), ChatCopy.Cancel).Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Dialog.Trash");
        Thread.Sleep(500);
        Assert.IsEmpty(Server.Phone.ConversationUpdates, "Cancel still sent the phone an update.");
        App.Find("Row.c-ann");

        PickFromRowMenu("Row.c-ann", "Conversation.MoveToTrash");
        DialogButton(App.Find("Dialog.Trash"), ChatCopy.MoveToTrash).Patterns.Invoke.Pattern.Invoke();
        WaitForUpdates(1);
        Assert.AreEqual(ConversationActionStatus.Delete, Server.Phone.ConversationUpdates[0].Action);
        WaitForGone("Row.c-ann");
    }

    /// <summary>
    /// Shift + F10 on the focused row opens its menu.
    /// </summary>
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
    /// When the phone refuses, the failure banner shows and the row of Ann stays. Closing the banner hides it.
    /// </summary>
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
    /// The ⋮ button in the chat header, named More options, opens the menu of Ann. Details is first, then her
    /// actions. A right-click on the header opens the same menu.
    /// </summary>
    [TestMethod]
    public async Task HeaderMenu_HasDetailsAndActions()
    {
        await OpenChatAsync("Row.c-ann");

        AutomationElement more = App.Find("Chat.More");
        Assert.AreEqual(ChatCopy.MoreOptions, AppDriver.SafeName(more));
        more.Patterns.Invoke.Pattern.Invoke();
        foreach (string item in (string[])["Details", "Archive", "Mute", "BlockAndReport", "MoveToTrash"])
        {
            App.Find("Conversation." + item);
        }

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        WaitForGone("Conversation.Details");

        App.Find("Chat.Header").RightClick();
        App.Find("Conversation.Details");
        App.Find("Conversation.MoveToTrash");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// The menu of a ⋮ button opens downward. The menu of the chat header opens below its button with the right
    /// edges lined up. The menu of a row opens below its ⋮ button too.
    /// </summary>
    [TestMethod]
    public async Task MoreMenus_OpenBelowTheirButton()
    {
        await OpenChatAsync("Row.c-ann");

        AutomationElement more         = App.Find("Chat.More");
        System.Drawing.Rectangle under = more.BoundingRectangle;
        more.Patterns.Invoke.Pattern.Invoke();

        // The test waits because the WinUI 2.8 menu slides down into place as it opens.
        int top = 0;
        Wait.Until(() => (top = App.Find("Conversation.Details").BoundingRectangle.Top) >= under.Bottom, AppDriver.DefaultTimeout,
            () => $"The header menu's top ({top}) isn't below its button's bottom ({under.Bottom}).");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        WaitForGone("Conversation.Details");

        // The ⋮ button of a row shows on hover.
        AutomationElement row = App.Find("Row.c-team");
        PointerInput.Hover(row.BoundingRectangle);
        AutomationElement rowMore       = App.Find("Row.More.c-team");
        System.Drawing.Rectangle button = rowMore.BoundingRectangle;
        rowMore.Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => (top = App.Find("Conversation.Archive").BoundingRectangle.Top) >= button.Bottom, AppDriver.DefaultTimeout,
            () => $"The row menu's top ({top}) isn't below its ⋮'s bottom ({button.Bottom}).");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// A confirmed Move to trash from the header menu closes the chat. The empty state shows, the header goes and the
    /// keyboard focus moves to the conversation list.
    /// </summary>
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
    /// The Details of Team list Bo and Cy with their numbers and show Mute off. They offer no Block &amp; report
    /// spam.
    /// </summary>
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
    /// Turning on Mute in Details sends the phone a mute at once, and the row shows the muted icon.
    /// </summary>
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
    /// When the phone refuses the mute, the toggle in Details flips back off. The app sends no second request.
    /// </summary>
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
    /// Block &amp; report spam in Details closes Details and then asks for confirmation as the menu does. Block sends
    /// a block-and-report.
    /// </summary>
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
    /// Ctrl + Shift + o in the message box opens the Details of the open chat.
    /// </summary>
    [TestMethod]
    public async Task CtrlShiftO_OpensDetails()
    {
        await OpenChatAsync("Row.c-ann");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_O);

        App.Find("Dialog.Details");
        App.Find("Details.Block");
    }

    /// <summary>
    /// Ctrl + d asks for confirmation first. Move to trash sends a delete and closes the chat, and the keyboard focus
    /// moves to the conversation list.
    /// </summary>
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
    /// A second Ctrl + d while the trash dialog is open opens no second dialog. After Cancel closes the dialog, no
    /// dialog is left and the app sent nothing.
    /// </summary>
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
    /// Ctrl + Alt + h in the message box archives the open chat and closes it.
    /// </summary>
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
    /// Ctrl + Z puts the archived chat back in the inbox, also after the Undo bar has gone. This works at any time in
    /// the session.
    /// </summary>
    [TestMethod]
    public async Task CtrlZ_AfterTheBarHid_UnarchivesAgain()
    {
        await OpenChatAsync("Row.c-ann");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_H);
        WaitForUpdates(1);
        WaitForGone("Row.c-ann");
        Wait.Until(() => App.TryFind("Main.Undo") is null, MainViewModel.UndoTime + AppDriver.DefaultTimeout, () => "The Undo bar never hid.");

        // The keys go to the list. In the message box, Ctrl + Z undoes typing.
        App.Find("Main.ConversationList").Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);

        WaitForUpdates(2);
        Assert.AreEqual(ConversationStatus.Active, Server.Phone.ConversationUpdates[1].UpdateData.Status);
        App.Find("Row.c-ann");
    }

    /// <summary>
    /// Ctrl + Alt + h on an open spam chat does nothing, because its header menu offers no Archive. Ctrl + Shift + o
    /// then opens its Details, which proves that the keys reached the app.
    /// </summary>
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

    // Opens the chat of a row from the list. The chat puts the cursor in the message box, and the keys go there.
    private async Task OpenChatAsync(string rowId)
    {
        await OpenListAsync();
        App.Find(rowId).Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => App.TryFind("Chat.Draft")?.Properties.HasKeyboardFocus.ValueOrDefault == true, AppDriver.DefaultTimeout,
            () => "The message box never took focus.");
    }

    // Pairs, opens the window and brings it to the front. Mouse and key input goes to the foreground window.
    private async Task OpenListAsync()
    {
        await OpenMainAsync();
        App.Find("Row.c-ann");
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
    }

    // Right-clicks a row and picks one of its menu items.
    private void PickFromRowMenu(string rowId, string itemId)
    {
        App.Find(rowId).RightClick();
        Invoke(itemId);
    }

    // Returns the ⋮ button of a row after the hover shows it and gives it space. The button is collapsed until then,
    // so a search does not find it.
    private AutomationElement ShownMore(string automationId)
    {
        AutomationElement? more = null;
        Wait.Until(() => (more = App.TryFind(automationId)) is not null && more.BoundingRectangle.Width > 0, AppDriver.DefaultTimeout,
            () => $"The hovered row's ⋮ ('{automationId}') never showed.");
        return more!;
    }

    // Widens the list with its resize edge in six steps of 16 effective pixels. A row that sizes only to its text
    // then ends short of the right edge of the list.
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

    // Finds a button of a dialog by its text.
    private static AutomationElement DialogButton(AutomationElement dialog, string name)
    {
        AutomationElement? button = null;
        Wait.Until(() => (button = AppDriver.Search(dialog, e => e.ControlType == ControlType.Button && AppDriver.SafeName(e) == name)) is not null,
            AppDriver.DefaultTimeout, () => $"The dialog has no '{name}' button.");
        return button!;
    }
}
