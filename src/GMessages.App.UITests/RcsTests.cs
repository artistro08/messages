using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.App.UITests.FakeGoogle;
using GMessages.Presentation;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;
using Keyboard = FlaUI.Core.Input.Keyboard;
using Mouse = FlaUI.Core.Input.Mouse;

namespace GMessages.App.UITests;

/// <summary>
/// RCS in the chat pane: the status line, RCS and SMS bubbles, the lock, reactions, replies, the hover strip and typing.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class RcsTests : UiTest
{
    private const string AnnFirst = "Hi, it's Ann";
    private const string AnnLast  = "Are you free later?";
    private const string MyReply  = "Hey Ann";
    private const string AnnPhone = "+15550000001";

    /// <summary>
    /// A sent message reads "Sent" once the phone echoes it, then "Delivered", then "Read" as the phone reports them.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task SentRcs_ShowsDeliveredThenRead()
    {
        await OpenChatAsync("Row.c-ann");

        App.Find("Chat.Draft").Focus();
        Keyboard.Type("On my way");
        Keyboard.Type(VirtualKeyShort.RETURN);
        Wait.Until(() => Server.Phone.Sent.Count == 1, AppDriver.DefaultTimeout, () => "The phone never got the message.");
        string id = Newest("c-ann").MessageID;
        App.WaitForText("Status." + id, ChatCopy.Sent);

        Server.Phone.PushStatus("c-ann", id, MessageStatusType.OutgoingDelivered);
        App.WaitForText("Status." + id, ChatCopy.Delivered);

        Server.Phone.PushStatus("c-ann", id, MessageStatusType.OutgoingDisplayed);
        App.WaitForText("Status." + id, ChatCopy.Read);
    }

    /// <summary>
    /// A bubble tells screen readers what it is: Team's SMS/MMS bubbles are "Text message", Ann's RCS ones
    /// "Chat message".
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Sms_And_Rcs_HaveTheirItemTypes()
    {
        await OpenChatAsync("Row.c-team");
        Assert.AreEqual(ChatCopy.TextMessage, ItemType("Bubble." + Newest("c-team").MessageID));

        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        Assert.AreEqual(ChatCopy.ChatMessage, ItemType("Bubble." + Find("c-ann", AnnLast).MessageID));
    }

    /// <summary>
    /// My bubble sits on the right and Ann's on the left. Every bubble showed on the left while the side's visual states
    /// sat below the bubble's root element, where GoToState never finds them.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task SentAndReceived_SitOnTheirSides()
    {
        await OpenChatAsync("Row.c-ann");

        System.Drawing.Rectangle list     = App.Find("Chat.Messages").BoundingRectangle;
        System.Drawing.Rectangle mine     = App.Find("Bubble." + Find("c-ann", MyReply).MessageID).BoundingRectangle;
        System.Drawing.Rectangle received = App.Find("Bubble." + Find("c-ann", AnnFirst).MessageID).BoundingRectangle;
        int middle                        = list.Left + (list.Width / 2);

        Assert.IsTrue(mine.Left > middle, $"My bubble ({mine}) isn't on the right of the list ({list}).");
        Assert.IsTrue(received.Right < middle, $"Ann's bubble ({received}) isn't on the left of the list ({list}).");
    }

    /// <summary>
    /// A link in a message's text shows as a link named by its text. Clicking it opens it (a test run only logs that,
    /// with no browser) and doesn't also click the bubble: the newest bubble's caption row stays.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task LinkInText_Click_OpensWithoutTogglingTheCaption()
    {
        await OpenChatAsync("Row.c-ann");

        // Plain http To A Name That Never Resolves: No Preview Is Fetched For It
        Message message = Server.Phone.PushIncoming("c-ann", "read http://example.invalid/page today");
        AutomationElement link = App.Find($"Link.{message.MessageID}.0");
        Assert.AreEqual("http://example.invalid/page", AppDriver.SafeName(link));
        App.Find("Caption." + message.MessageID);

        link.Click();
        Wait.Until(() => ReadLog().Contains("Link opened (http, launched: False)", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => "The link click was never logged.");

        // A Bubble Click Would Hide The Caption A Turn Later; Give It Time To Show It Didn't
        Thread.Sleep(500);
        Assert.IsNotNull(App.TryFind("Caption." + message.MessageID), "The link click also clicked the bubble (its caption row hid).");
    }

    /// <summary>
    /// A link in an inbox chat gets a preview card named by the linked page's <c>og:title</c>.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task LinkMessage_ShowsPreviewCard()
    {
        await OpenChatAsync("Row.c-ann");

        Message link = Server.Phone.PushIncoming("c-ann", $"look http://127.0.0.1:{Server.BaseUrl.Port}/preview-page");
        App.WaitForText("Preview." + link.MessageID, FakeGoogleServer.PreviewPageTitle);
    }

    /// <summary>
    /// Ann's encrypted chat shows the lock after her name; Team's SMS group doesn't.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task EncryptedChat_ShowsLock()
    {
        await OpenChatAsync("Row.c-ann");
        Assert.AreEqual(ChatCopy.EndToEndEncrypted, AppDriver.SafeName(App.Find("Chat.Encrypted")));

        App.Find("Row.c-team").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Team");
        WaitForGone("Chat.Encrypted");
    }

    /// <summary>
    /// The bubble menu's React opens the picker; 👍 sends the phone an Add of 👍, and the pill under the bubble shows it.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task React_FromMenu_PhoneGetsAdd_PillShows()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Find("c-ann", AnnLast).MessageID;

        App.Find("Bubble." + id).RightClick();
        App.Find("Bubble.React").Patterns.Invoke.Pattern.Invoke();
        App.Find("ReactionPicker");
        App.Find("ReactionPicker.1").Patterns.Invoke.Pattern.Invoke();

        Wait.Until(() => Server.Phone.Reactions.Count == 1, AppDriver.DefaultTimeout, () => "The phone never got the reaction.");
        SendReactionRequest reaction = Server.Phone.Reactions[0];
        Assert.AreEqual(SendReactionRequest.Types.Action.Add, reaction.Action);
        Assert.AreEqual(EmojiType.Like, reaction.ReactionData.Type);
        Assert.AreEqual(id, reaction.MessageID);

        string last = "";
        Wait.Until(() => (last = AppDriver.SafeName(App.TryFind("Reactions." + id))).Contains("\U0001F44D", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => $"The reaction pill read '{last}', without 👍.");
    }

    /// <summary>
    /// Shift + F10 on a focused bubble opens its menu, and Copy puts the bubble's text on the clipboard (pasted into the
    /// message box to check).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Copy_FromKeyboardMenu_CopiesText()
    {
        await OpenChatAsync("Row.c-ann");
        FocusLastRow();

        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.F10);
        App.Find("Bubble.Copy").Patterns.Invoke.Pattern.Invoke();

        App.Find("Chat.Draft").Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        string last = "";
        Wait.Until(() => (last = App.TryFind("Chat.Draft")?.Patterns.Value.Pattern.Value.ValueOrDefault ?? "") == AnnLast, AppDriver.DefaultTimeout,
            () => $"The message box read '{last}' after pasting, not '{AnnLast}'.");
    }

    /// <summary>
    /// Shift + + on a focused bubble opens the reaction picker.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ShiftPlus_OnFocusedBubble_OpensPicker()
    {
        await OpenChatAsync("Row.c-ann");
        FocusLastRow();

        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.OEM_PLUS);

        App.Find("ReactionPicker");
        App.Find("ReactionPicker.7");

        // Love Shows Google's Red Heart, Not Heart Eyes
        Assert.AreEqual("❤️", App.Find("ReactionPicker.2").Name);
    }

    /// <summary>
    /// The bubble menu's Reply shows the reply chip and moves to the message box; the send carries the quoted message's
    /// ID, and the sent bubble shows the quote.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Reply_FromMenu_SendsReplyPayload_QuoteShows()
    {
        await OpenChatAsync("Row.c-ann");
        string quoted = Find("c-ann", AnnLast).MessageID;

        App.Find("Bubble." + quoted).RightClick();
        App.Find("Bubble.Reply").Patterns.Invoke.Pattern.Invoke();
        App.WaitForText("Chat.ReplyingTo", AnnLast);
        WaitForFocus("Chat.Draft");

        Keyboard.Type("Yes");
        Keyboard.Type(VirtualKeyShort.RETURN);

        Wait.Until(() => Server.Phone.Sent.Count == 1, AppDriver.DefaultTimeout, () => "The phone never got the reply.");
        Assert.AreEqual(quoted, Server.Phone.Sent[0].Reply?.MessageID, "The send didn't carry the quoted message's ID.");
        WaitForGone("Chat.ReplyingTo");
        App.WaitForText("Quote." + Newest("c-ann").MessageID, AnnLast);
    }

    /// <summary>
    /// Ctrl + Enter on a focused bubble starts a reply to it and moves to the message box.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CtrlEnter_OnFocusedBubble_StartsReply()
    {
        await OpenChatAsync("Row.c-ann");
        FocusLastRow();

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.RETURN);

        App.WaitForText("Chat.ReplyingTo", AnnLast);
        WaitForFocus("Chat.Draft");
    }

    /// <summary>
    /// With the mouse over Ann's last bubble, the hover strip's Reply starts a reply to it.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task HoverBubble_Reply_StartsReply()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Find("c-ann", AnnLast).MessageID;

        AutomationElement reply = HoverStripButton(id, "HoverReply.");
        Assert.AreEqual(ChatCopy.Reply, AppDriver.SafeName(reply));
        reply.Click();

        App.WaitForText("Chat.ReplyingTo", AnnLast);
    }

    /// <summary>
    /// With the mouse over Ann's last bubble, the hover strip's React opens the reaction picker.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task HoverBubble_React_OpensPicker()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Find("c-ann", AnnLast).MessageID;

        AutomationElement react = HoverStripButton(id, "HoverReact.");
        Assert.AreEqual(ChatCopy.React, AppDriver.SafeName(react));
        react.Click();

        App.Find("ReactionPicker");
        App.Find("ReactionPicker.1");
    }

    /// <summary>
    /// With the mouse over Ann's last bubble, the hover strip's ⋮ opens the bubble menu.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task HoverBubble_More_OpensMenu()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Find("c-ann", AnnLast).MessageID;

        AutomationElement more = HoverStripButton(id, "HoverMore.");
        Assert.AreEqual(ChatCopy.MoreOptions, AppDriver.SafeName(more));
        more.Click();

        App.Find("Bubble.Copy");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// The hover strip's space is always kept: hovering Ann's newest bubble moves neither it nor the bubble above it in its
    /// group, and the strip sits right of the received bubble.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task HoverStrip_DoesNotMoveBubbles()
    {
        await OpenChatAsync("Row.c-ann");

        // A Second Message From Ann, A Second After Her Last: The Two Make One Group
        Message newest                           = Server.Phone.PushIncoming("c-ann", "See you then");
        string neighbor_id                       = Find("c-ann", AnnLast).MessageID;
        App.Find("Bubble." + newest.MessageID);

        // Settle Wait For The List To Finish Scrolling To The New Bubble
        Thread.Sleep(1000);
        System.Drawing.Rectangle bubble_before  = App.Find("Bubble." + newest.MessageID).BoundingRectangle;
        System.Drawing.Rectangle neighbor_before = App.Find("Bubble." + neighbor_id).BoundingRectangle;

        AutomationElement react                 = HoverStripButton(newest.MessageID, "HoverReact.");
        System.Drawing.Rectangle bubble_after   = App.Find("Bubble." + newest.MessageID).BoundingRectangle;
        System.Drawing.Rectangle neighbor_after = App.Find("Bubble." + neighbor_id).BoundingRectangle;

        AssertSame(bubble_before, bubble_after, "Ann's hovered bubble");
        AssertSame(neighbor_before, neighbor_after, "The bubble above it in its group");
        Assert.IsTrue(react.BoundingRectangle.Left >= bubble_after.Right,
            $"The strip ({react.BoundingRectangle}) isn't right of Ann's bubble ({bubble_after}).");

        static void AssertSame(System.Drawing.Rectangle before, System.Drawing.Rectangle after, string what) =>
            Assert.IsTrue(Math.Abs(before.Left - after.Left) <= 1 && Math.Abs(before.Top - after.Top) <= 1
                && Math.Abs(before.Width - after.Width) <= 1 && Math.Abs(before.Height - after.Height) <= 1,
                $"{what} moved on hover: {before} became {after}.");
    }

    /// <summary>
    /// Leaving the window straight from Ann's bubble hides the hover strip (the exit reports a point inside the bubble, so
    /// a bounds check would keep it): a click where the ⋮ was opens no menu.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task HoverBubble_LeaveWindow_HidesStrip()
    {
        await OpenChatAsync("Row.c-ann");
        string id                    = Find("c-ann", AnnLast).MessageID;
        System.Drawing.Rectangle win = App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).BoundingRectangle;
        AutomationElement more       = App.Find("HoverMore." + id);
        PointerInput.Hover(App.Find("Bubble." + id).BoundingRectangle);

        // Straight Out Through The Window's Left Edge, As Real Input
        PointerInput.MoveBy(win.Left - Mouse.Position.X - 80);
        if (Mouse.Position.X >= win.Left)
        {
            Assert.Inconclusive($"The pointer ({Mouse.Position}) couldn't leave the window ({win}) on the left.");
        }

        Thread.Sleep(500);
        more.Click();

        // A Menu Would Show By Now
        Thread.Sleep(500);
        Assert.IsNull(App.TryFind("Bubble.Copy"), "The strip stayed after the pointer left the window.");
    }

    /// <summary>
    /// A reply arriving from Ann shows the message it quotes above its text.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task IncomingReply_ShowsQuote()
    {
        await OpenChatAsync("Row.c-ann");

        Message reply = Server.Phone.PushIncoming("c-ann", "Sure", replyToId: Find("c-ann", AnnFirst).MessageID);

        App.WaitForText("Quote." + reply.MessageID, AnnFirst);
    }

    /// <summary>
    /// Ann typing shows the typing dots; her stop hides them.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Typing_ShowsDots_ThenHides()
    {
        await OpenChatAsync("Row.c-ann");

        Server.Phone.PushTyping("c-ann", AnnPhone, started: true);
        Assert.AreEqual(ChatCopy.Typing, AppDriver.SafeName(App.Find("Chat.Typing")));

        Server.Phone.PushTyping("c-ann", AnnPhone, started: false);
        WaitForGone("Chat.Typing");
    }

    /// <summary>
    /// Typing in Ann's RCS chat tells the phone; typing in Team's SMS group doesn't.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task TypingInDraft_SendsTypingToPhone_RcsOnly()
    {
        // Team First: Anything It Sent Would Be In Before Ann's
        await OpenChatAsync("Row.c-team");
        App.Find("Chat.Draft").Focus();
        Keyboard.Type("Hi all");

        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Ann Lee");
        App.Find("Chat.Draft").Focus();
        Keyboard.Type("Hi Ann");

        Wait.Until(() => Server.Phone.Typing.Count >= 1, AppDriver.DefaultTimeout, () => "The phone never heard Ann's chat typing.");
        Assert.IsTrue(Server.Phone.Typing.All(t => t.Data.ConversationID == "c-ann"),
            $"Typing went to [{string.Join(", ", Server.Phone.Typing.Select(t => t.Data.ConversationID))}], not only c-ann.");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Opens The Main Window And A Row's Chat, Waits For Its Bubbles And Brings The Window To The Front (A Real Right-Click
    // Lands On Whatever Window Is On Top), With The Cursor In The Message Box
    private async Task OpenChatAsync(string rowId)
    {
        await OpenMainAsync();
        App.Find(rowId).Patterns.SelectionItem.Pattern.Select();
        App.Find("Chat.Draft");
        Wait.Until(() => RowCount() > 0, AppDriver.DefaultTimeout, () => $"{rowId}'s chat showed no bubbles.");
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();

        // Opening The Chat Puts The Cursor In The Message Box; Waited For, So It Can't Take Focus From A Row Later
        WaitForFocus("Chat.Draft");
    }

    // Moves The Pointer Onto A Bubble, Then Onto One Of Its Hover Strip's Buttons (Hidden Until Then, So It Takes No
    // Clicks), And Waits For The Strip's Fade In (167 ms), Which Turns On Its Clicks
    private AutomationElement HoverStripButton(string messageId, string prefix)
    {
        AutomationElement button = App.Find(prefix + messageId);
        PointerInput.Hover(App.Find("Bubble." + messageId).BoundingRectangle);
        PointerInput.Hover(button.BoundingRectangle);
        Thread.Sleep(500);
        return button;
    }

    // The Newest Bubble's Row Takes Keyboard Focus
    private void FocusLastRow()
    {
        AutomationElement[] rows = Rows();
        rows[^1].Focus();
    }

    private AutomationElement[] Rows() =>
        App.Find("Chat.Messages").FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem));

    private int RowCount()
    {
        try
        {
            return Rows().Length;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            return 0;
        }
    }

    // A Bubble's Accessible Item Type, Once It Shows
    private string ItemType(string automationId)
    {
        string last = "";
        Wait.Until(() => (last = App.TryFind(automationId)?.Properties.ItemType.ValueOrDefault ?? "").Length > 0, AppDriver.DefaultTimeout,
            () => $"'{automationId}' had no item type.");
        return last;
    }

    private void WaitForFocus(string automationId)
    {
        string last = "";
        Wait.Until(() => (last = App.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault ?? "") == automationId, AppDriver.DefaultTimeout,
            () => $"Focus was on '{last}', not '{automationId}'.");
    }

    // The Conversation's Newest Message On The Fake Phone
    private Message Newest(string conversationId) => Server.Phone.Chats.Page(conversationId, 1, null).Page[0];

    // A Conversation's Message On The Fake Phone, By Its Text
    private Message Find(string conversationId, string text) =>
        Server.Phone.Chats.Page(conversationId, 50, null).Page.Single(m => m.MessageInfo.Count > 0 && m.MessageInfo[0].MessageContent?.Content == text);
}
