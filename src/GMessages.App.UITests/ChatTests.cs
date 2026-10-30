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
/// The chat pane: opening a conversation, sending, the failed send and its retry, live messages, and Start chat.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class ChatTests : UiTest
{
    /// <summary>
    /// Opening Ann's unread chat shows her three messages, tells the phone it was read, and her row stops being bold.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task OpenChat_ShowsMessagesAndMarksRead()
    {
        await OpenAnnAsync();

        Wait.Until(() => Server.Phone.Reads.Count(r => r.ConversationID == "c-ann") == 1, AppDriver.DefaultTimeout,
            () => $"The phone got {Server.Phone.Reads.Count(r => r.ConversationID == "c-ann")} reads for c-ann, not 1.");
        Wait.Until(() => !AppDriver.SafeName(App.TryFind("Row.c-ann")).Contains("Unread", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => "Ann's row stayed unread after opening her chat.");
        App.WaitForText("Chat.Header", "Ann Lee");
    }

    /// <summary>
    /// The newest bubble shows its caption row (the time) and no other bubble does; a click on the newest hides the row,
    /// and a second click shows it again.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task NewestBubble_ShowsCaption_ClickHidesAndShowsIt()
    {
        await OpenAnnAsync();
        List<(string Id, string Text)> bubbles = Bubbles();
        string id                              = bubbles[^1].Id;
        string caption                         = "Caption." + id["Bubble.".Length..];
        App.Find(caption);
        Assert.IsNull(App.TryFind("Caption." + bubbles[^2].Id["Bubble.".Length..]), "An older bubble showed a caption row.");

        App.Find(id).Click();
        WaitForGone(caption);
        App.Find(id).Click();
        App.Find(caption);
    }

    /// <summary>
    /// An older bubble's time is hidden until the bubble is clicked (on its text), and Enter on the focused bubble hides
    /// it again.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task BubbleClick_ShowsTime_EnterHidesIt()
    {
        await OpenAnnAsync();
        string id = Bubbles()[^2].Id;
        Assert.IsNull(App.TryFind(id + ".Time"), "The time showed before the bubble was clicked.");

        // Click Shows It
        App.Find(id).Click();
        App.Find(id + ".Time");

        // Enter On The Focused Bubble Hides It
        AutomationElement[] rows = App.Find("Chat.Messages").FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem));
        rows[^2].Focus();
        Keyboard.Type(VirtualKeyShort.RETURN);
        WaitForGone(id + ".Time");
    }

    /// <summary>
    /// Typing a message (Shift + Enter for a new line) and pressing Enter shows the bubble, the phone gets the text,
    /// and the phone's echo turns the same bubble into the sent message (no second bubble).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task TypeAndSend_ShowsBubble_PhoneReceives()
    {
        await OpenAnnAsync();

        TypeInDraft("See you", "at six");

        Wait.Until(() => Server.Phone.Sent.Count == 1, AppDriver.DefaultTimeout, () => "The phone never got the message.");
        string sent = FakePhone.SentText(Server.Phone.Sent[^1]);
        StringAssert.StartsWith(sent, "See you", "The phone got other text.");
        StringAssert.EndsWith(sent, "at six", "Shift + Enter didn't keep the second line in the same message.");
        Assert.IsTrue(sent.Contains('\r', StringComparison.Ordinal) || sent.Contains('\n', StringComparison.Ordinal), "Shift + Enter didn't make a new line.");

        // The Echo Gives The Bubble The Phone's Message ID
        string echo_id = Server.Phone.Chats.Page("c-ann", 1, null).Page[0].MessageID;
        Wait.Until(() => Bubbles().Any(b => b.Id == "Bubble." + echo_id), AppDriver.DefaultTimeout,
            () => $"No bubble Bubble.{echo_id}; bubbles: [{string.Join(", ", Bubbles().Select(b => b.Id))}].");
        Assert.AreEqual(4, Bubbles().Count, "The echo added a second bubble instead of updating the sent one.");
    }

    /// <summary>
    /// A send the phone refuses shows "Not sent. Click to try again."; clicking it sends the text again.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Send_PhoneFails_ShowsNotSent_RetrySends()
    {
        await OpenAnnAsync();
        Server.Phone.FailSends = true;

        TypeInDraft("Running late");
        AutomationElement retry = WaitForRetry();

        // Retry With The Phone Taking It
        Server.Phone.FailSends = false;
        retry.Patterns.Invoke.Pattern.Invoke();

        Wait.Until(() => Server.Phone.Sent.Count == 2, AppDriver.DefaultTimeout, () => $"The phone got {Server.Phone.Sent.Count} sends, not 2.");
        Assert.AreEqual("Running late", FakePhone.SentText(Server.Phone.Sent[^1]), "The retry sent other text.");
        Assert.AreNotEqual(Server.Phone.Sent[0].TmpID, Server.Phone.Sent[^1].TmpID, "The retry reused the failed temporary ID.");
        Wait.Until(() => FindRetry() is null, AppDriver.DefaultTimeout, () => "'Not sent' stayed after the retry went through.");
    }

    /// <summary>
    /// A message arriving in the open chat appends a bubble.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task LiveIncoming_InOpenChat_AppendsBubble()
    {
        await OpenAnnAsync();

        Server.Phone.PushIncoming("c-ann", "Still on?");

        Wait.Until(() => Bubbles().Count == 4 && Bubbles()[^1].Text == "Still on?", AppDriver.DefaultTimeout,
            () => $"Bubbles: [{string.Join(" | ", Bubbles().Select(b => b.Text))}].");
    }

    /// <summary>
    /// Start chat: typing a name filters the contacts, picking Ann and pressing Go opens her conversation in place
    /// of the Start chat pane.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task StartChat_PickContact_OpensConversation()
    {
        // From An Open Chat: Start Chat Takes Its Place
        await OpenAnnAsync();
        App.Find("Main.StartChat").Patterns.Invoke.Pattern.Invoke();
        App.Find("StartChat.Query").Patterns.Value.Pattern.SetValue("Ann");
        Wait.Until(() => App.TryFind("Chat.Draft") is null && App.TryFind("Main.EmptyState") is null, AppDriver.DefaultTimeout,
            () => "The chat pane or the empty state still showed under Start chat.");

        // Only Ann Matches
        Wait.Until(() => AppDriver.SafeName(App.TryFind("Contact.0")).Contains("Ann Lee", StringComparison.Ordinal) && App.TryFind("Contact.1") is null,
            AppDriver.DefaultTimeout, () => $"Contact.0 was '{AppDriver.SafeName(App.TryFind("Contact.0"))}', Contact.1 was '{AppDriver.SafeName(App.TryFind("Contact.1"))}'.");
        // The Real Mouse Clicks Whatever Window Is On Top There. A Window Opened Through UI Automation (No Real Input)
        // Isn't Allowed The Foreground, So It Can Sit Behind Another Window And The Click Misses It: Bring It Up First
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
        App.Find("Contact.0").Click();

        // Go Opens Her Conversation
        AutomationElement go = App.Find("StartChat.Go");
        Wait.Until(() => go.IsEnabled, AppDriver.DefaultTimeout, () => "Go stayed disabled after picking Ann.");
        go.Patterns.Invoke.Pattern.Invoke();

        App.WaitForText("Chat.Header", "Ann Lee");
        Wait.Until(() => App.TryFind("StartChat.Query") is null, AppDriver.DefaultTimeout, () => "The Start chat pane stayed open.");
        Wait.Until(() => Bubbles().Count == 3, AppDriver.DefaultTimeout, () => $"Ann's chat showed {Bubbles().Count} bubbles, not 3.");
    }

    /// <summary>
    /// A first page that doesn't fill the list (here all system lines, which show no bubble) loads older pages on its
    /// own, since there is nothing to scroll up to.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ShortFirstPage_LoadsOlderUntilItScrolls()
    {
        // Five Real Messages, Then A Full Page Of System Lines Above Them In Time
        Conversation old = FakeChats.NewConversation("c-old", "Old Pal", false, ConversationStatus.Active, false, false, FakeChats.Person("p-old", "Old Pal", "+15550000005"));
        long time        = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() * 1000;
        Message[] texts  = [.. Enumerable.Range(1, 5).Select(i => Line($"Old {i}", MessageStatusType.IncomingComplete))];
        Message[] lines  = [.. Enumerable.Range(1, ChatStore.PageSize).Select(i => Line($"System {i}", (MessageStatusType)200))];
        Server.Phone.Chats.Add(old, [.. texts, .. lines]);

        await OpenMainAsync();
        App.Find("Row.c-old").Patterns.SelectionItem.Pattern.Select();

        Wait.Until(() => Bubbles().Count == 5, AppDriver.DefaultTimeout, () => $"The short chat showed {Bubbles().Count} bubbles, not the 5 older ones.");

        Message Line(string text, MessageStatusType status)
        {
            var message = new Message
            {
                MessageID      = "m-" + text.Replace(' ', '-'),
                ConversationID = "c-old",
                ParticipantID  = "p-old",
                Timestamp      = time += 1_000_000,
                MessageStatus  = new MessageStatus { Status = status },
            };
            message.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = text } });
            return message;
        }
    }

    /// <summary>
    /// Scrolling up a long chat loads older pages, and the scroll-to-bottom button shows once far up and takes the list
    /// back to the newest message.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ScrollUp_LoadsOlder_ScrollToBottomReturns()
    {
        // Three Pages Of Texts
        Conversation chat = FakeChats.NewConversation("c-long", "Long Pal", false, ConversationStatus.Active, false, false, FakeChats.Person("p-long", "Long Pal", "+15550000006"));
        long time         = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() * 1000;
        // Mixed Heights, Like A Real Chat: Every Third Message Wraps Over Several Lines
        Message[] texts   = [.. Enumerable.Range(1, ChatStore.PageSize * 3).Select(i => Line(i % 3 == 0 ? $"Line {i} " + string.Concat(Enumerable.Repeat("wrapping words ", 30)) : $"Line {i}"))];
        Server.Phone.Chats.Add(chat, texts);

        await OpenMainAsync();
        App.Find("Row.c-long").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Shows("m-", NewestLine), AppDriver.DefaultTimeout, () => $"The long chat showed {Bubbles().Count} bubbles, without its newest.");

        // Opened, It Settles On The Newest Message And Stays There (No Scroll Up, So No Bottom Fade Or Button)
        var scroll = App.Find("Chat.Messages").Patterns.Scroll.Pattern;
        Thread.Sleep(2000);
        AssertAtNewest("Opening the chat");

        // Up To The Top: The Older Page Comes In, And The Button Shows
        Wait.Until(() =>
        {
            // -1: Leave The Horizontal Position Alone (UI Automation's NoScroll)
            scroll.SetScrollPercent(-1, 0);
            return OldestShown("m-") <= OlderPageLast;
        }, AppDriver.DefaultTimeout, () => $"Scrolling up showed line {OldestShown("m-")} at the oldest; no older page loaded.");
        AutomationElement down = App.Find("Chat.ScrollToBottom");
        Wait.Until(() => down.Properties.IsKeyboardFocusable.ValueOrDefault, AppDriver.DefaultTimeout, () => "Scroll to bottom didn't show far up the chat.");

        // The Button: Back At The Newest, And It Hides Again
        down.Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => scroll.VerticalScrollPercent.ValueOrDefault >= 99, AppDriver.DefaultTimeout, () => $"Scroll to bottom left the list at {scroll.VerticalScrollPercent.ValueOrDefault:0}%.");
        Wait.Until(() => !down.Properties.IsKeyboardFocusable.ValueOrDefault, AppDriver.DefaultTimeout, () => "Scroll to bottom stayed at the bottom of the chat.");

        // Reopened, The Window Shows The Same Chat From What The App Kept: Scrolling Up Works There Too
        CloseMainWindow();
        Tray.LeftClick();
        Wait.Until(() => Shows("m-", NewestLine), AppDriver.DefaultTimeout, () => $"The reopened chat showed {Bubbles().Count} bubbles, without its newest.");
        scroll = App.Find("Chat.Messages").Patterns.Scroll.Pattern;
        down   = App.Find("Chat.ScrollToBottom");
        Thread.Sleep(2000);
        AssertAtNewest("Reopening the chat");
        Wait.Until(() =>
        {
            scroll.SetScrollPercent(-1, 0);
            return OldestShown("m-") <= OlderPageLast && down.Properties.IsKeyboardFocusable.ValueOrDefault;
        }, AppDriver.DefaultTimeout, () => $"In the reopened chat, scrolling up showed line {OldestShown("m-")} at the oldest and Scroll to bottom showed: {down.Properties.IsKeyboardFocusable.ValueOrDefault}.");

        Message Line(string text)
        {
            var message = new Message
            {
                MessageID      = "m-" + text.Split(' ')[1],
                ConversationID = "c-long",
                ParticipantID  = "p-long",
                Timestamp      = time += 1_000_000,
                MessageStatus  = new MessageStatus { Status = MessageStatusType.IncomingComplete },
            };
            message.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = text } });
            return message;
        }
    }

    /// <summary>
    /// Scrolling up with the mouse wheel (real input, not UI Automation) loads older pages, and the scroll-to-bottom
    /// button shows once far up.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task WheelUp_LoadsOlder_ShowsScrollToBottom()
    {
        // Three Pages Of Texts
        Conversation chat = FakeChats.NewConversation("c-wheel", "Wheel Pal", false, ConversationStatus.Active, false, false, FakeChats.Person("p-wheel", "Wheel Pal", "+15550000007"));
        long time         = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() * 1000;
        // Mixed Heights, Like A Real Chat: Every Third Message Wraps Over Several Lines
        Message[] texts   = [.. Enumerable.Range(1, ChatStore.PageSize * 3).Select(i => Line(i % 3 == 0 ? $"Line {i} " + string.Concat(Enumerable.Repeat("wrapping words ", 30)) : $"Line {i}"))];
        Server.Phone.Chats.Add(chat, texts);

        await OpenMainAsync();
        App.Find("Row.c-wheel").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Shows("w-", NewestLine), AppDriver.DefaultTimeout, () => $"The wheel chat showed {Bubbles().Count} bubbles, without its newest.");

        // Wheel Up Over The Bubbles Until The Older Page Comes In And The Button Shows
        AutomationElement list = App.Find("Chat.Messages");
        AutomationElement down = App.Find("Chat.ScrollToBottom");
        System.Drawing.Rectangle over = list.BoundingRectangle;
        FlaUI.Core.Input.Mouse.MoveTo(over.X + (over.Width / 2), over.Y + (over.Height / 2));
        Wait.Until(() =>
        {
            FlaUI.Core.Input.Mouse.Scroll(5);
            return OldestShown("w-") <= OlderPageLast && down.Properties.IsKeyboardFocusable.ValueOrDefault;
        }, AppDriver.DefaultTimeout, () => $"Wheeling up showed line {OldestShown("w-")} at the oldest; Scroll to bottom showed: {down.Properties.IsKeyboardFocusable.ValueOrDefault}.");

        Message Line(string text)
        {
            var message = new Message
            {
                MessageID      = "w-" + text.Split(' ')[1],
                ConversationID = "c-wheel",
                ParticipantID  = "p-wheel",
                Timestamp      = time += 1_000_000,
                MessageStatus  = new MessageStatus { Status = MessageStatusType.IncomingComplete },
            };
            message.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = text } });
            return message;
        }
    }

    /// <summary>
    /// Switching back and forth between two chats that were opened before (their bubbles shown from what the app kept)
    /// leaves each one on its newest message, not a few pixels short of it under the bottom fade.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task SwitchChats_EachOpensOnNewest()
    {
        AddTwoPageChats("sa", "sb");

        await OpenMainAsync();
        foreach (string id in new[] { "sa", "sb", "sa", "sb" })
        {
            App.Find("Row.c-" + id).Patterns.SelectionItem.Pattern.Select();
            Wait.Until(() => Bubbles().Any(b => b.Id == $"Bubble.{id}-{ChatStore.PageSize * 2}"), AppDriver.DefaultTimeout, () => $"Chat {id} didn't show its newest bubble.");
            Thread.Sleep(2000);
            AssertAtNewest($"Opening chat {id}");
        }
    }

    /// <summary>
    /// Clicking between chats (never opened and opened before) shows no flash, sampled about every 8 ms from the click:
    /// the message box keeps its focused look (a click on a row once took the focus for a moment), and no frame paints
    /// the list's gray scrolling placeholders over the rows.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task SwitchChats_ByClick_NoFlash()
    {
        AddTwoPageChats("fa", "fb", "fc");

        await OpenMainAsync();
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
        App.Find("Row.c-fa").Click();
        WaitForNewest("fa");
        Wait.Until(() => App.Find("Chat.Draft").Properties.HasKeyboardFocus.ValueOrDefault, AppDriver.DefaultTimeout, () => "Opening fa didn't focus the message box.");
        Thread.Sleep(1000);

        // Never Opened, Opened Before, Never Opened, Opened Before
        foreach (string id in new[] { "fb", "fa", "fc", "fb" })
        {
            AssertNoFlash(id);
        }
    }

    /// <summary>
    /// The message box shows one color (its focused, active fill) at rest, under the pointer and focused, and typing
    /// still works after those changes (owner's call).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task DraftBox_KeepsOneColor_AtRestHoverAndFocused()
    {
        await OpenAnnAsync();
        AutomationElement draft         = App.Find("Chat.Draft");
        System.Drawing.Rectangle box    = draft.BoundingRectangle;
        System.Drawing.Rectangle header = App.Find("Chat.Header").BoundingRectangle;

        // An Empty Spot In The Box: Left Of Send, Away From The Caret And The Bottom Accent Line
        System.Drawing.Rectangle spot = new(box.Right - 60, box.Top + (box.Height / 2), 1, 1);

        // Focused, Pointer Away
        PointerInput.Hover(header);
        System.Drawing.Color focused = SettledPixel(spot);

        // At Rest: The Focus On A Bubble, Pointer Away
        AutomationElement[] rows = App.Find("Chat.Messages").FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem));
        rows[^1].Focus();
        Wait.Until(() => !draft.Properties.HasKeyboardFocus.ValueOrDefault, AppDriver.DefaultTimeout, () => "The message box kept the focus.");
        System.Drawing.Color rest = SettledPixel(spot);

        // Under The Pointer, Not Focused
        PointerInput.Hover(box);
        System.Drawing.Color hover = SettledPixel(spot);

        TestContext.WriteLine($"Message box: focused {focused}, at rest {rest}, hover {hover}.");
        Assert.IsTrue(Near(rest, focused), $"At rest the message box was {rest}, not its focused {focused}.");
        Assert.IsTrue(Near(hover, focused), $"Under the pointer the message box was {hover}, not its focused {focused}.");

        // Typing Still Works
        draft.Click();
        Keyboard.Type("Hi");
        Wait.Until(() => draft.Patterns.Value.Pattern.Value.ValueOrDefault == "Hi", AppDriver.DefaultTimeout,
            () => "Typing in the message box after the hover and focus changes didn't show the text.");

        static bool Near(System.Drawing.Color a, System.Drawing.Color b) => Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2;
    }

    /// <summary>
    /// While an older page loads and lands, the bubbles on screen stay where they are: no blanking, no jump. The
    /// oldest shown bubble keeps its place on screen (within 4 px) from the loading ring showing until a second after
    /// the page landed.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task OlderPage_KeepsVisibleBubblesInPlace()
    {
        // A Long Chat Whose Older Pages Take Two Seconds: The Baseline Settles (Within One) Before The Page Lands
        Server.Phone.Chats.AddLongChat();
        Server.Phone.OlderPagesDelay = TimeSpan.FromSeconds(2);
        const string Oldest          = "Bubble.c-history-096";
        const string NextPage        = "Bubble.c-history-071";

        await OpenMainAsync();
        App.Find("Row.c-history").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Any(b => b.Text.StartsWith("long 120", StringComparison.Ordinal)), AppDriver.DefaultTimeout,
            () => $"The long chat didn't show 'long 120'; bubbles: [{string.Join(", ", Bubbles().Select(b => b.Id))}].");

        // Up Until The Older Page Is Asked For, Then It Lands Without Moving The Shown Bubbles
        AutomationElement list = App.Find("Chat.Messages");
        var scroll             = list.Patterns.Scroll.Pattern;
        AssertOlderPageKeepsInPlace(list, Oldest, () => scroll.SetScrollPercent(-1, 0));

        // The Older Page Is There (Scrolled To If It Sits Above The Realized Bubbles)
        Wait.Until(() =>
        {
            if (Bubbles().Any(b => b.Id == NextPage))
            {
                return true;
            }

            scroll.SetScrollPercent(-1, 0);
            return false;
        }, AppDriver.DefaultTimeout, () => $"No {NextPage} after the older page landed.");
    }

    /// <summary>
    /// The same with the mouse wheel in a chat like a real one (mostly one-liners, some wrapping, a few very tall) whose
    /// older pages take as long as a real phone's: the shown bubbles stay put, and the page that landed isn't followed
    /// at once by another while the view still sits at its top.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task OlderPage_WheelInMixedChat_KeepsVisibleBubblesInPlace()
    {
        // Four Pages Of Mixed Heights; Older Pages Take 0.7 Seconds, As In The Owner's Recording
        const int Count   = ChatStore.PageSize * 4;
        Conversation chat = FakeChats.NewConversation("c-mixed", "Mixed Pal", false, ConversationStatus.Active, false, false, FakeChats.Person("p-mixed", "Mixed Pal", "+15550000008"));
        long time         = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() * 1000;
        Server.Phone.Chats.Add(chat, [.. Enumerable.Range(1, Count).Select(Line)]);
        Server.Phone.OlderPagesDelay = TimeSpan.FromMilliseconds(700);
        string oldest                = $"Bubble.m-{Count - ChatStore.PageSize + 1:000}";

        await OpenMainAsync();
        App.Find("Row.c-mixed").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Any(b => b.Id == $"Bubble.m-{Count:000}"), AppDriver.DefaultTimeout, () => "The mixed chat didn't show its newest bubble.");

        // Wheel Up Over The Bubbles Until The Older Page Is Asked For
        AutomationElement list        = App.Find("Chat.Messages");
        System.Drawing.Rectangle over = list.BoundingRectangle;
        FlaUI.Core.Input.Mouse.MoveTo(over.X + (over.Width / 2), over.Y + (over.Height / 2));
        AssertOlderPageKeepsInPlace(list, oldest, () => FlaUI.Core.Input.Mouse.Scroll(5));

        Message Line(int i)
        {
            bool from_me = (i / 2 + i / 7) % 2 == 0;
            string text  = (i % 11, i % 4) switch
            {
                (0, _) => $"Mixed {i} " + string.Concat(Enumerable.Repeat("a very tall message ", 30)),
                (_, 0) => $"Mixed {i} " + string.Concat(Enumerable.Repeat("wraps once ", 9)),
                _      => $"Mixed {i}",
            };
            var message = new Message
            {
                MessageID      = $"m-{i:000}",
                ConversationID = "c-mixed",
                ParticipantID  = from_me ? FakeChats.MyParticipantId : "p-mixed",
                Timestamp      = time += 1_000_000,
                MessageStatus  = new MessageStatus { Status = from_me ? MessageStatusType.OutgoingComplete : MessageStatusType.IncomingComplete },
            };
            message.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = text } });
            return message;
        }
    }

    /// <summary>
    /// Scroll to bottom, pressed while an older page is still on its way, ends on the newest message and stays there
    /// once the page lands (the older page's anchoring doesn't pull the list back up).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ScrollToNewest_WhileOlderPageLoads_EndsAtNewest()
    {
        // A Long Chat Whose Older Pages Take Two Seconds
        Server.Phone.Chats.AddLongChat();
        Server.Phone.OlderPagesDelay = TimeSpan.FromSeconds(2);

        await OpenMainAsync();
        App.Find("Row.c-history").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Any(b => b.Text.StartsWith("long 120", StringComparison.Ordinal)), AppDriver.DefaultTimeout,
            () => $"The long chat didn't show 'long 120'; bubbles: [{string.Join(", ", Bubbles().Select(b => b.Id))}].");

        // Up Until The Older Page Is Asked For
        AutomationElement list = App.Find("Chat.Messages");
        var scroll             = list.Patterns.Scroll.Pattern;
        Wait.Until(() =>
        {
            scroll.SetScrollPercent(-1, 0);
            return App.TryFind("Chat.LoadingOlder") is not null;
        }, AppDriver.DefaultTimeout, () => "Scrolling up never showed the loading ring.");

        // The Button, While The Page Is Still On Its Way
        AutomationElement down = App.Find("Chat.ScrollToBottom");
        Wait.Until(() => down.Properties.IsKeyboardFocusable.ValueOrDefault, AppDriver.DefaultTimeout, () => "Scroll to bottom didn't show at the top of the chat.");
        down.Patterns.Invoke.Pattern.Invoke();

        // The Page Lands, Then The List Settles
        Wait.Until(() => App.TryFind("Chat.LoadingOlder") is null, AppDriver.DefaultTimeout, () => "The older page never landed.");
        Thread.Sleep(1000);
        AssertAtNewest("Scroll to bottom pressed while the older page loaded");

        // And Stays There For A Second
        DateTime until = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(100);
            AssertAtNewest("A second after the older page landed, the list");
        }
    }

    /// <summary>
    /// Reopening a chat the app kept shows its bubbles at once, with no ring (big or small) while the phone's page loads
    /// (owner's call).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Reopen_CachedChat_ShowsNoRingWhilePhoneLoads()
    {
        // Ann And Team Opened Once, So Both Are Kept
        await OpenAnnAsync();
        App.Find("Row.c-team").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Any(b => b.Text == "Works for me"), AppDriver.DefaultTimeout, () => "Team never showed its bubbles.");

        // Back To Ann With The Phone Slow
        Server.Phone.MessagesDelay = TimeSpan.FromSeconds(3);
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();

        Wait.Until(() => Bubbles().Count == 3, TimeSpan.FromSeconds(2), () => $"Ann's kept bubbles didn't show at once; bubbles: {Bubbles().Count}.");

        // No Ring While The Phone Is Slow
        DateTime until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
        {
            Assert.IsNull(App.TryFind("Chat.LoadingOlder"), "The top ring showed over kept bubbles.");
            Assert.IsTrue(App.TryFind("Chat.Loading") is not { IsOffscreen: false }, "The big ring showed over kept bubbles.");
            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// After a restart, a chat opened before shows its bubbles from the encrypted database, before the phone answers.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Restart_OpensChatFromDatabase_BeforePhoneAnswers()
    {
        await OpenAnnAsync();

        await App.RestartAsync();

        // The Phone Slow, So Only The Database Can Show The Bubbles In Time
        Server.Phone.MessagesDelay = TimeSpan.FromSeconds(5);
        Tray.LeftClick();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();

        Wait.Until(() => Bubbles().Count == 3, TimeSpan.FromSeconds(2), () => $"Ann's saved chat showed {Bubbles().Count} bubbles within 2 s, not 3.");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // True When The List Holds At Least One Bubble (A Blanked List Holds None)
    private static bool AnyBubble(AutomationElement list)
    {
        try
        {
            return list.FindFirstDescendant(cf => cf.ByAutomationId("Bubble.", FlaUI.Core.Definitions.PropertyConditionFlags.MatchSubstring)) is not null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            return false;
        }
    }

    // Adds Chats Like Real Ones (c-{id}, Pal {id}): Two Pages Of Sent And Received One-Liners ({id}-1 Up), Some With Emoji,
    // A Screen And A Half Per Page
    private void AddTwoPageChats(params string[] ids)
    {
        long time = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() * 1000;
        foreach (string id in ids)
        {
            Conversation chat = FakeChats.NewConversation("c-" + id, "Pal " + id, false, ConversationStatus.Active, false, false, FakeChats.Person("p-" + id, "Pal " + id, "+1555000001" + id.Length));
            Server.Phone.Chats.Add(chat, [.. Enumerable.Range(1, ChatStore.PageSize * 2).Select(i => Line(id, i))]);
        }

        Message Line(string id, int i)
        {
            var message = new Message
            {
                MessageID      = $"{id}-{i}",
                ConversationID = "c-" + id,
                ParticipantID  = i % 2 == 0 ? FakeChats.MyParticipantId : "p-" + id,
                Timestamp      = time += 1_000_000,
                MessageStatus  = new MessageStatus { Status = i % 2 == 0 ? MessageStatusType.OutgoingComplete : MessageStatusType.IncomingComplete },
            };
            message.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = i % 3 == 0 ? $"Line {i} \U0001F923\U0001F923" : $"Line {i}" } });
            return message;
        }
    }

    // Waits For A Chat's Newest Bubble (Line Two Pages In)
    private void WaitForNewest(string id) =>
        Wait.Until(() => App.TryFind($"Bubble.{id}-{ChatStore.PageSize * 2}") is not null, AppDriver.DefaultTimeout, () => $"Chat {id} didn't show its newest bubble.");

    // Clicks A Chat's Row (Real Input) And Reads The Screen About Every 8 ms For 800 ms. Each Reading Holds The Message
    // Box's Brightness And How Much Lighter (Or Darker) The Empty Middle Of The List Is Than Its Left Padding, Averaged
    // Down The List. The Gray Placeholders Cover The Rows But Not The Padding, And The Middle Column Holds No Bubble Here
    // (Received Ones Sit Left, Sent Ones Right), So Before The Click Both Read The Same
    private void AssertNoFlash(string id)
    {
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
        System.Drawing.Rectangle view  = App.Find("Chat.Messages").BoundingRectangle;
        System.Drawing.Rectangle draft = App.Find("Chat.Draft").BoundingRectangle;
        System.Drawing.Rectangle row   = App.Find("Row.c-" + id).BoundingRectangle;
        System.Drawing.Rectangle strip = System.Drawing.Rectangle.FromLTRB(view.Left, view.Top, view.Left + (view.Width / 2) + 1, draft.Bottom);
        FlaUI.Core.Input.Mouse.MoveTo(row.X + (row.Width / 2), row.Y + (row.Height / 2));
        Thread.Sleep(300);

        (double Draft, double Lighter) Read()
        {
            using FlaUI.Core.Capturing.CaptureImage capture = FlaUI.Core.Capturing.Capture.Rectangle(strip);
            System.Drawing.Bitmap bitmap = capture.Bitmap;
            double draft_luma            = Luma(bitmap.GetPixel(draft.Left + 120 - strip.Left, draft.Top + (draft.Height / 2) - strip.Top));
            double lighter               = 0;
            int rows                     = 0;
            for (int y = 40; y < view.Height - 40; y += 40, rows++)
            {
                lighter += Luma(bitmap.GetPixel(view.Width / 2, y)) - Luma(bitmap.GetPixel(5, y));
            }

            return (draft_luma, lighter / rows);
        }

        // Before: The Focused Message Box, And The List's Middle As Dark As Its Padding
        (double draft_before, double lighter_before) = Read();
        Assert.IsTrue(Math.Abs(lighter_before) < 1, $"Before opening {id}, the list's middle read {lighter_before:0.0} off its padding; the check needs it empty.");

        // Click, Then Sample On A Second Thread So The Readings Keep Their Pace. A Failed Reading Is Kept And Thrown Here
        // Once The Thread Ends, So It Fails The Test Instead Of The Test Host
        var samples        = new List<(double Ms, double Draft, double Lighter)>();
        var clock          = System.Diagnostics.Stopwatch.StartNew();
        Exception? failure = null;
        var sampler        = new Thread(() =>
        {
            try
            {
                while (clock.ElapsedMilliseconds < 800)
                {
                    (double d, double l) = Read();
                    samples.Add((clock.Elapsed.TotalMilliseconds, d, l));
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        sampler.Start();
        FlaUI.Core.Input.Mouse.Click();
        sampler.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        string trace = string.Join(" ", samples.Select(s => $"{s.Ms:0}:{s.Draft - draft_before:0}/{s.Lighter:0.0}"));
        TestContext.WriteLine($"{id}: {samples.Count} readings (ms:box/list) {trace}");
        WaitForNewest(id);
        Assert.IsTrue(samples.Count >= 40, $"Only {samples.Count} readings in 800 ms; too slow to catch a one-frame flash.");
        Assert.IsFalse(samples.Any(s => Math.Abs(s.Draft - draft_before) > 8), $"Opening {id}, the message box lost its focused look for a moment. Readings: {trace}");
        Assert.IsFalse(samples.Any(s => Math.Abs(s.Lighter) >= 1.5), $"Opening {id}, the list's rows flashed gray. Readings: {trace}");
        Thread.Sleep(1000);
    }

    private static double Luma(System.Drawing.Color pixel) => (0.299 * pixel.R) + (0.587 * pixel.G) + (0.114 * pixel.B);

    // One Screen Pixel, Read After The Control's Brush Transitions Have Had Time To Finish
    private System.Drawing.Color SettledPixel(System.Drawing.Rectangle spot)
    {
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
        Thread.Sleep(500);
        using FlaUI.Core.Capturing.CaptureImage capture = FlaUI.Core.Capturing.Capture.Rectangle(spot);
        return capture.Bitmap.GetPixel(0, 0);
    }

    // Scrolls Up Until The Loading Ring Shows, Then Checks The Bubble Stays Put (Within 4 px) Every 50 ms From Its Settled
    // Place Until The Ring Has Been Gone A Second: The Page Lands Above Without Moving It, Blanking The List Or Asking
    // For Another Page Before The View Is Put Back
    private void AssertOlderPageKeepsInPlace(AutomationElement list, string watched, Action scrollUp)
    {
        Wait.Until(() =>
        {
            scrollUp();
            return App.TryFind("Chat.LoadingOlder") is not null;
        }, AppDriver.DefaultTimeout, () => "Scrolling up never showed the loading ring.");

        // Where The Bubble Sits Once The Scroll Settled
        int? recorded = null;
        Wait.Until(() =>
        {
            int? y    = BubbleTop(list, watched);
            bool same = y is not null && y == recorded;
            recorded  = y;
            return same;
        }, TimeSpan.FromSeconds(1), () => $"{watched} never settled on screen (last top: {recorded?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "gone"}).");

        // Every 50 ms Until The Page Lands, Then A Second More: It Stays Put
        var samples         = new List<string>();
        DateTime landed_cap = DateTime.UtcNow + AppDriver.DefaultTimeout;
        DateTime? until     = null;
        while (until is null || DateTime.UtcNow < until)
        {
            int? y   = BubbleTop(list, watched);
            bool any = AnyBubble(list);

            // UI Automation Can See The List With No Rows For A Moment While The Page Goes In, Though The Screen Never Stops
            // Showing Them (Frames Captured Every 8 ms Around Six Such Readings All Showed The Bubbles In Place). A Reading
            // With No Bubbles Counts Once 3 Readings 50 ms Apart Agree; Such A Sample Is Marked "~"
            string mark = "";
            if (!any)
            {
                (any, y) = Settled(() => (AnyBubble(list), BubbleTop(list, watched)));
                mark     = "~";
            }

            samples.Add((y?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "off") + mark);
            Assert.IsTrue(any, $"The list showed no bubbles during the older page's arrival. Samples: {string.Join(" ", samples)}.");
            Assert.IsTrue(y is { } top && Math.Abs(top - recorded!.Value) <= 4,
                $"{watched} moved from y={recorded} during the older page's arrival. Samples: {string.Join(" ", samples)}.");

            if (until is null && App.TryFind("Chat.LoadingOlder") is null)
            {
                until = DateTime.UtcNow.AddSeconds(1);
            }

            Assert.IsTrue(DateTime.UtcNow < landed_cap, $"The older page never landed. Samples: {string.Join(" ", samples)}.");
            Thread.Sleep(50);
        }

        TestContext.WriteLine($"{watched} tops: {string.Join(" ", samples)}");
    }

    // Reads Again Every 50 ms Until 3 Readings In A Row Agree (Up To A Second), And Gives The Last
    private static T Settled<T>(Func<T> read)
    {
        T last         = read();
        int same       = 1;
        DateTime until = DateTime.UtcNow.AddSeconds(1);
        while (same < 3 && DateTime.UtcNow < until)
        {
            Thread.Sleep(50);
            T now = read();
            same  = EqualityComparer<T>.Default.Equals(now, last) ? same + 1 : 1;
            last  = now;
        }

        return last;
    }

    // A Bubble's Screen Top, Or Null When It Is Gone, Off Screen Or Outside The List
    private static int? BubbleTop(AutomationElement list, string automationId)
    {
        try
        {
            AutomationElement? bubble = list.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (bubble is null || bubble.Properties.IsOffscreen.ValueOrDefault)
            {
                return null;
            }

            System.Drawing.Rectangle box  = bubble.BoundingRectangle;
            System.Drawing.Rectangle view = list.BoundingRectangle;
            return box.Bottom > view.Top && box.Top < view.Bottom ? box.Top : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            return null;
        }
    }

    // Three-Page Chats (Lines 1 To 3 Pages): The Newest Line, And The Last Line Of The Page Above The Newest One
    private const int NewestLine    = ChatStore.PageSize * 3;
    private const int OlderPageLast = ChatStore.PageSize * 2;

    // Whether The Bubble Of A Line ("m-12") Is In The List. The List Only Makes The Rows In And Near The View, So How Many
    // Bubbles UI Automation Sees Depends On Their Heights (Narrow Windows Wrap More): Tests Look For Particular Lines
    private bool Shows(string prefix, int line) => Bubbles().Any(b => b.Id == $"Bubble.{prefix}{line}");

    // The Oldest Line Among The Bubbles In The List, Or int.MaxValue For None
    private int OldestShown(string prefix) =>
        Bubbles().Select(b => b.Id.StartsWith("Bubble." + prefix, StringComparison.Ordinal) && int.TryParse(b.Id.AsSpan(7 + prefix.Length), out int line) ? line : int.MaxValue)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

    // The Message List Sits On Its Newest Message: Less Than A Pixel Short Of Its End, So No Bottom Fade Or Arrow
    private void AssertAtNewest(string what)
    {
        AutomationElement list = App.Find("Chat.Messages");
        var scroll             = list.Patterns.Scroll.Pattern;
        double view_size       = scroll.VerticalViewSize.ValueOrDefault;
        double viewport        = list.BoundingRectangle.Height;
        double scrollable      = view_size is > 0 and < 100 ? viewport * 100 / view_size - viewport : 0;
        double short_by        = (100 - scroll.VerticalScrollPercent.ValueOrDefault) / 100 * scrollable;
        Assert.IsTrue(short_by < 1, $"{what} left the list {short_by:0.0} px short of the newest message.");
    }

    // Opens The Main Window, Then Ann's Chat, And Waits For Her Three Messages
    private async Task OpenAnnAsync()
    {
        await OpenMainAsync();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Count == 3, AppDriver.DefaultTimeout, () => $"Ann's chat showed {Bubbles().Count} bubbles, not 3.");

        // Opening The Chat Puts The Cursor In The Message Box; Waited For (Window In Front), So It Can't Take Focus From A
        // Row Later
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
        Wait.Until(() => App.Find("Chat.Draft").Properties.HasKeyboardFocus.ValueOrDefault, AppDriver.DefaultTimeout, () => "Opening Ann's chat didn't focus the message box.");
    }

    // Types Each Line In The Message Box, Shift + Enter Between Them, Then Enter To Send
    private void TypeInDraft(params string[] lines)
    {
        App.Find("Chat.Draft").Focus();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.RETURN);
            }

            Keyboard.Type(lines[i]);
        }

        Keyboard.Type(VirtualKeyShort.RETURN);
    }

    // The Bubbles In The Open Chat, Top To Bottom: AutomationId And Text
    private List<(string Id, string Text)> Bubbles()
    {
        AutomationElement? list = App.TryFind("Chat.Messages");
        if (list is null)
        {
            return [];
        }

        try
        {
            return [.. list.FindAllDescendants()
                .Select(e => (Id: e.Properties.AutomationId.ValueOrDefault ?? "", Text: AppDriver.SafeName(e)))
                .Where(b => b.Id.StartsWith("Bubble.", StringComparison.Ordinal) && !b.Id.EndsWith(".Retry", StringComparison.Ordinal) && !b.Id.EndsWith(".Time", StringComparison.Ordinal))];
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            // The List Changed Mid-Walk; Read Again
            return [];
        }
    }

    // The Failed Bubble's "Not sent" Button, Or Null
    private AutomationElement? FindRetry() =>
        App.TryFind("Chat.Messages") is { } list ? AppDriver.Search(list, e => AppDriver.SafeName(e) == ChatCopy.NotSentRetry) : null;

    private AutomationElement WaitForRetry()
    {
        AutomationElement? retry = null;
        Wait.Until(() => (retry = FindRetry()) is not null, AppDriver.DefaultTimeout, () => $"'{ChatCopy.NotSentRetry}' never showed.");
        return retry!;
    }
}
