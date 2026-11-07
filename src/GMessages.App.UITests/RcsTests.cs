using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.App.UITests.FakeGoogle;
using GMessages.Presentation;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace GMessages.App.UITests;

/// <summary>
/// RCS in the chat pane: the status line, RCS and SMS bubbles, the lock, reactions, replies, the hover strip and
/// typing.
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
    /// Delete in the message menu asks first, and Cancel keeps the message. Delete removes the message from the phone
    /// and the bubble goes.
    /// </summary>
    [TestMethod]
    public async Task DeleteMessage_AsksThenDeletes()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Newest("c-ann").MessageID;

        App.Find("Bubble." + id).RightClick();
        App.Find("Bubble.Delete").Patterns.Invoke.Pattern.Invoke();
        DialogButton(App.Find("Dialog.DeleteMessage"), ChatCopy.Cancel).Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Dialog.DeleteMessage");
        Thread.Sleep(500);
        Assert.IsEmpty(Server.Phone.Deletes, "Cancel still asked the phone to delete.");
        App.Find("Bubble." + id);

        App.Find("Bubble." + id).RightClick();
        App.Find("Bubble.Delete").Patterns.Invoke.Pattern.Invoke();
        DialogButton(App.Find("Dialog.DeleteMessage"), ChatCopy.Delete).Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => Server.Phone.Deletes.Contains(id), AppDriver.DefaultTimeout, () => "The phone was never asked to delete the message.");
        WaitForGone("Bubble." + id);
    }

    private static AutomationElement DialogButton(AutomationElement dialog, string name)
    {
        AutomationElement? button = null;
        Wait.Until(() => (button = AppDriver.Search(dialog, e => e.ControlType == FlaUI.Core.Definitions.ControlType.Button && AppDriver.SafeName(e) == name)) is not null,
            AppDriver.DefaultTimeout, () => $"The dialog has no '{name}' button.");
        return button!;
    }

    /// <summary>
    /// A sent message reads "Sent" when the phone echoes it. It then reads "Delivered" and "Read" as the phone
    /// reports them.
    /// </summary>
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
    /// A bubble tells screen readers what it is. The SMS and MMS bubbles of Team are "Text message". The RCS bubbles
    /// of Ann are "Chat message".
    /// </summary>
    [TestMethod]
    public async Task Sms_And_Rcs_HaveTheirItemTypes()
    {
        await OpenChatAsync("Row.c-team");
        Assert.AreEqual(ChatCopy.TextMessage, ItemType("Bubble." + Newest("c-team").MessageID));

        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        Assert.AreEqual(ChatCopy.ChatMessage, ItemType("Bubble." + Find("c-ann", AnnLast).MessageID));
    }

    /// <summary>
    /// My bubble sits on the right and the bubble of Ann sits on the left. Every bubble showed on the left while the
    /// visual states of the side sat below the root element of the bubble, where GoToState never finds them.
    /// </summary>
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
    /// A link in the text of a message shows as a link named by its text. A click opens the link. A test run only logs
    /// the open, because it has no browser. The click does not also click the bubble, so the caption row of the newest
    /// bubble stays.
    /// </summary>
    [TestMethod]
    public async Task LinkInText_Click_OpensWithoutTogglingTheCaption()
    {
        await OpenChatAsync("Row.c-ann");

        // The link uses plain http to a name that never resolves, so the app fetches no preview.
        Message message = Server.Phone.PushIncoming("c-ann", "read http://example.invalid/page today");
        AutomationElement link = App.Find($"Link.{message.MessageID}.0");
        Assert.AreEqual("http://example.invalid/page", AppDriver.SafeName(link));
        App.Find("Caption." + message.MessageID);

        link.Click();
        Wait.Until(() => ReadLog().Contains("Link opened (http, launched: False)", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => "The link click was never logged.");

        // A bubble click would hide the caption one turn later. The sleep gives it time to show that it did not.
        Thread.Sleep(500);
        Assert.IsNotNull(App.TryFind("Caption." + message.MessageID), "The link click also clicked the bubble (its caption row hid).");
    }

    /// <summary>
    /// A link that wraps counts only its letters. A click in the empty space at the end of its first line opens
    /// nothing. A click on its first letter opens the link.
    /// </summary>
    [TestMethod]
    public async Task WrappedLink_EmptyLineEnd_IsNotTheLink()
    {
        await OpenChatAsync("Row.c-ann");

        // The hyphens let the address wrap partway, which leaves space at the end of the first line.
        Message message = Server.Phone.PushIncoming("c-ann", "read http://example.invalid/" + string.Join("-", Enumerable.Repeat("word", 30)) + " today");
        AutomationElement link   = App.Find($"Link.{message.MessageID}.0");
        AutomationElement body   = App.Find("Bubble." + message.MessageID);
        System.Drawing.Rectangle text  = body.BoundingRectangle;
        System.Drawing.Rectangle where = link.BoundingRectangle;
        Assert.IsTrue(where.Height > 30, $"The link ({where}) didn't wrap.");

        // The click lands on the empty end of the first line of the link, just inside the right edge of the text.
        FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(text.Right - 2, where.Top + 6));
        Thread.Sleep(600);
        Assert.IsFalse(ReadLog().Contains("Link opened", StringComparison.Ordinal), "A click past the link's letters opened it.");

        link.Click();
        Wait.Until(() => ReadLog().Contains("Link opened (http, launched: False)", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => "A click on the link never opened it.");
    }

    /// <summary>
    /// A link in an inbox chat gets a preview card named by the <c>og:title</c> of the linked page and the host of the
    /// link. When the card shows, it takes the place of the link, and the text reads without the address.
    /// </summary>
    [TestMethod]
    public async Task LinkMessage_ShowsPreviewCard_TextDropsTheLink()
    {
        await OpenChatAsync("Row.c-ann");

        Message link = Server.Phone.PushIncoming("c-ann", $"look {PreviewLink()}");
        App.WaitForText("Preview." + link.MessageID, FakeGoogleServer.PreviewPageTitle + ", 127.0.0.1");
        App.WaitForText("Bubble." + link.MessageID, "look");
        Assert.IsNull(App.TryFind($"Link.{link.MessageID}.0"), "The previewed link still shows in the text.");
    }

    /// <summary>
    /// The pointer over a link preview card shows the hand cursor, as it does over a link.
    /// </summary>
    [TestMethod]
    public async Task PreviewCard_Hover_ShowsHandCursor()
    {
        await OpenChatAsync("Row.c-ann");

        Message link = Server.Phone.PushIncoming("c-ann", $"look {PreviewLink()}");
        App.WaitForText("Preview." + link.MessageID, FakeGoogleServer.PreviewPageTitle + ", 127.0.0.1");
        Thread.Sleep(300);
        PointerInput.Hover(App.Find("Preview." + link.MessageID).BoundingRectangle);
        Wait.Until(PointerInput.IsHandCursor, AppDriver.DefaultTimeout, () => "The pointer over the preview card isn't the hand cursor.");
    }

    /// <summary>
    /// A message that is only a link shows its preview card alone, with no empty text line above the card.
    /// </summary>
    [TestMethod]
    public async Task LinkOnlyMessage_ShowsCardAlone()
    {
        await OpenChatAsync("Row.c-ann");

        Message link = Server.Phone.PushIncoming("c-ann", PreviewLink());
        App.WaitForText("Preview." + link.MessageID, FakeGoogleServer.PreviewPageTitle + ", 127.0.0.1");
        WaitForGone("Bubble." + link.MessageID);
    }

    /// <summary>
    /// A right-click on a preview card opens the menu of the card, and the bubble menu does not open. Copy link puts
    /// the whole address on the clipboard. The test pastes it into the message box to check.
    /// </summary>
    [TestMethod]
    public async Task PreviewCard_RightClick_CopyLink_CopiesTheAddress()
    {
        await OpenChatAsync("Row.c-ann");
        string url = PreviewLink();
        Message link = Server.Phone.PushIncoming("c-ann", $"look {url}");
        App.WaitForText("Preview." + link.MessageID, FakeGoogleServer.PreviewPageTitle + ", 127.0.0.1");

        App.Find("Preview." + link.MessageID).RightClick();
        AutomationElement copyLink = App.Find("Preview.CopyLink");
        Assert.AreEqual(ChatCopy.CopyLink, AppDriver.SafeName(copyLink));
        Assert.IsNull(App.TryFind("Bubble.Copy"), "The bubble menu opened over the card's menu.");
        copyLink.Patterns.Invoke.Pattern.Invoke();

        App.Find("Chat.Draft").Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        string last = "";
        Wait.Until(() => (last = App.TryFind("Chat.Draft")?.Patterns.Value.Pattern.Value.ValueOrDefault ?? "") == url, AppDriver.DefaultTimeout,
            () => $"The message box read '{last}' after pasting, not the link.");
    }

    /// <summary>
    /// A link whose preview fails, because the page is not there, gets no card and keeps its link in the text.
    /// </summary>
    [TestMethod]
    public async Task LinkPreviewFails_KeepsTheLink()
    {
        await OpenChatAsync("Row.c-ann");

        string url   = $"http://127.0.0.1:{Server.BaseUrl.Port}/no-such-page";
        Message link = Server.Phone.PushIncoming("c-ann", $"look {url}");
        Wait.Until(() => ReadLog().Contains("No link preview", StringComparison.Ordinal) || ReadLog().Contains("Link preview failed", StringComparison.Ordinal),
            AppDriver.DefaultTimeout, () => "The preview never finished.");

        Assert.AreEqual(url, AppDriver.SafeName(App.Find($"Link.{link.MessageID}.0")));
        Assert.IsNull(App.TryFind("Preview." + link.MessageID), "A failed preview showed a card.");
    }

    /// <summary>
    /// The encrypted chat of Ann shows the lock under its newest message, not in the header. The SMS group Team shows
    /// no lock.
    /// </summary>
    [TestMethod]
    public async Task EncryptedChat_ShowsLock()
    {
        await OpenChatAsync("Row.c-ann");
        Wait.Until(() => App.TryFindWhere(e => AppDriver.SafeName(e) == ChatCopy.EndToEndEncrypted) is not null, AppDriver.DefaultTimeout,
            () => "Ann's encrypted chat showed no lock.");
        Assert.IsNull(App.TryFind("Chat.Encrypted"), "The header still shows a lock.");

        App.Find("Row.c-team").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Team");
        Wait.Until(() => App.TryFindWhere(e => AppDriver.SafeName(e) == ChatCopy.EndToEndEncrypted) is null, AppDriver.DefaultTimeout,
            () => "Team's SMS group showed a lock.");
    }

    /// <summary>
    /// React in the bubble menu opens the picker. The 👍 button sends the phone an Add of 👍, and the pill under the
    /// bubble shows it.
    /// </summary>
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
    /// A right-click on the selectable text of a message opens the bubble menu with React and Reply, at the pointer.
    /// The own Copy and Select all menu of the text does not open.
    /// </summary>
    [TestMethod]
    public async Task RightClickOnText_OpensBubbleMenu()
    {
        await OpenChatAsync("Row.c-ann");
        AutomationElement text = App.Find("Bubble." + Find("c-ann", AnnLast).MessageID);

        text.RightClick();
        AutomationElement react = App.Find("Bubble.React");
        App.Find("Bubble.Reply");
        Assert.IsNull(App.TryFindWhere(e => string.Equals(AppDriver.SafeName(e), "Select all", StringComparison.OrdinalIgnoreCase)),
            "The text's own menu opened.");

        // The menu opens at the pointer, in the middle of the text, not over the edge of the back.
        System.Drawing.Rectangle box  = text.BoundingRectangle;
        System.Drawing.Rectangle item = react.BoundingRectangle;
        Assert.IsTrue(Math.Abs(item.Left - (box.Left + (box.Width / 2))) < 40, $"The menu ({item}) didn't open at the pointer on the text ({box}).");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// A short message, received or sent, shows its time wider than the bubble. The reaction pill still overlaps the
    /// bottom edge of the bubble on its bottom-right corner, not beside the time.
    /// </summary>
    [TestMethod]
    public async Task ReactionPill_ShortMessage_OverlapsTheBubble()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Server.Phone.PushIncoming("c-ann", "Hi").MessageID;
        App.Find("Bubble." + id);
        App.Find("Bubble." + Server.Phone.PushIncoming("c-ann", "A longer message after it").MessageID);

        AutomationElement bubble = App.Find("Bubble." + id);
        bubble.RightClick();
        App.Find("Bubble.React").Patterns.Invoke.Pattern.Invoke();
        App.Find("ReactionPicker.1").Patterns.Invoke.Pattern.Invoke();
        AutomationElement pill = App.Find("Reactions." + id);
        WaitForGone("ReactionPicker.1");
        Thread.Sleep(500);
        bubble.Click();
        App.Find("Bubble." + id + ".Time");
        Thread.Sleep(500);

        AssertPillOnRightCorner(bubble, pill);

        // A message follows the sent message, so its time is not the status row.
        App.Find("Chat.Draft").Focus();
        Keyboard.Type("Hi");
        Keyboard.Type(VirtualKeyShort.RETURN);
        Wait.Until(() => Server.Phone.Sent.Count == 1, AppDriver.DefaultTimeout, () => "The phone never got the message.");
        string sentId = Newest("c-ann").MessageID;
        App.Find("Bubble." + Server.Phone.PushIncoming("c-ann", "Another longer message after it").MessageID);
        AutomationElement sent = App.Find("Bubble." + sentId);
        sent.RightClick();
        App.Find("Bubble.React").Patterns.Invoke.Pattern.Invoke();
        App.Find("ReactionPicker.1").Patterns.Invoke.Pattern.Invoke();
        AutomationElement sentPill = App.Find("Reactions." + sentId);
        WaitForGone("ReactionPicker.1");
        Thread.Sleep(500);
        sent.Click();
        App.Find("Bubble." + sentId + ".Time");
        Thread.Sleep(500);
        AssertPillOnLeftCorner(sent, sentPill);
    }

    // For a received message, the pill crosses the bottom edge of the bubble at its bottom-right corner. The text sits
    // 12 px inside the back.
    private static void AssertPillOnRightCorner(AutomationElement bubble, AutomationElement pill)
    {
        System.Drawing.Rectangle text = bubble.BoundingRectangle;
        System.Drawing.Rectangle on   = pill.BoundingRectangle;
        Assert.IsTrue(on.Top < text.Bottom + 12 && on.Bottom > text.Bottom + 12, $"The pill {on} doesn't cross the bubble's bottom edge (text {text}).");
        int middle = on.Left + (on.Width / 2);
        Assert.IsTrue(middle >= text.Right - 2 && on.Left < text.Right + 12, $"The pill {on} isn't on the bubble's bottom-right corner (text {text}).");
    }

    // For a sent message, the pill crosses the bottom edge of the bubble at its bottom-left corner. This mirrors the
    // received pill.
    private static void AssertPillOnLeftCorner(AutomationElement bubble, AutomationElement pill)
    {
        System.Drawing.Rectangle text = bubble.BoundingRectangle;
        System.Drawing.Rectangle on   = pill.BoundingRectangle;
        Assert.IsTrue(on.Top < text.Bottom + 12 && on.Bottom > text.Bottom + 12, $"The pill {on} doesn't cross the bubble's bottom edge (text {text}).");
        int middle = on.Left + (on.Width / 2);
        Assert.IsTrue(middle <= text.Left + 2 && on.Right > text.Left - 12, $"The pill {on} isn't on the bubble's bottom-left corner (text {text}).");
    }

    /// <summary>
    /// On a sent message wider than twice the pill, the left edge of the pill lines up with the left edge of the text,
    /// 12 px in from the left of the back. This mirrors the pill of a received bubble, as the owner asked.
    /// </summary>
    [TestMethod]
    public async Task ReactionPill_LongSentMessage_SitsBottomLeft()
    {
        await OpenChatAsync("Row.c-ann");
        string id = SendAndWait("c-ann", "This one is long enough for the long rule");

        AutomationElement pill = ReactAndWait("c-ann", id, "p-ann");
        System.Drawing.Rectangle text = App.Find("Bubble." + id).BoundingRectangle;
        System.Drawing.Rectangle on   = pill.BoundingRectangle;

        Assert.IsTrue(Math.Abs(on.Left - text.Left) <= 2, $"The pill {on} doesn't start at the text's left edge (text {text}).");
    }

    /// <summary>
    /// The owner sent a screenshot of the newest message "Oh yeahhhh". The pill covers neither the time nor the
    /// status. The time ends at the right side of the bubble and drops below the pill. The test uses Team, because the
    /// chat of the owner had no lock after the status. Team is SMS, so the status is the word "SMS".
    /// </summary>
    [TestMethod]
    public async Task ReactionPill_SentMessage_DoesNotCoverTime()
    {
        await OpenChatAsync("Row.c-team");
        string id = SendAndWait("c-team", "Oh yeahhhh");

        System.Drawing.Rectangle on      = ReactAndWait("c-team", id, "p-bo").BoundingRectangle;
        System.Drawing.Rectangle text    = App.Find("Bubble." + id).BoundingRectangle;
        System.Drawing.Rectangle caption = App.Find("Caption." + id).BoundingRectangle;
        System.Drawing.Rectangle status  = App.Find("Sms." + id).BoundingRectangle;

        Assert.IsFalse(on.IntersectsWith(caption), $"The pill {on} covers the time {caption}.");
        Assert.IsFalse(on.IntersectsWith(status), $"The pill {on} covers the status {status}.");
        Assert.IsTrue(Math.Abs(status.Right - text.Right) <= 2, $"The status {status} doesn't end at the bubble's right side (text {text}).");
        Assert.IsTrue(caption.Top >= on.Bottom - 1, $"The time {caption} didn't drop below the pill {on}.");
    }

    /// <summary>
    /// On a wide sent message, the pill and the time both fit under the text, so a reaction does not push the time
    /// down. The gap from the text to the time is the same, within 2 px, before and after the reaction.
    /// </summary>
    [TestMethod]
    public async Task ReactionPill_WideSentMessage_TimeStaysClose()
    {
        await OpenChatAsync("Row.c-ann");
        string id  = SendAndWait("c-ann", "This message is wide enough that the pill and the time both fit under it");
        int before = App.Find("Caption." + id).BoundingRectangle.Top - App.Find("Bubble." + id).BoundingRectangle.Bottom;

        ReactAndWait("c-ann", id, "p-ann");
        int after = App.Find("Caption." + id).BoundingRectangle.Top - App.Find("Bubble." + id).BoundingRectangle.Bottom;

        Assert.IsTrue(Math.Abs(after - before) <= 2, $"The time sat {before} px under the text without a reaction and {after} px with one.");
    }

    // Sends a message from the draft, waits until the phone echoes it as sent, and returns its ID.
    private string SendAndWait(string conversationId, string text)
    {
        int sent = Server.Phone.Sent.Count;
        App.Find("Chat.Draft").Focus();
        Keyboard.Type(text);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Wait.Until(() => Server.Phone.Sent.Count == sent + 1, AppDriver.DefaultTimeout, () => "The phone never got the message.");
        string id = Newest(conversationId).MessageID;
        App.WaitForText("Status." + id, ChatCopy.Sent);
        return id;
    }

    // A participant reacts with 👍 to a message. The method waits for the pill to show and settle. It returns the
    // pill.
    private AutomationElement ReactAndWait(string conversationId, string id, string participantId)
    {
        Server.Phone.PushReactions(conversationId, id, (EmojiType.Like, participantId));
        Wait.Until(() => App.TryFind("Reactions." + id)?.BoundingRectangle.Height > 0, AppDriver.DefaultTimeout, () => "The pill never showed.");
        Thread.Sleep(300);
        return App.Find("Reactions." + id);
    }

    /// <summary>
    /// A reaction added to a shown bubble pops its pill in. The test needs animation effects on, as on the test
    /// machine.
    /// </summary>
    [TestMethod]
    public async Task ReactionPill_PopsIn()
    {
        await OpenChatAsync("Row.c-ann");
        string id  = Find("c-ann", AnnLast).MessageID;
        string pop = Path.Combine(App.DataDir, "reaction-pop.txt");

        App.Find("Bubble." + id).RightClick();
        App.Find("Bubble.React").Patterns.Invoke.Pattern.Invoke();
        App.Find("ReactionPicker.1").Patterns.Invoke.Pattern.Invoke();

        string last = string.Empty;
        Wait.Until(() =>
        {
            try
            {
                last = File.Exists(pop) ? File.ReadAllText(pop) : string.Empty;
            }
            catch (IOException)
            {
                // The app is writing the file. Read it again.
            }

            return last == id;
        }, AppDriver.DefaultTimeout, () => $"The pill never popped in (last pop '{last}').");
        App.Find("Reactions." + id);
    }

    /// <summary>
    /// More reactions in the picker opens the emoji picker of the app. Focus is in its search box, and its emoji fill
    /// its width. The text "eyes" shows 👀. The text "fire" leaves 🔥. A click on 🔥 closes the picker and sends the
    /// phone an Add of 🔥 as a custom reaction, and the pill shows it.
    /// </summary>
    [TestMethod]
    public async Task MoreReactions_Picker_SearchAndPick_SendsCustomEmoji()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Find("c-ann", AnnLast).MessageID;

        App.Find("Bubble." + id).RightClick();
        App.Find("Bubble.React").Patterns.Invoke.Pattern.Invoke();
        AutomationElement more = App.Find("ReactionPicker.More");
        Assert.AreEqual(ChatCopy.MoreReactions, more.Name);
        more.Patterns.Invoke.Pattern.Invoke();

        AutomationElement search = App.Find("EmojiPicker.Search");
        Wait.Until(() => search.Properties.HasKeyboardFocus.ValueOrDefault, AppDriver.DefaultTimeout, () => "The search box didn't take focus.");

        // The first row fills the picker edge to edge, lined up with the search box: 😀 is first and 😂 is eighth.
        System.Drawing.Rectangle box    = search.BoundingRectangle;
        System.Drawing.Rectangle first  = App.Find("EmojiPicker.1F600").BoundingRectangle;
        System.Drawing.Rectangle eighth = App.Find("EmojiPicker.1F602").BoundingRectangle;
        Assert.IsTrue(Math.Abs(first.Left - box.Left) <= 2 && Math.Abs(eighth.Right - box.Right) <= 2,
            $"The emoji ({first.Left} to {eighth.Right}) don't fill the search box's width ({box.Left} to {box.Right}).");

        // The test types as the owner did. The text "eyes" shows 👀, and also 😍, because its words have "eyes".
        Keyboard.Type("eyes");
        Wait.Until(() => App.TryFind("EmojiPicker.1F440") is not null, AppDriver.DefaultTimeout, () => "Typing 'eyes' doesn't show 👀.");
        Assert.IsNotNull(App.TryFind("EmojiPicker.1F60D"), "Typing 'eyes' doesn't show 😍.");

        search.Patterns.Value.Pattern.SetValue("fire");
        Wait.Until(() => App.TryFind("EmojiPicker.1F600") is null, AppDriver.DefaultTimeout, () => "Searching 'fire' still shows 😀.");
        App.Find("EmojiPicker.1F525").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => App.TryFind("EmojiPicker") is null, AppDriver.DefaultTimeout, () => "Picking an emoji left the picker open.");

        Wait.Until(() => Server.Phone.Reactions.Count == 1, AppDriver.DefaultTimeout, () => "The phone never got the reaction.");
        SendReactionRequest reaction = Server.Phone.Reactions[0];
        Assert.AreEqual(SendReactionRequest.Types.Action.Add, reaction.Action);
        Assert.AreEqual(EmojiType.Custom, reaction.ReactionData.Type);
        Assert.AreEqual("\U0001F525", reaction.ReactionData.Unicode);
        Assert.AreEqual(id, reaction.MessageID);

        string last = "";
        Wait.Until(() => (last = AppDriver.SafeName(App.TryFind("Reactions." + id))).Contains("\U0001F525", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => $"The reaction pill read '{last}', without 🔥.");
    }

    /// <summary>
    /// Escape closes the emoji picker without a reaction.
    /// </summary>
    [TestMethod]
    public async Task MoreReactions_Picker_Escape_ClosesWithoutReacting()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Find("c-ann", AnnLast).MessageID;

        App.Find("Bubble." + id).RightClick();
        App.Find("Bubble.React").Patterns.Invoke.Pattern.Invoke();
        App.Find("ReactionPicker.More").Patterns.Invoke.Pattern.Invoke();
        App.Find("EmojiPicker.Search");
        Keyboard.Type(VirtualKeyShort.ESCAPE);

        Wait.Until(() => App.TryFind("EmojiPicker") is null, AppDriver.DefaultTimeout, () => "Escape left the picker open.");
        Assert.IsEmpty(Server.Phone.Reactions, "Escape sent a reaction.");
    }

    /// <summary>
    /// The first open of the emoji picker does not freeze the app. It drew every emoji at once before. The test opens
    /// the picker twice. The timings in the log, in milliseconds only, show no frame gap over 300 ms in either open.
    /// The picker makes only the rows in view.
    /// </summary>
    [TestMethod]
    public async Task MoreReactions_Picker_FirstOpen_IsQuick()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Find("c-ann", AnnLast).MessageID;

        for (int open = 1; open <= 2; open++)
        {
            App.Find("Bubble." + id).RightClick();
            App.Find("Bubble.React").Patterns.Invoke.Pattern.Invoke();
            App.Find("ReactionPicker.More").Patterns.Invoke.Pattern.Invoke();

            // The test runs no UI Automation query while the picker opens. Each Find walks the tree of the app on its
            // UI thread, and the timing would then include that walk.
            string line = $"Emoji picker open {open}:";
            Wait.Until(() => ReadLog().Contains(line, StringComparison.Ordinal), AppDriver.DefaultTimeout, () => $"No '{line}' timing in the log.");
            App.Find("EmojiPicker.Search");
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Wait.Until(() => App.TryFind("EmojiPicker") is null, AppDriver.DefaultTimeout, () => "Escape left the picker open.");
        }

        // A log line reads: "Emoji picker open N: built in B ms, longest stall S ms, R rows made"
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(ReadLog(), @"Emoji picker open (\d+): built in \d+ ms, longest stall (\d+) ms, (\d+) rows made"))
        {
            Console.WriteLine(m.Value);
            Assert.IsLessThan(300, int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), $"Open {m.Groups[1].Value} stood the app still: {m.Value}");
            Assert.IsLessThan(40, int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), $"Open {m.Groups[1].Value} made too many rows: {m.Value}");
        }
    }

    /// <summary>
    /// In the group Team, a click on the reaction pill of a message lists who reacted, in the order of the pill. 👍
    /// comes from Bo and from me, shown as "You". Then 😂 comes from Cy.
    /// </summary>
    [TestMethod]
    public async Task GroupPill_Click_ListsWhoReacted()
    {
        await OpenChatAsync("Row.c-team");
        string id = Find("c-team", "Lunch at noon?").MessageID;

        Server.Phone.PushReactions("c-team", id, (EmojiType.Like, "p-bo"), (EmojiType.Laugh, "p-cy"), (EmojiType.Like, FakeChats.MyParticipantId));
        string last = "";
        Wait.Until(() => (last = AppDriver.SafeName(App.TryFind("Reactions." + id))).EndsWith(" 3", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => $"The reaction pill read '{last}', not three reactions.");

        App.Find("Reactions." + id).Click();
        App.Find("Reactors");
        string[] rows = [.. Enumerable.Range(0, 3).Select(i => AppDriver.SafeName(App.Find("Reactors." + i)))];
        CollectionAssert.AreEqual(new[] { "\U0001F44D Bo", "\U0001F44D " + ChatCopy.You, "\U0001F602 Cy" }, rows);
        Assert.IsNull(App.TryFind("ReactionPicker"), "The pill opened the reaction picker in a group.");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// The picker for a message that I reacted to marks my reaction as selected, and Narrator reads "Selected". The
    /// other reactions are not selected.
    /// </summary>
    [TestMethod]
    public async Task Picker_MarksMyReactionSelected()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Find("c-ann", AnnLast).MessageID;

        Server.Phone.PushReactions("c-ann", id, (EmojiType.Laugh, FakeChats.MyParticipantId));
        string last = "";
        Wait.Until(() => (last = AppDriver.SafeName(App.TryFind("Reactions." + id))).Contains("\U0001F602", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => $"The reaction pill read '{last}', without 😂.");
        App.Find("Bubble." + id).RightClick();
        App.Find("Bubble.React").Patterns.Invoke.Pattern.Invoke();

        App.Find("ReactionPicker");
        Assert.AreEqual(ChatCopy.Selected, App.Find("ReactionPicker." + (int)EmojiType.Laugh).Properties.ItemStatus.ValueOrDefault);
        Assert.AreEqual("", App.Find("ReactionPicker.1").Properties.ItemStatus.ValueOrDefault ?? "");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// In the one-on-one chat with Ann, the reaction pill lists who reacted, as in a group. It never opens the
    /// reaction picker. The owner asked for this in Plan 7.
    /// </summary>
    [TestMethod]
    public async Task OneOnOnePill_Click_ListsWhoReacted()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Find("c-ann", AnnLast).MessageID;

        Server.Phone.PushReactions("c-ann", id, (EmojiType.Like, "p-ann"));
        App.Find("Reactions." + id).Click();

        App.Find("Reactors");
        Assert.AreEqual("\U0001F44D Ann Lee", AppDriver.SafeName(App.Find("Reactors.0")));
        Assert.IsNull(App.TryFind("ReactionPicker"), "The pill opened the reaction picker in a one-on-one chat.");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// My reaction is on a message. The color changes steadily when the pointer hovers its pill, and when the pointer
    /// hovers my reaction in the picker. Every frame of the hover sits between the color before and the settled hover
    /// color, so there is no flash. The hover keeps the accent of my reaction and only tints it. The fill under the
    /// accent sat on the button itself. Its hover swapped the accent for the hover fill through the brush transition.
    /// This caused the flicker that the owner reported.
    /// </summary>
    [TestMethod]
    public async Task MyReaction_Hover_StaysSteady()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Find("c-ann", AnnLast).MessageID;

        Server.Phone.PushReactions("c-ann", id, (EmojiType.Laugh, FakeChats.MyParticipantId));
        Wait.Until(() => App.TryFind("Reactions." + id)?.BoundingRectangle.Height > 0, AppDriver.DefaultTimeout, () => "The pill never showed.");
        AssertSteadyHover("pill", App.Find("Reactions." + id).BoundingRectangle);

        App.Find("Bubble." + id).RightClick();
        App.Find("Bubble.React").Patterns.Invoke.Pattern.Invoke();
        AssertSteadyHover("picker", App.Find("ReactionPicker." + (int)EmojiType.Laugh).BoundingRectangle);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// A reaction pill does not push the time of the newest bubble down. The gap from the text of the bubble to the
    /// time under it is the same, within 2 px, before and after Ann reacts.
    /// </summary>
    [TestMethod]
    public async Task ReactionPill_KeepsTheTimeUnderTheBubble()
    {
        await OpenChatAsync("Row.c-ann");
        string id  = Find("c-ann", AnnLast).MessageID;
        int before = App.Find("Caption." + id).BoundingRectangle.Top - App.Find("Bubble." + id).BoundingRectangle.Bottom;

        Server.Phone.PushReactions("c-ann", id, (EmojiType.Like, "p-ann"));
        Wait.Until(() => App.TryFind("Reactions." + id)?.BoundingRectangle.Height > 0, AppDriver.DefaultTimeout, () => "The pill never showed.");
        Thread.Sleep(300);
        int after = App.Find("Caption." + id).BoundingRectangle.Top - App.Find("Bubble." + id).BoundingRectangle.Bottom;

        Assert.IsTrue(Math.Abs(after - before) <= 2, $"The time sat {before} px under the text without a reaction and {after} px with one.");
    }

    /// <summary>
    /// A short bubble fits its text, sent or received, and also a bubble that ends in an emoji. The left and right
    /// insets of the text inside the bubble are equal, within 2 px. The time and status row under the newest bubble
    /// are wider.
    /// </summary>
    [TestMethod]
    public async Task ShortBubbles_FitTheirText()
    {
        await OpenChatAsync("Row.c-ann");

        Message ok = Server.Phone.PushIncoming("c-ann", "ok");
        App.Find("Caption." + ok.MessageID);
        AssertTextFits(ok.MessageID);

        AutomationElement draft = App.Find("Chat.Draft");
        draft.Patterns.Value.Pattern.SetValue("okay \U0001F497");
        draft.Focus();
        Keyboard.Type(VirtualKeyShort.RETURN);
        Wait.Until(() => Server.Phone.Sent.Count == 1, AppDriver.DefaultTimeout, () => "The phone never got the message.");
        string id = Newest("c-ann").MessageID;
        App.WaitForText("Status." + id, ChatCopy.Sent);
        AssertTextFits(id);
    }

    /// <summary>
    /// Shift + F10 on a focused bubble opens its menu. Copy puts the text of the bubble on the clipboard. The test
    /// pastes the text into the message box to check.
    /// </summary>
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
    [TestMethod]
    public async Task ShiftPlus_OnFocusedBubble_OpensPicker()
    {
        await OpenChatAsync("Row.c-ann");
        FocusLastRow();

        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.OEM_PLUS);

        App.Find("ReactionPicker");
        App.Find("ReactionPicker.7");

        // Love shows the red heart of Google, not heart eyes.
        Assert.AreEqual("❤️", App.Find("ReactionPicker.2").Name);
    }

    /// <summary>
    /// Reply in the bubble menu shows the reply chip and moves focus to the message box. The send carries the ID of
    /// the quoted message, and the sent bubble shows the quote.
    /// </summary>
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
    /// Ctrl + Enter on a focused bubble starts a reply to it and moves focus to the message box.
    /// </summary>
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
    /// Focus on the newest message, as the keyboard moves it, makes the bubble draw its own ring. The system focus
    /// visual of the row stays off. When focus leaves, the ring goes. A click on the bubble does not bring the ring
    /// back, as the owner asked.
    /// </summary>
    [TestMethod]
    public async Task KeyboardFocus_RingsTheBubble_NotTheRow()
    {
        await OpenChatAsync("Row.c-ann");
        AutomationElement text = App.Find("Bubble." + Find("c-ann", AnnLast).MessageID);

        FocusLastRow();
        WaitForRing(text, "FocusRing");

        App.Find("Chat.Draft").Focus();
        WaitForRing(text, "NoRing");

        text.Click();
        Thread.Sleep(300);
        Assert.AreNotEqual("FocusRing", text.Properties.ItemStatus.ValueOrDefault, "A click rang the bubble.");
    }

    /// <summary>
    /// The focus ring of the bubble is themed. The pixels that it draws have the accent color, never the white or
    /// black focus stroke of Windows.
    /// </summary>
    /// <remarks>
    /// Manual test. UI Automation focus on the row lands as FocusState.Pointer in most runs here, as the state of the
    /// app shows. By design, that state draws no ring. The test then times out even where the ring works. The same
    /// flake hits KeyboardFocus_RingsTheBubble_NotTheRow when it runs alone.
    /// </remarks>
    [TestMethod]
    [TestCategory("Manual")]
    public async Task KeyboardFocus_RingIsAccentColored()
    {
        await OpenChatAsync("Row.c-ann");
        // The bubble is ringed, then not ringed after focus goes back to the message box, as in
        // KeyboardFocus_RingsTheBubble_NotTheRow.
        AutomationElement text          = App.Find("Bubble." + Find("c-ann", AnnLast).MessageID);
        System.Drawing.Rectangle around = System.Drawing.Rectangle.Inflate(text.BoundingRectangle, 24, 16);
        FocusLastRow();
        WaitForRing(text, "FocusRing");
        Thread.Sleep(300);
        using FlaUI.Core.Capturing.CaptureImage after = FlaUI.Core.Capturing.Capture.Rectangle(around);

        App.Find("Chat.Draft").Focus();
        WaitForRing(text, "NoRing");
        Thread.Sleep(300);
        using FlaUI.Core.Capturing.CaptureImage before = FlaUI.Core.Capturing.Capture.Rectangle(around);

        // The ring pixels are the pixels that changed. Each one must have a color: not gray, white or black.
        int changed = 0;
        int gray    = 0;
        for (int y = 0; y < after.Bitmap.Height; y++)
        {
            for (int x = 0; x < after.Bitmap.Width; x++)
            {
                System.Drawing.Color was = before.Bitmap.GetPixel(x, y);
                System.Drawing.Color now = after.Bitmap.GetPixel(x, y);
                if (Math.Abs(was.R - now.R) + Math.Abs(was.G - now.G) + Math.Abs(was.B - now.B) < 60)
                {
                    continue;
                }

                changed++;
                int chroma = Math.Max(now.R, Math.Max(now.G, now.B)) - Math.Min(now.R, Math.Min(now.G, now.B));
                if (chroma < 30)
                {
                    gray++;
                }
            }
        }

        Assert.IsTrue(changed > 20, $"The ring drew only {changed} pixels.");
        Assert.IsTrue(gray * 4 < changed, $"{gray} of the ring's {changed} pixels are gray, white or black, not the accent.");
    }

    /// <summary>
    /// With the mouse over the last bubble of Ann, Reply in the hover strip starts a reply to it.
    /// </summary>
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
    /// With the mouse over the last bubble of Ann, React in the hover strip opens the reaction picker.
    /// </summary>
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
    /// With the mouse over the last bubble of Ann, the ⋮ button in the hover strip opens the bubble menu.
    /// </summary>
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
    /// The space of the hover strip is always kept. A hover on my bubble does not move it, and the strip is on its
    /// left. A hover on the newest bubble of Ann moves neither that bubble nor the bubble above it in its group, and
    /// the strip is on its right. React in the strip then opens the picker, so the hover took effect.
    /// </summary>
    [TestMethod]
    public async Task HoverStrip_DoesNotMoveBubbles()
    {
        await OpenChatAsync("Row.c-ann");

        // A second message from Ann, one second after her last message, makes one group with it.
        Message newest     = Server.Phone.PushIncoming("c-ann", "See you then");
        string neighborId  = Find("c-ann", AnnLast).MessageID;
        string mineId      = Find("c-ann", MyReply).MessageID;
        App.Find("Bubble." + newest.MessageID);

        // The list needs time to finish the scroll to the new bubble.
        Thread.Sleep(1000);

        System.Drawing.Rectangle mineBefore  = App.Find("Bubble." + mineId).BoundingRectangle;
        AutomationElement mineReact          = HoverStripButton(mineId, "HoverReact.");
        System.Drawing.Rectangle mineAfter   = App.Find("Bubble." + mineId).BoundingRectangle;
        AssertSame(mineBefore, mineAfter, "My hovered bubble");
        Assert.IsTrue(mineReact.BoundingRectangle.Right <= mineAfter.Left,
            $"The strip ({mineReact.BoundingRectangle}) isn't left of my bubble ({mineAfter}).");

        System.Drawing.Rectangle bubbleBefore    = App.Find("Bubble." + newest.MessageID).BoundingRectangle;
        System.Drawing.Rectangle neighborBefore  = App.Find("Bubble." + neighborId).BoundingRectangle;
        AutomationElement react                  = HoverStripButton(newest.MessageID, "HoverReact.");
        System.Drawing.Rectangle bubbleAfter     = App.Find("Bubble." + newest.MessageID).BoundingRectangle;
        System.Drawing.Rectangle neighborAfter   = App.Find("Bubble." + neighborId).BoundingRectangle;
        AssertSame(bubbleBefore, bubbleAfter, "Ann's hovered bubble");
        AssertSame(neighborBefore, neighborAfter, "The bubble above it in its group");
        Assert.IsTrue(react.BoundingRectangle.Left >= bubbleAfter.Right,
            $"The strip ({react.BoundingRectangle}) isn't right of Ann's bubble ({bubbleAfter}).");

        react.Click();
        App.Find("ReactionPicker");

        static void AssertSame(System.Drawing.Rectangle before, System.Drawing.Rectangle after, string what) =>
            Assert.IsTrue(Math.Abs(before.Left - after.Left) <= 1 && Math.Abs(before.Top - after.Top) <= 1
                && Math.Abs(before.Width - after.Width) <= 1 && Math.Abs(before.Height - after.Height) <= 1,
                $"{what} moved on hover: {before} became {after}.");
    }

    /// <summary>
    /// My failed bubble takes no strip. The test retries it with the pointer resting on it. The strip shows when the
    /// phone echoes the message, with no pointer move, and its ⋮ button opens the bubble menu.
    /// </summary>
    [TestMethod]
    public async Task HoverBubble_SentWhileHovered_ShowsStrip()
    {
        await OpenChatAsync("Row.c-ann");
        Server.Phone.FailSends = true;

        App.Find("Chat.Draft").Focus();
        Keyboard.Type("Running late");
        Keyboard.Type(VirtualKeyShort.RETURN);
        AutomationElement? retry = null;
        Wait.Until(() => (retry = App.TryFind("Chat.Messages") is { } list ? AppDriver.Search(list, e => AppDriver.SafeName(e) == ChatCopy.NotSentRetry) : null) is not null,
            AppDriver.DefaultTimeout, () => $"'{ChatCopy.NotSentRetry}' never showed.");

        // The pointer rests on the text of the failed bubble. The retry then goes through without a pointer move.
        PointerInput.Hover(App.Find("Bubble." + Server.Phone.Sent[0].TmpID).BoundingRectangle);
        Server.Phone.FailSends = false;
        retry!.Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => Server.Phone.Sent.Count == 2, AppDriver.DefaultTimeout, () => $"The phone got {Server.Phone.Sent.Count} sends, not 2.");
        string id = Newest("c-ann").MessageID;
        App.Find("Bubble." + id);

        // The strip fades in for 167 ms. The sleep waits for the fade to end.
        Thread.Sleep(500);
        App.Find("HoverMore." + id).Click();

        App.Find("Bubble.Copy");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// When the pointer leaves the window straight from the bubble of Ann, the hover strip hides. The exit reports a
    /// point inside the bubble, so a bounds check would keep the strip. A click where the ⋮ button was opens no menu.
    /// </summary>
    [TestMethod]
    public async Task HoverBubble_LeaveWindow_HidesStrip()
    {
        await OpenChatAsync("Row.c-ann");
        string id                    = Find("c-ann", AnnLast).MessageID;
        System.Drawing.Rectangle win = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).BoundingRectangle;
        PointerInput.Hover(App.Find("Bubble." + id).BoundingRectangle);
        AutomationElement more = App.Find("HoverMore." + id);

        PointerInput.LeaveLeft(win);

        Thread.Sleep(500);
        more.Click();

        // A menu would show by now.
        Thread.Sleep(500);
        Assert.IsNull(App.TryFind("Bubble.Copy"), "The strip stayed after the pointer left the window.");
    }

    /// <summary>
    /// A reply that arrives from Ann shows the message that it quotes above its text.
    /// </summary>
    [TestMethod]
    public async Task IncomingReply_ShowsQuote()
    {
        await OpenChatAsync("Row.c-ann");

        Message reply = Server.Phone.PushIncoming("c-ann", "Sure", replyToId: Find("c-ann", AnnFirst).MessageID);

        App.WaitForText("Quote." + reply.MessageID, AnnFirst);
    }

    /// <summary>
    /// A click on the quote of a reply scrolls smoothly to the quoted message, which newer messages scrolled out of
    /// view. The original does not pulse. Enter on the focused quote does the same.
    /// </summary>
    [TestMethod]
    public async Task ReplyQuote_Click_ScrollsToTheOriginal()
    {
        await OpenChatAsync("Row.c-ann");
        string original = Find("c-ann", AnnFirst).MessageID;

        // The newer messages scroll the first message of Ann out of view.
        for (int i = 0; i < 30; i++)
        {
            Server.Phone.PushIncoming("c-ann", "filler " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Message reply = Server.Phone.PushIncoming("c-ann", "Sure", replyToId: original);
        App.WaitForText("Quote." + reply.MessageID, AnnFirst);

        string pulse  = Path.Combine(App.DataDir, "reply-jump.txt");
        string scroll = Path.Combine(App.DataDir, "chat-scroll.txt");
        Thread.Sleep(2000);
        File.Delete(scroll);
        App.Find("QuoteJump." + reply.MessageID).Click();
        Wait.Until(() => File.Exists(pulse) && ReadPulse(pulse) == original, AppDriver.DefaultTimeout, () => "The quote's click never landed on the original.");
        Assert.IsFalse(File.Exists(Path.Combine(App.DataDir, "pulse.txt")), "The original pulsed.");
        System.Drawing.Rectangle list = App.Find("Chat.Messages").BoundingRectangle;
        System.Drawing.Rectangle shown = App.Find("Bubble." + original).BoundingRectangle;
        Assert.IsTrue(shown.Top >= list.Top && shown.Bottom <= list.Bottom, $"The original ({shown}) isn't in view ({list}).");
        Assert.AreEqual("Smooth", File.Exists(scroll) ? ReadPulse(scroll) : string.Empty, "The jump to the original wasn't a smooth scroll.");

        // Enter on the focused quote, from the newest message. The list does not keep the row of the reply realized
        // that far up.
        File.Delete(pulse);
        App.Find("Chat.ScrollToBottom").Patterns.Invoke.Pattern.Invoke();
        App.Find("QuoteJump." + reply.MessageID).Focus();
        Keyboard.Type(VirtualKeyShort.ENTER);
        Wait.Until(() => File.Exists(pulse) && ReadPulse(pulse) == original, AppDriver.DefaultTimeout, () => "Enter on the quote never landed on the original.");

        static string ReadPulse(string path)
        {
            try
            {
                using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                return reader.ReadToEnd().Trim();
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// When Ann types, the typing dots show. When she stops, the dots hide.
    /// </summary>
    [TestMethod]
    public async Task Typing_ShowsDots_ThenHides()
    {
        await OpenChatAsync("Row.c-ann");

        Server.Phone.PushTyping("c-ann", AnnPhone, started: true);
        Assert.AreEqual("Ann Lee is typing", AppDriver.SafeName(App.Find("Chat.Typing")));

        Server.Phone.PushTyping("c-ann", AnnPhone, started: false);
        WaitForGone("Chat.Typing");
    }

    /// <summary>
    /// Typing in the RCS chat with Ann tells the phone. Typing in the SMS group Team does not.
    /// </summary>
    [TestMethod]
    public async Task TypingInDraft_SendsTypingToPhone_RcsOnly()
    {
        // The test uses Team first. Anything that Team sent would arrive before the typing of Ann.
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

    /// <summary>
    /// A plain text bubble has no preview card. The card of a link message loads into its bubble when the preview
    /// arrives. The card is named by the title of the page and the host of the link, and the text drops the link.
    /// </summary>
    [TestMethod]
    public async Task Bubble_TextThenLinkPreview_ShowsCard()
    {
        await OpenChatAsync("Row.c-ann");

        Message text = Server.Phone.PushIncoming("c-ann", "No link here");
        App.WaitForText("Bubble." + text.MessageID, "No link here");
        Assert.IsNull(App.TryFind("Preview." + text.MessageID), "A plain text bubble showed a preview card.");

        Message link = Server.Phone.PushIncoming("c-ann", $"look {PreviewLink()}");
        App.Find("Bubble." + link.MessageID);
        App.WaitForText("Preview." + link.MessageID, FakeGoogleServer.PreviewPageTitle + ", 127.0.0.1");
        App.WaitForText("Bubble." + link.MessageID, "look");
        Assert.IsNull(App.TryFind("Preview." + text.MessageID), "The plain text bubble showed a preview card.");
    }

    /// <summary>
    /// A bubble with no reactions has no pill. The 👍 of Ann brings a pill in on the bottom edge of the bubble. On
    /// another bubble, my own 👍 brings a pill in, taking it back removes the pill, and a new reaction brings it back.
    /// </summary>
    [TestMethod]
    public async Task Bubble_ReceivesReaction_ShowsPill()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Find("c-ann", AnnLast).MessageID;
        Assert.IsNull(App.TryFind("Reactions." + id), "A bubble without reactions showed a pill.");

        AutomationElement pill        = ReactAndWait("c-ann", id, "p-ann");
        System.Drawing.Rectangle text = App.Find("Bubble." + id).BoundingRectangle;
        System.Drawing.Rectangle on   = pill.BoundingRectangle;
        StringAssert.Contains(AppDriver.SafeName(pill), "\U0001F44D");
        Assert.IsTrue(on.Top < text.Bottom + 12 && on.Bottom > text.Bottom + 12, $"The pill {on} doesn't cross the bubble's bottom edge (text {text}).");

        // On another bubble, my reaction goes in, comes back out with the same emoji, then goes in again.
        string mineId  = Find("c-ann", AnnFirst).MessageID;
        for (int round = 0; round < 3; round++)
        {
            // The last picker needs time to close and the pill needs time to settle.
            Thread.Sleep(500);
            App.Find("Bubble." + mineId).RightClick();
            App.Find("Bubble.React").Patterns.Invoke.Pattern.Invoke();
            App.Find("ReactionPicker.1").Patterns.Invoke.Pattern.Invoke();
            WaitForGone("ReactionPicker.1");
            Wait.Until(() => Server.Phone.Reactions.Count == round + 1, AppDriver.DefaultTimeout, () => $"The phone got {Server.Phone.Reactions.Count} reactions, not {round + 1}.");
            if (round == 1)
            {
                WaitForGone("Reactions." + mineId);
                continue;
            }

            Wait.Until(() => App.TryFind("Reactions." + mineId)?.BoundingRectangle.Height > 0, AppDriver.DefaultTimeout, () => $"My pill never showed (round {round}).");
        }

        Assert.AreEqual(SendReactionRequest.Types.Action.Remove, Server.Phone.Reactions[1].Action, "The second pick didn't take my reaction back.");
    }

    /// <summary>
    /// A send that the phone refuses in Team, which is SMS, shows the retry footer. After a retry, the footer goes.
    /// The parts of the sent SMS bubble take the on-accent brushes. The bubble shows no part that its message does
    /// not use.
    /// </summary>
    [TestMethod]
    public async Task Bubble_SendFails_ShowsRetryFooter()
    {
        await OpenChatAsync("Row.c-team");
        Server.Phone.FailSends = true;

        App.Find("Chat.Draft").Focus();
        Keyboard.Type("Running late");
        Keyboard.Type(VirtualKeyShort.RETURN);
        Wait.Until(() => Server.Phone.Sent.Count == 1, AppDriver.DefaultTimeout, () => "The phone never got the send.");
        AutomationElement retry = App.Find("Bubble." + Server.Phone.Sent[0].TmpID + ".Retry");
        Assert.AreEqual(ChatCopy.NotSentRetry, AppDriver.SafeName(retry));

        Server.Phone.FailSends = false;
        retry.Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => Server.Phone.Sent.Count == 2, AppDriver.DefaultTimeout, () => $"The phone got {Server.Phone.Sent.Count} sends, not 2.");
        string id = Newest("c-team").MessageID;
        App.WaitForText("Status." + id, ChatCopy.Sent);
        WaitForGone("Bubble." + Server.Phone.Sent[0].TmpID + ".Retry");
        Assert.IsNull(App.TryFind("Bubble." + id + ".Retry"), "The sent bubble kept the retry footer.");

        foreach (string part in new[] { "QuoteJump.", "Preview.", "Bubble.Photo.", "Bubble.Video.", "Bubble.File.", "Bubble.Voice.", "Upload.", "Reactions." })
        {
            Assert.IsNull(App.TryFind(part + id), $"The sent SMS bubble showed {part}{id}.");
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Waits for the focus ring state of a bubble. In a test run, the state is the item status of its text. See
    // MessageBubble.ShowFocusRing.
    private static void WaitForRing(AutomationElement text, string state)
    {
        string last = "";
        Wait.Until(() => (last = text.Properties.ItemStatus.ValueOrDefault ?? "") == state, AppDriver.DefaultTimeout,
            () => $"The bubble's ring read '{last}', not '{state}'.");
    }

    // Opens the main window and the chat of a row. The method waits for the bubbles and brings the window to the
    // front, because a real right-click lands on the window that is on top. The cursor ends in the message box.
    private async Task OpenChatAsync(string rowId)
    {
        await OpenMainAsync();
        App.Find(rowId).Patterns.SelectionItem.Pattern.Select();
        App.Find("Chat.Draft");
        Wait.Until(() => RowCount() > 0, AppDriver.DefaultTimeout, () => $"{rowId}'s chat showed no bubbles.");
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();

        // Opening the chat puts the cursor in the message box. The wait stops the cursor from taking focus from a row
        // later.
        WaitForFocus("Chat.Draft");
    }

    // Moves the pointer onto a bubble, because its first hover loads the strip. Then the pointer moves onto a button
    // of the hover strip, which takes no clicks while hidden. The method waits for the fade in of 167 ms, which turns
    // the clicks on.
    private AutomationElement HoverStripButton(string messageId, string prefix)
    {
        PointerInput.Hover(App.Find("Bubble." + messageId).BoundingRectangle);
        AutomationElement button = App.Find(prefix + messageId);
        PointerInput.Hover(button.BoundingRectangle);
        Thread.Sleep(500);
        return button;
    }

    // The text of a bubble fills its box edge to edge. The padding of the back is the same on both sides. So the
    // insets of the text are equal only when the text box, stretched across the back, is as wide as the text drawn in
    // it.
    private void AssertTextFits(string messageId)
    {
        Thread.Sleep(300);
        AutomationElement body          = App.Find("Bubble." + messageId);
        System.Drawing.Rectangle box    = body.BoundingRectangle;
        System.Drawing.Rectangle[] runs = body.Patterns.Text.Pattern.DocumentRange.GetBoundingRectangles();
        Assert.IsTrue(runs.Length > 0, $"Bubble {messageId}'s text has no drawn lines.");

        int left  = runs.Min(r => r.Left) - box.Left;
        int right = box.Right - runs.Max(r => r.Right);
        TestContext.WriteLine($"Bubble {messageId}: box {box}, text {string.Join(" ", runs)}, insets {left}/{right}.");
        Assert.IsTrue(Math.Abs(left - right) <= 2, $"Bubble {messageId}'s text sits {left} px from its left and {right} px from its right.");
    }

    // Reads one pixel of a selected fill, near the left end and clear of the emoji. The first read comes before the
    // pointer arrives. Then a second thread reads about every 8 ms for 600 ms, from a real move onto the fill. The
    // second thread keeps the pace of the readings. Every reading must sit between the color before and the settled
    // hover color, with 4 levels of slack. The hover must keep the accent: at least half of the color strength of the
    // fill, which is the spread of its channels.
    private void AssertSteadyHover(string name, System.Drawing.Rectangle box)
    {
        System.Drawing.Rectangle spot = new(box.Left + 5, box.Top + (box.Height / 2), 1, 1);
        FlaUI.Core.Input.Mouse.MoveTo(box.Right + 200, box.Top + (box.Height / 2));
        Thread.Sleep(500);

        System.Drawing.Color Read()
        {
            using FlaUI.Core.Capturing.CaptureImage capture = FlaUI.Core.Capturing.Capture.Rectangle(spot);
            return capture.Bitmap.GetPixel(0, 0);
        }

        System.Drawing.Color before = Read();
        var samples                 = new List<System.Drawing.Color>();
        var clock                   = System.Diagnostics.Stopwatch.StartNew();
        Exception? failure          = null;
        var sampler                 = new Thread(() =>
        {
            try
            {
                while (clock.ElapsedMilliseconds < 600)
                {
                    samples.Add(Read());
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        sampler.Start();
        PointerInput.Hover(box);
        sampler.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        System.Drawing.Color hovered = Read();
        string trace                 = string.Join(" ", samples.Select(c => $"{c.R},{c.G},{c.B}"));
        TestContext.WriteLine($"{name}: before {before}, hovered {hovered}, {samples.Count} readings: {trace}");

        static bool Between(int value, int a, int b) => value >= Math.Min(a, b) - 4 && value <= Math.Max(a, b) + 4;
        static int Strength(System.Drawing.Color c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));

        Assert.IsTrue(samples.Count >= 30, $"Only {samples.Count} readings of the {name} in 600 ms; too slow to catch a one-frame flash.");
        Assert.IsTrue(samples.All(c => Between(c.R, before.R, hovered.R) && Between(c.G, before.G, hovered.G) && Between(c.B, before.B, hovered.B)),
            $"The {name}'s fill flashed on hover (before {before}, hovered {hovered}). Readings: {trace}");
        Assert.IsTrue(Strength(hovered) * 2 >= Strength(before), $"Hovering the {name} lost my reaction's accent (before {before}, hovered {hovered}).");
    }

    // The row of the newest bubble takes keyboard focus.
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

    // The accessible item type of a bubble, when the bubble shows.
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

    // A link to the preview page of the fake server. The app previews loopback links in test runs only.
    private string PreviewLink() => $"http://127.0.0.1:{Server.BaseUrl.Port}/preview-page";

    // The newest message of the conversation on the fake phone.
    private Message Newest(string conversationId) => Server.Phone.Chats.Page(conversationId, 1, null).Page[0];

    // A message of a conversation on the fake phone, found by its text.
    private Message Find(string conversationId, string text) =>
        Server.Phone.Chats.Page(conversationId, 50, null).Page.Single(m => m.MessageInfo.Count > 0 && m.MessageInfo[0].MessageContent?.Content == text);
}
