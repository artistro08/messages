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
    /// A link that wraps counts only its letters: a click in the empty space at the end of its first line opens nothing,
    /// while a click on its first letter opens it.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task WrappedLink_EmptyLineEnd_IsNotTheLink()
    {
        await OpenChatAsync("Row.c-ann");

        // Hyphens Let The Address Wrap Partway, Leaving Space At The First Line's End
        Message message = Server.Phone.PushIncoming("c-ann", "read http://example.invalid/" + string.Join("-", Enumerable.Repeat("word", 30)) + " today");
        AutomationElement link   = App.Find($"Link.{message.MessageID}.0");
        AutomationElement body   = App.Find("Bubble." + message.MessageID);
        System.Drawing.Rectangle text  = body.BoundingRectangle;
        System.Drawing.Rectangle where = link.BoundingRectangle;
        Assert.IsTrue(where.Height > 30, $"The link ({where}) didn't wrap.");

        // The Empty End Of The Link's First Line, Just Inside The Text's Right Edge
        FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(text.Right - 2, where.Top + 6));
        Thread.Sleep(600);
        Assert.IsFalse(ReadLog().Contains("Link opened", StringComparison.Ordinal), "A click past the link's letters opened it.");

        link.Click();
        Wait.Until(() => ReadLog().Contains("Link opened (http, launched: False)", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => "A click on the link never opened it.");
    }

    /// <summary>
    /// A link in an inbox chat gets a preview card named by the linked page's <c>og:title</c> and the link's host. Once it
    /// shows, the card takes the link's place: the text reads without the address.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// Pointing at a link preview card shows the hand cursor, as a link does.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// A message that is only a link shows its preview card alone, with no empty text line above it.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task LinkOnlyMessage_ShowsCardAlone()
    {
        await OpenChatAsync("Row.c-ann");

        Message link = Server.Phone.PushIncoming("c-ann", PreviewLink());
        App.WaitForText("Preview." + link.MessageID, FakeGoogleServer.PreviewPageTitle + ", 127.0.0.1");
        WaitForGone("Bubble." + link.MessageID);
    }

    /// <summary>
    /// Right-clicking a preview card opens its own menu, whose Copy link puts the whole address on the clipboard (pasted
    /// into the message box to check); the bubble menu doesn't open.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task PreviewCard_RightClick_CopyLink_CopiesTheAddress()
    {
        await OpenChatAsync("Row.c-ann");
        string url = PreviewLink();
        Message link = Server.Phone.PushIncoming("c-ann", $"look {url}");
        App.WaitForText("Preview." + link.MessageID, FakeGoogleServer.PreviewPageTitle + ", 127.0.0.1");

        App.Find("Preview." + link.MessageID).RightClick();
        AutomationElement copy_link = App.Find("Preview.CopyLink");
        Assert.AreEqual(ChatCopy.CopyLink, AppDriver.SafeName(copy_link));
        Assert.IsNull(App.TryFind("Bubble.Copy"), "The bubble menu opened over the card's menu.");
        copy_link.Patterns.Invoke.Pattern.Invoke();

        App.Find("Chat.Draft").Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        string last = "";
        Wait.Until(() => (last = App.TryFind("Chat.Draft")?.Patterns.Value.Pattern.Value.ValueOrDefault ?? "") == url, AppDriver.DefaultTimeout,
            () => $"The message box read '{last}' after pasting, not the link.");
    }

    /// <summary>
    /// A link whose preview fails (the page isn't there) gets no card and keeps its link in the text.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// On a short message with its time shown (the time wider than the bubble), received or sent, the reaction pill still
    /// overlaps the bubble's bottom edge on its bottom-right corner, not beside the time.
    /// </summary>
    /// <returns>A task for the test.</returns>
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

        AssertPillOnCorner(bubble, pill);

        // Sent: Hi, Then Something After It So Its Time Isn't The Status Row
        App.Find("Chat.Draft").Focus();
        Keyboard.Type("Hi");
        Keyboard.Type(VirtualKeyShort.RETURN);
        Wait.Until(() => Server.Phone.Sent.Count == 1, AppDriver.DefaultTimeout, () => "The phone never got the message.");
        string sent_id = Newest("c-ann").MessageID;
        App.Find("Bubble." + Server.Phone.PushIncoming("c-ann", "Another longer message after it").MessageID);
        AutomationElement sent = App.Find("Bubble." + sent_id);
        sent.RightClick();
        App.Find("Bubble.React").Patterns.Invoke.Pattern.Invoke();
        App.Find("ReactionPicker.1").Patterns.Invoke.Pattern.Invoke();
        AutomationElement sent_pill = App.Find("Reactions." + sent_id);
        WaitForGone("ReactionPicker.1");
        Thread.Sleep(500);
        sent.Click();
        App.Find("Bubble." + sent_id + ".Time");
        Thread.Sleep(500);
        AssertPillOnCorner(sent, sent_pill);
    }

    // The Pill Crosses The Bubble's Bottom Edge At Its Bottom-Right Corner. The Text Sits 12 px Inside The Back
    private static void AssertPillOnCorner(AutomationElement bubble, AutomationElement pill)
    {
        System.Drawing.Rectangle text = bubble.BoundingRectangle;
        System.Drawing.Rectangle on   = pill.BoundingRectangle;
        Assert.IsTrue(on.Top < text.Bottom + 12 && on.Bottom > text.Bottom + 12, $"The pill {on} doesn't cross the bubble's bottom edge (text {text}).");
        Assert.IsTrue(on.Left < text.Right + 12 && on.Right > text.Right - 4, $"The pill {on} isn't on the bubble's bottom-right corner (text {text}).");
    }

    /// <summary>
    /// A reaction added to a shown bubble pops its pill in (animation effects on, as on the test machine).
    /// </summary>
    /// <returns>A task for the test.</returns>
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
                // The App Is Writing It: Read Again
            }

            return last == id;
        }, AppDriver.DefaultTimeout, () => $"The pill never popped in (last pop '{last}').");
        App.Find("Reactions." + id);
    }

    /// <summary>
    /// The picker's More reactions opens the app's emoji picker (focus in its search box, its emoji filling its width);
    /// typing "eyes" shows 👀; searching "fire" leaves 🔥,
    /// whose click closes the picker and sends the phone an Add of 🔥 as a custom reaction, and the pill shows it.
    /// </summary>
    /// <returns>A task for the test.</returns>
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

        // The First Row Fills The Picker Edge To Edge, Lined Up With The Search Box (😀 First, 😂 Eighth)
        System.Drawing.Rectangle box    = search.BoundingRectangle;
        System.Drawing.Rectangle first  = App.Find("EmojiPicker.1F600").BoundingRectangle;
        System.Drawing.Rectangle eighth = App.Find("EmojiPicker.1F602").BoundingRectangle;
        Assert.IsTrue(Math.Abs(first.Left - box.Left) <= 2 && Math.Abs(eighth.Right - box.Right) <= 2,
            $"The emoji ({first.Left} to {eighth.Right}) don't fill the search box's width ({box.Left} to {box.Right}).");

        // Typed Like The Owner Did: "eyes" Shows 👀 (And 😍, Whose Words Have It)
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
    /// Escape closes the emoji picker without reacting.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// The emoji picker's first open doesn't freeze the app (it drew every emoji at once): opened twice, the log's timings
    /// (milliseconds only) show no frame gap over 300 ms in either open, and only the rows in view are made.
    /// </summary>
    /// <returns>A task for the test.</returns>
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

            // No UI Automation Queries While It Opens: Each Find Walks The App's Tree On Its UI Thread, Which Would Be Timed Too
            string line = $"Emoji picker open {open}:";
            Wait.Until(() => ReadLog().Contains(line, StringComparison.Ordinal), AppDriver.DefaultTimeout, () => $"No '{line}' timing in the log.");
            App.Find("EmojiPicker.Search");
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Wait.Until(() => App.TryFind("EmojiPicker") is null, AppDriver.DefaultTimeout, () => "Escape left the picker open.");
        }

        // "Emoji picker open N: built in B ms, longest stall S ms, R rows made"
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(ReadLog(), @"Emoji picker open (\d+): built in \d+ ms, longest stall (\d+) ms, (\d+) rows made"))
        {
            Console.WriteLine(m.Value);
            Assert.IsLessThan(300, int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), $"Open {m.Groups[1].Value} stood the app still: {m.Value}");
            Assert.IsLessThan(40, int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), $"Open {m.Groups[1].Value} made too many rows: {m.Value}");
        }
    }

    /// <summary>
    /// The emoji are drawn once in the background after the chats show (the log's warm-up line, milliseconds and counts
    /// only), so the picker's first open after it stands the app still for even less.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task EmojiWarmUp_DrawsInBackground_ThenPickerOpensQuick()
    {
        await OpenChatAsync("Row.c-ann");
        string id = Find("c-ann", AnnLast).MessageID;

        // Two Emoji Every 100 ms After A 3 s Wait: Done Within A Minute
        Wait.Until(() => ReadLog().Contains("Emoji warm-up done:", StringComparison.Ordinal), TimeSpan.FromSeconds(90), () => "The emoji warm-up never finished.");

        App.Find("Bubble." + id).RightClick();
        App.Find("Bubble.React").Patterns.Invoke.Pattern.Invoke();
        App.Find("ReactionPicker.More").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => ReadLog().Contains("Emoji picker open 1:", StringComparison.Ordinal), AppDriver.DefaultTimeout, () => "No picker timing in the log.");

        System.Text.RegularExpressions.Match open = System.Text.RegularExpressions.Regex.Match(ReadLog(), @"Emoji picker open 1: built in \d+ ms, longest stall (\d+) ms");
        Assert.IsTrue(open.Success, "The picker's timing line didn't parse.");
        Assert.IsLessThan(300, int.Parse(open.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), $"The warmed picker stood the app still: {open.Value}");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// In Team (a group), clicking a message's reaction pill lists who reacted, in the pill's order: 👍 from Bo and me
    /// (shown as "You"), then 😂 from Cy.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// The picker for a message I reacted to marks my reaction selected (Narrator reads "Selected"); the others aren't.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// In Ann's one-on-one chat the reaction pill lists who reacted too, as in a group, and never opens the reaction
    /// picker (owner, Plan 7).
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// With my reaction on a message, hovering its pill, then my reaction in the picker, changes their color steadily: every
    /// frame of the hover sits between the color before and the settled hover color (no flash), and the hover keeps my
    /// reaction's accent (only tints it). The fill under each sat on the button itself, so its hover swapped the accent for
    /// the hover fill through the brush transition (the owner's flicker).
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// A reaction pill doesn't push the newest bubble's time down: the gap from the bubble's text to the time under it
    /// is the same (within 2 px) before and after Ann reacts.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// A short bubble fits its text, sent and received, and one ending in an emoji too: the text's left and right insets
    /// inside the bubble are equal (within 2 px), though the time and status row under the newest bubble are wider.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// Focus on the newest message (as the keyboard moves it) makes its bubble draw its own ring while the row's system focus
    /// visual stays off; focus leaving takes the ring away, and a click on the bubble doesn't bring it back (owner request).
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// The bubble's focus ring is themed: the pixels it draws carry the accent color, never Windows' white or black focus
    /// stroke.
    /// </summary>
    /// <remarks>
    /// Manual: UI Automation's focus on the row lands as FocusState.Pointer in most runs here (seen in the app's own
    /// state), which by design draws no ring, so this times out even where the ring itself works. The same flake hits
    /// KeyboardFocus_RingsTheBubble_NotTheRow when run alone.
    /// </remarks>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    [TestCategory("Manual")]
    public async Task KeyboardFocus_RingIsAccentColored()
    {
        await OpenChatAsync("Row.c-ann");
        // Ringed, Then Not (Focus Back In The Message Box), As KeyboardFocus_RingsTheBubble_NotTheRow Moves It
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

        // The Ring's Pixels: What Changed, Each One Colored (Not Gray, White Or Black)
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
    /// The hover strip's space is always kept: hovering my bubble moves it not, with the strip on its left; hovering Ann's
    /// newest bubble moves neither it nor the bubble above it in its group, with the strip on its right. The strip's React
    /// then opens the picker, so the hover took effect.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task HoverStrip_DoesNotMoveBubbles()
    {
        await OpenChatAsync("Row.c-ann");

        // A Second Message From Ann, A Second After Her Last: The Two Make One Group
        Message newest     = Server.Phone.PushIncoming("c-ann", "See you then");
        string neighbor_id = Find("c-ann", AnnLast).MessageID;
        string mine_id     = Find("c-ann", MyReply).MessageID;
        App.Find("Bubble." + newest.MessageID);

        // Settle Wait For The List To Finish Scrolling To The New Bubble
        Thread.Sleep(1000);

        // Mine: The Strip On The Left
        System.Drawing.Rectangle mine_before = App.Find("Bubble." + mine_id).BoundingRectangle;
        AutomationElement mine_react         = HoverStripButton(mine_id, "HoverReact.");
        System.Drawing.Rectangle mine_after  = App.Find("Bubble." + mine_id).BoundingRectangle;
        AssertSame(mine_before, mine_after, "My hovered bubble");
        Assert.IsTrue(mine_react.BoundingRectangle.Right <= mine_after.Left,
            $"The strip ({mine_react.BoundingRectangle}) isn't left of my bubble ({mine_after}).");

        // Ann's: The Strip On The Right, And Her Grouped Bubble Above Stays Put
        System.Drawing.Rectangle bubble_before   = App.Find("Bubble." + newest.MessageID).BoundingRectangle;
        System.Drawing.Rectangle neighbor_before = App.Find("Bubble." + neighbor_id).BoundingRectangle;
        AutomationElement react                  = HoverStripButton(newest.MessageID, "HoverReact.");
        System.Drawing.Rectangle bubble_after    = App.Find("Bubble." + newest.MessageID).BoundingRectangle;
        System.Drawing.Rectangle neighbor_after  = App.Find("Bubble." + neighbor_id).BoundingRectangle;
        AssertSame(bubble_before, bubble_after, "Ann's hovered bubble");
        AssertSame(neighbor_before, neighbor_after, "The bubble above it in its group");
        Assert.IsTrue(react.BoundingRectangle.Left >= bubble_after.Right,
            $"The strip ({react.BoundingRectangle}) isn't right of Ann's bubble ({bubble_after}).");

        // The Hover Took Effect: The Strip Takes Clicks
        react.Click();
        App.Find("ReactionPicker");

        static void AssertSame(System.Drawing.Rectangle before, System.Drawing.Rectangle after, string what) =>
            Assert.IsTrue(Math.Abs(before.Left - after.Left) <= 1 && Math.Abs(before.Top - after.Top) <= 1
                && Math.Abs(before.Width - after.Width) <= 1 && Math.Abs(before.Height - after.Height) <= 1,
                $"{what} moved on hover: {before} became {after}.");
    }

    /// <summary>
    /// My failed bubble takes no strip; retried with the pointer resting on it, the strip shows once the phone echoes it
    /// (no pointer move needed), and its ⋮ opens the bubble menu.
    /// </summary>
    /// <returns>A task for the test.</returns>
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

        // The Pointer Rests On The Failed Bubble's Text, Then The Retry Goes Through Without It Moving
        PointerInput.Hover(App.Find("Bubble." + Server.Phone.Sent[0].TmpID).BoundingRectangle);
        Server.Phone.FailSends = false;
        retry!.Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => Server.Phone.Sent.Count == 2, AppDriver.DefaultTimeout, () => $"The phone got {Server.Phone.Sent.Count} sends, not 2.");
        string id = Newest("c-ann").MessageID;
        App.Find("Bubble." + id);

        // Settle Wait For The Strip's Fade In (167 ms)
        Thread.Sleep(500);
        App.Find("HoverMore." + id).Click();

        App.Find("Bubble.Copy");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
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

        PointerInput.LeaveLeft(win);

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
    /// Clicking a reply's quote jumps to the quoted message, scrolled up out of view by newer ones, and pulses it; Enter
    /// on the focused quote does the same.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ReplyQuote_Click_JumpsToTheOriginal_AndPulsesIt()
    {
        await OpenChatAsync("Row.c-ann");
        string original = Find("c-ann", AnnFirst).MessageID;

        // Enough Newer Messages To Scroll Ann's First Out Of View
        for (int i = 0; i < 30; i++)
        {
            Server.Phone.PushIncoming("c-ann", "filler " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Message reply = Server.Phone.PushIncoming("c-ann", "Sure", replyToId: original);
        App.WaitForText("Quote." + reply.MessageID, AnnFirst);

        string pulse  = Path.Combine(App.DataDir, "pulse.txt");
        string scroll = Path.Combine(App.DataDir, "chat-scroll.txt");
        Thread.Sleep(2000);
        File.Delete(scroll);
        App.Find("QuoteJump." + reply.MessageID).Click();
        Wait.Until(() => File.Exists(pulse) && ReadPulse(pulse) == original, AppDriver.DefaultTimeout, () => "The quote's click never pulsed the original.");
        System.Drawing.Rectangle list = App.Find("Chat.Messages").BoundingRectangle;
        System.Drawing.Rectangle shown = App.Find("Bubble." + original).BoundingRectangle;
        Assert.IsTrue(shown.Top >= list.Top && shown.Bottom <= list.Bottom, $"The original ({shown}) isn't in view ({list}).");
        Assert.AreEqual("Smooth", File.Exists(scroll) ? ReadPulse(scroll) : string.Empty, "The jump to the original wasn't a smooth scroll.");

        // Enter On The Focused Quote
        File.Delete(pulse);
        App.Find("QuoteJump." + reply.MessageID).Focus();
        Keyboard.Type(VirtualKeyShort.ENTER);
        Wait.Until(() => File.Exists(pulse) && ReadPulse(pulse) == original, AppDriver.DefaultTimeout, () => "Enter on the quote never pulsed the original.");

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

    // Waits For A Bubble's Focus Ring State (Its Text's Item Status In A Test Run; See MessageBubble.ShowFocusRing)
    private static void WaitForRing(AutomationElement text, string state)
    {
        string last = "";
        Wait.Until(() => (last = text.Properties.ItemStatus.ValueOrDefault ?? "") == state, AppDriver.DefaultTimeout,
            () => $"The bubble's ring read '{last}', not '{state}'.");
    }

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

    // A Bubble's Text Fills Its Box Edge To Edge: The Back's Padding Is The Same Either Side, So The Text's Insets In The
    // Bubble Are Equal Only When The Text Box (Stretched Across The Back) Is As Wide As The Text Drawn In It
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

    // Reads One Pixel Of A Selected Fill (Near The Left End, Clear Of The Emoji) Before The Pointer Comes, Then About Every
    // 8 ms For 600 ms From A Real Move Onto It, On A Second Thread So The Readings Keep Their Pace. Every Reading Must Sit
    // Between The Color Before And The Settled Hover Color (4 Levels Of Slack), And The Hover Must Keep The Accent: At Least
    // Half The Fill's Color Strength (Its Channels' Spread)
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

    // A Link To The Fake Server's Preview Page (Loopback Links Get Previews In Test Runs Only)
    private string PreviewLink() => $"http://127.0.0.1:{Server.BaseUrl.Port}/preview-page";

    // The Conversation's Newest Message On The Fake Phone
    private Message Newest(string conversationId) => Server.Phone.Chats.Page(conversationId, 1, null).Page[0];

    // A Conversation's Message On The Fake Phone, By Its Text
    private Message Find(string conversationId, string text) =>
        Server.Phone.Chats.Page(conversationId, 50, null).Page.Single(m => m.MessageInfo.Count > 0 && m.MessageInfo[0].MessageContent?.Content == text);
}
