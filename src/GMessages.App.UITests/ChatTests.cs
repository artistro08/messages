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
    /// A right-click on a misspelled word in the message box puts the Windows spelling fixes first in the app menu.
    /// A click on a fix corrects the word, and Ignore leaves the word unmarked.
    /// </summary>
    [TestMethod]
    public async Task RightClick_MisspelledWord_OffersTheFix()
    {
        await OpenAnnAsync();
        AutomationElement draft = App.Find("Chat.Draft");
        draft.Focus();

        // The test types the text and does not set it, because Windows marks only typed words.
        Keyboard.Type("helo there ");
        PickSpellItem(draft, "hello");
        Wait.Until(() => draft.Patterns.Value.Pattern.Value.Value == "hello there ", AppDriver.DefaultTimeout,
            () => $"The box read '{draft.Patterns.Value.Pattern.Value.Value}', not 'hello there '.");

        // After Ignore, the word offers no fixes.
        draft.Patterns.Value.Pattern.SetValue(string.Empty);
        draft.Focus();
        Keyboard.Type("qwrtzk ");
        PickSpellItem(draft, "Ignore");
        RightClickFirstWord(draft);
        Thread.Sleep(1000);
        Assert.IsNotNull(App.TryFindWhere(e => AppDriver.SafeName(e) == "Paste"), "The menu didn't open after Ignore.");
        Assert.IsNull(App.TryFindWhere(e => AppDriver.SafeName(e) == "Ignore"), "An ignored word still offered fixes.");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
    }

    // Right-clicks the first word of the box until Windows marks it and the item shows. Then it invokes the item
    // through UI Automation. A synthetic click does not reach a menu that opens from a text box.
    // The menu can close during the call, and that is fine.
    private void PickSpellItem(AutomationElement draft, string name)
    {
        AutomationElement? item = null;
        Wait.Until(() =>
        {
            RightClickFirstWord(draft);
            Thread.Sleep(1000);
            item = App.TryFindWhere(e => AppDriver.SafeName(e) == name && e.Properties.ControlType.ValueOrDefault == FlaUI.Core.Definitions.ControlType.MenuItem);
            if (item is null)
            {
                Keyboard.Press(VirtualKeyShort.ESCAPE);
            }

            return item is not null;
        }, AppDriver.DefaultTimeout, () => $"Right-clicking the word showed no '{name}'.");

        try
        {
            item!.Patterns.Invoke.Pattern.Invoke();
        }
        catch (FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
        }
    }

    // Right-clicks the first word, near the left edge of the box.
    private static void RightClickFirstWord(AutomationElement draft)
    {
        System.Drawing.Rectangle box = draft.BoundingRectangle;
        FlaUI.Core.Input.Mouse.RightClick(new System.Drawing.Point(box.Left + 20, box.Top + (box.Height / 2)));
    }

    /// <summary>
    /// Opening the unread chat of Ann shows her three messages and tells the phone that the chat is read. Her row
    /// stops being bold.
    /// </summary>
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
    /// The newest bubble shows its caption row, which is the time, and no other bubble does. A click on the newest
    /// bubble hides the row, and a second click shows it again.
    /// </summary>
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
    /// The time of an older bubble stays hidden until a click on the text of the bubble. Enter on the focused bubble
    /// hides the time again.
    /// </summary>
    [TestMethod]
    public async Task BubbleClick_ShowsTime_EnterHidesIt()
    {
        await OpenAnnAsync();
        string id = Bubbles()[^2].Id;
        Assert.IsNull(App.TryFind(id + ".Time"), "The time showed before the bubble was clicked.");

        App.Find(id).Click();
        App.Find(id + ".Time");

        AutomationElement[] rows = App.Find("Chat.Messages").FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem));
        rows[^2].Focus();
        Keyboard.Type(VirtualKeyShort.RETURN);
        WaitForGone(id + ".Time");
    }

    /// <summary>
    /// A typed message, with Shift + Enter for a new line, and a press of Enter show the bubble. The phone gets the
    /// text. The echo of the phone turns the same bubble into the sent message, with no second bubble.
    /// </summary>
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

        // The echo gives the bubble the message ID of the phone.
        string echoId = Server.Phone.Chats.Page("c-ann", 1, null).Page[0].MessageID;
        Wait.Until(() => Bubbles().Any(b => b.Id == "Bubble." + echoId), AppDriver.DefaultTimeout,
            () => $"No bubble Bubble.{echoId}; bubbles: [{string.Join(", ", Bubbles().Select(b => b.Id))}].");
        Assert.AreEqual(4, Bubbles().Count, "The echo added a second bubble instead of updating the sent one.");
    }

    /// <summary>
    /// A send that the phone refuses shows "Not sent. Click to try again." A click on it sends the text again.
    /// </summary>
    [TestMethod]
    public async Task Send_PhoneFails_ShowsNotSent_RetrySends()
    {
        await OpenAnnAsync();
        Server.Phone.FailSends = true;

        TypeInDraft("Running late");
        AutomationElement retry = WaitForRetry();

        // The phone accepts the retry.
        Server.Phone.FailSends = false;
        retry.Patterns.Invoke.Pattern.Invoke();

        Wait.Until(() => Server.Phone.Sent.Count == 2, AppDriver.DefaultTimeout, () => $"The phone got {Server.Phone.Sent.Count} sends, not 2.");
        Assert.AreEqual("Running late", FakePhone.SentText(Server.Phone.Sent[^1]), "The retry sent other text.");
        Assert.AreNotEqual(Server.Phone.Sent[0].TmpID, Server.Phone.Sent[^1].TmpID, "The retry reused the failed temporary ID.");
        Wait.Until(() => FindRetry() is null, AppDriver.DefaultTimeout, () => "'Not sent' stayed after the retry went through.");
    }

    /// <summary>
    /// A message that arrives in the open chat appends a bubble.
    /// </summary>
    [TestMethod]
    public async Task LiveIncoming_InOpenChat_AppendsBubble()
    {
        await OpenAnnAsync();

        Server.Phone.PushIncoming("c-ann", "Still on?");

        Wait.Until(() => Bubbles().Count == 4 && Bubbles()[^1].Text == "Still on?", AppDriver.DefaultTimeout,
            () => $"Bubbles: [{string.Join(" | ", Bubbles().Select(b => b.Text))}].");
    }

    /// <summary>
    /// A message that arrives in the open chat animates in once. The bubble then sits still in its final place, and
    /// the chat stays on its newest message.
    /// </summary>
    [TestMethod]
    public async Task LiveIncoming_AnimatesIn_EndsInPlace()
    {
        await OpenAnnAsync();

        Message message = Server.Phone.PushIncoming("c-ann", "Coming in");
        AutomationElement bubble = App.Find("Bubble." + message.MessageID);
        Wait.Until(() => ReadLog().Contains($"Bubble animated in ({message.MessageID})", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => "The arriving bubble didn't animate in.");

        Thread.Sleep(400);
        System.Drawing.Rectangle settled = bubble.BoundingRectangle;
        Thread.Sleep(400);
        Assert.AreEqual(settled, bubble.BoundingRectangle, "The bubble kept moving after its entrance.");
        AssertAtNewest("The entrance");
        Assert.AreEqual(1, AnimatedCount(), "More than the one new bubble animated in.");
    }

    /// <summary>
    /// Opening a chat and loading its older pages show the bubbles without the entrance animation. A sent bubble then
    /// animates in.
    /// </summary>
    [TestMethod]
    public async Task OpenAndOlderPages_DoNotAnimate_SendDoes()
    {
        // The short first page loads its older pages on its own. See ShortFirstPage_LoadsOlderUntilItScrolls.
        Conversation old = FakeChats.NewConversation("c-old", "Old Pal", false, ConversationStatus.Active, false, false, FakeChats.Person("p-old", "Old Pal", "+15550000005"));
        long time        = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() * 1000;
        Message[] texts  = [.. Enumerable.Range(1, 5).Select(i => Line($"Old {i}", MessageStatusType.IncomingComplete))];
        Message[] lines  = [.. Enumerable.Range(1, ChatStore.PageSize).Select(i => Line($"System {i}", (MessageStatusType)200))];
        Server.Phone.Chats.Add(old, [.. texts, .. lines]);

        await OpenMainAsync();
        App.Find("Row.c-old").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Count == 5, AppDriver.DefaultTimeout, () => $"The short chat showed {Bubbles().Count} bubbles, not the 5 older ones.");
        Thread.Sleep(500);
        Assert.AreEqual(0, AnimatedCount(), "Opening the chat or loading its older pages animated bubbles in.");

        TypeInDraft("Hello again");
        Wait.Until(() => AnimatedCount() == 1, AppDriver.DefaultTimeout, () => $"The sent bubble animated in {AnimatedCount()} times, not once.");

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
    /// In Start chat, a typed name filters the contacts. A pick of Ann and a press of Go open her conversation in
    /// place of the Start chat pane.
    /// </summary>
    [TestMethod]
    public async Task StartChat_PickContact_OpensConversation()
    {
        // Start chat takes the place of the open chat.
        await OpenAnnAsync();
        App.Find("Main.StartChat").Patterns.Invoke.Pattern.Invoke();
        App.Find("StartChat.Query").Patterns.Value.Pattern.SetValue("Ann");
        Wait.Until(() => App.TryFind("Chat.Draft") is null && App.TryFind("Main.EmptyState") is null, AppDriver.DefaultTimeout,
            () => "The chat pane or the empty state still showed under Start chat.");

        // Only Ann matches.
        Wait.Until(() => AppDriver.SafeName(App.TryFind("Contact.0")).Contains("Ann Lee", StringComparison.Ordinal) && App.TryFind("Contact.1") is null,
            AppDriver.DefaultTimeout, () => $"Contact.0 was '{AppDriver.SafeName(App.TryFind("Contact.0"))}', Contact.1 was '{AppDriver.SafeName(App.TryFind("Contact.1"))}'.");
        // The real mouse clicks the window that is on top at that place. A window that opens through UI Automation,
        // with no real input, does not get the foreground. It can sit behind another window, and the click misses it.
        // The test brings the window to the front first.
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
        App.Find("Contact.0").Click();

        AutomationElement go = App.Find("StartChat.Go");
        Wait.Until(() => go.IsEnabled, AppDriver.DefaultTimeout, () => "Go stayed disabled after picking Ann.");
        go.Patterns.Invoke.Pattern.Invoke();

        App.WaitForText("Chat.Header", "Ann Lee");
        Wait.Until(() => App.TryFind("StartChat.Query") is null, AppDriver.DefaultTimeout, () => "The Start chat pane stayed open.");
        Wait.Until(() => Bubbles().Count == 3, AppDriver.DefaultTimeout, () => $"Ann's chat showed {Bubbles().Count} bubbles, not 3.");

        // The history loads once, so the bubbles never animate in one by one.
        Thread.Sleep(800);
        Assert.AreEqual(0, AnimatedCount(), "Start chat's conversation animated its history in.");
    }

    /// <summary>
    /// When the user switches back to a chat, the app shows the kept bubbles at once, well under 1 s. The phone takes
    /// 3 s to answer for its page. The log records the time of each step of the open, with milliseconds and counts
    /// only.
    /// </summary>
    [TestMethod]
    public async Task SwitchChats_ShowsKeptBubbles_BeforeThePhoneAnswers()
    {
        await OpenAnnAsync();
        App.Find("Row.c-team").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Team");
        Wait.Until(() => Bubbles().Count > 0, AppDriver.DefaultTimeout, () => "Team's chat showed no bubbles.");

        // The phone is slow to answer, but the kept bubbles of Ann still show at once.
        Server.Phone.MessagesDelay = TimeSpan.FromSeconds(3);
        var clock                  = System.Diagnostics.Stopwatch.StartNew();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => AppDriver.SafeName(App.TryFind("Chat.Header")).Contains("Ann", StringComparison.Ordinal) && Bubbles().Count == 3, TimeSpan.FromSeconds(3),
            () => $"Ann's kept chat showed {Bubbles().Count} bubbles before the phone answered, not 3.");
        Assert.IsLessThan(1000, clock.ElapsedMilliseconds, $"Ann's kept bubbles took {clock.ElapsedMilliseconds} ms to show.");

        Wait.Until(() => ReadLog().Contains("Chat open: first bubbles drawn at", StringComparison.Ordinal), AppDriver.DefaultTimeout, () => "No open timing in the log.");
    }

    /// <summary>
    /// The picks in Start chat are chips. When the user picks Ann, she moves from the list to a chip above it. The chip
    /// is a filled pill that stands out from the pane. Go reads Start Chat. A click on the chip puts her back in the
    /// list.
    /// </summary>
    [TestMethod]
    public async Task StartChat_PickedPersonIsAChip_LeavesTheList_ComesBack()
    {
        await OpenMainAsync();
        App.Find("Main.StartChat").Patterns.Invoke.Pattern.Invoke();
        App.Find("StartChat.Query").Patterns.Value.Pattern.SetValue("Ann");
        Wait.Until(() => AppDriver.SafeName(App.TryFind("Contact.0")).Contains("Ann Lee", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => "Ann never showed in the list.");
        Assert.AreEqual(ChatCopy.StartChatGo, AppDriver.SafeName(App.Find("StartChat.Go")), "Go doesn't read Start Chat.");
        Assert.IsNull(App.TryFind("StartChat.Chip.0"), "A chip showed before anyone was picked.");

        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
        App.Find("Contact.0").Click();

        // Her chip shows with its own fill, and she leaves the list.
        AutomationElement chip = App.Find("StartChat.Chip.0");
        Wait.Until(() => App.TryFind("Contact.0") is null || !AppDriver.SafeName(App.TryFind("Contact.0")).Contains("Ann Lee", StringComparison.Ordinal),
            AppDriver.DefaultTimeout, () => "Ann stayed in the list after being picked.");
        System.Drawing.Rectangle box = chip.BoundingRectangle;
        using (FlaUI.Core.Capturing.CaptureImage capture = FlaUI.Core.Capturing.Capture.Rectangle(new System.Drawing.Rectangle(box.Left - 6, box.Top + (box.Height / 2), 4, 1)))
        using (FlaUI.Core.Capturing.CaptureImage inside = FlaUI.Core.Capturing.Capture.Rectangle(new System.Drawing.Rectangle(box.Left + 6, box.Top + (box.Height / 2), 1, 1)))
        {
            System.Drawing.Color pane = capture.Bitmap.GetPixel(0, 0);
            System.Drawing.Color fill = inside.Bitmap.GetPixel(0, 0);
            Assert.AreNotEqual(pane.ToArgb(), fill.ToArgb(), $"The chip's fill ({fill}) is the pane's ({pane}).");
        }

        chip.Click();
        Wait.Until(() => App.TryFind("StartChat.Chip.0") is null && AppDriver.SafeName(App.TryFind("Contact.0")).Contains("Ann Lee", StringComparison.Ordinal),
            AppDriver.DefaultTimeout, () => "Removing the chip didn't put Ann back in the list.");
    }

    /// <summary>
    /// A first page that does not fill the list loads older pages on its own. There is nothing to scroll up to.
    /// In this test, the first page has only system lines, which show no bubble.
    /// </summary>
    [TestMethod]
    public async Task ShortFirstPage_LoadsOlderUntilItScrolls()
    {
        // Five real messages, then a full page of system lines that are newer than the messages.
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
    /// Moving between messages with the keyboard, Ctrl + j and then Up, scrolls the list smoothly and not in jumps.
    /// </summary>
    [TestMethod]
    public async Task KeyboardSelection_ScrollsSmoothly()
    {
        Conversation chat = FakeChats.NewConversation("c-long", "Long Pal", false, ConversationStatus.Active, false, false, FakeChats.Person("p-long", "Long Pal", "+15550000006"));
        long time         = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() * 1000;
        Message[] texts   = [.. Enumerable.Range(1, ChatStore.PageSize * 3).Select(i => Line($"Line {i}"))];
        Server.Phone.Chats.Add(chat, texts);

        await OpenMainAsync();
        App.Find("Row.c-long").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Shows("m-", NewestLine), AppDriver.DefaultTimeout, () => "The chat never showed its newest line.");
        Thread.Sleep(1500);

        // Ctrl + j and then Up move the selection past the top of the view.
        string scroll = Path.Combine(App.DataDir, "chat-scroll.txt");
        File.Delete(scroll);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_J);
        for (int i = 0; i < 25; i++)
        {
            Keyboard.Type(VirtualKeyShort.UP);
            Thread.Sleep(60);
        }

        Wait.Until(() => File.Exists(scroll) && Read(scroll) == "Smooth", AppDriver.DefaultTimeout, () => "Moving between messages with the keyboard didn't scroll smoothly.");

        static string Read(string path)
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
    /// A scroll up in a long chat loads older pages. The scroll-to-bottom button shows when the list is far up, and it
    /// takes the list back to the newest message.
    /// </summary>
    [TestMethod]
    public async Task ScrollUp_LoadsOlder_ScrollToBottomReturns()
    {
        // The chat has three pages of texts.
        Conversation chat = FakeChats.NewConversation("c-long", "Long Pal", false, ConversationStatus.Active, false, false, FakeChats.Person("p-long", "Long Pal", "+15550000006"));
        long time         = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() * 1000;
        // The heights are mixed, as in a real chat: every third message wraps over several lines.
        Message[] texts   = [.. Enumerable.Range(1, ChatStore.PageSize * 3).Select(i => Line(i % 3 == 0 ? $"Line {i} " + string.Concat(Enumerable.Repeat("wrapping words ", 30)) : $"Line {i}"))];
        Server.Phone.Chats.Add(chat, texts);

        await OpenMainAsync();
        App.Find("Row.c-long").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Shows("m-", NewestLine), AppDriver.DefaultTimeout, () => $"The long chat showed {Bubbles().Count} bubbles, without its newest.");

        // The opened chat settles on the newest message and stays there. With no scroll up, no bottom fade and no
        // button show.
        var scroll = App.Find("Chat.Messages").Patterns.Scroll.Pattern;
        Thread.Sleep(2000);
        AssertAtNewest("Opening the chat");

        // At the top, the older page comes in and the button shows.
        Wait.Until(() =>
        {
            // The value -1 leaves the horizontal position alone. It is the NoScroll value of UI Automation.
            scroll.SetScrollPercent(-1, 0);
            return OldestShown("m-") <= OlderPageLast;
        }, AppDriver.DefaultTimeout, () => $"Scrolling up showed line {OldestShown("m-")} at the oldest; no older page loaded.");
        AutomationElement down = App.Find("Chat.ScrollToBottom");
        Wait.Until(() => down.Properties.IsKeyboardFocusable.ValueOrDefault, AppDriver.DefaultTimeout, () => "Scroll to bottom didn't show far up the chat.");

        // The pointer over the button shows the hand cursor, as over a link.
        Thread.Sleep(300);
        PointerInput.Hover(down.BoundingRectangle);
        Wait.Until(PointerInput.IsHandCursor, AppDriver.DefaultTimeout, () => "The pointer over Scroll to bottom isn't the hand cursor.");

        // The button returns the list to the newest message, and the button hides again.
        down.Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => scroll.VerticalScrollPercent.ValueOrDefault >= 99, AppDriver.DefaultTimeout, () => $"Scroll to bottom left the list at {scroll.VerticalScrollPercent.ValueOrDefault:0}%.");
        Wait.Until(() => !down.Properties.IsKeyboardFocusable.ValueOrDefault, AppDriver.DefaultTimeout, () => "Scroll to bottom stayed at the bottom of the chat.");

        // The reopened window shows the same chat from what the app kept. A scroll up works there too.
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
    /// The user pages up past 200 bubbles and then returns to the newest message. The app drops the older bubbles and
    /// keeps the newest 200. A new scroll up brings the older bubbles back from the phone, page by page.
    /// </summary>
    [TestMethod]
    public async Task BackAtNewest_KeepsNewest200_ScrollUpBringsOlderBack()
    {
        // The chat has ten pages of one-line messages.
        const int Count   = ChatStore.PageSize * 10;
        Conversation chat = FakeChats.NewConversation("c-trim", "Trim Pal", false, ConversationStatus.Active, false, false, FakeChats.Person("p-trim", "Trim Pal", "+15550000009"));
        long time         = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() * 1000;
        Server.Phone.Chats.Add(chat, [.. Enumerable.Range(1, Count).Select(Line)]);

        await OpenMainAsync();
        App.Find("Row.c-trim").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Shows("t-", Count), AppDriver.DefaultTimeout, () => $"The chat showed {Bubbles().Count} bubbles, without its newest.");

        // The test scrolls up until every page is in.
        var scroll = App.Find("Chat.Messages").Patterns.Scroll.Pattern;
        Wait.Until(() =>
        {
            scroll.SetScrollPercent(-1, 0);
            return OldestShown("t-") == 1;
        }, TimeSpan.FromSeconds(60), () => $"Scrolling up stopped at line {OldestShown("t-")}.");

        // At the newest message, the older pages go. The top is then the 200th newest line until the phone answers.
        Server.Phone.OlderPagesDelay = TimeSpan.FromSeconds(3);
        AutomationElement down       = App.Find("Chat.ScrollToBottom");
        Wait.Until(() => down.Properties.IsKeyboardFocusable.ValueOrDefault, AppDriver.DefaultTimeout, () => "Scroll to bottom didn't show at the top of the chat.");
        down.Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => scroll.VerticalScrollPercent.ValueOrDefault >= 99, AppDriver.DefaultTimeout, () => $"Scroll to bottom left the list at {scroll.VerticalScrollPercent.ValueOrDefault:0}%.");
        Thread.Sleep(1000);
        AssertAtNewest("Back at the newest message");

        int keptOldest = Count - 200 + 1;
        Wait.Until(() =>
        {
            scroll.SetScrollPercent(-1, 0);
            return OldestShown("t-") == keptOldest;
        }, AppDriver.DefaultTimeout, () => $"At the top after the trim, the oldest line was {OldestShown("t-")}, not {keptOldest}.");

        // The dropped lines come back from the phone.
        Wait.Until(() =>
        {
            scroll.SetScrollPercent(-1, 0);
            return OldestShown("t-") < keptOldest;
        }, AppDriver.DefaultTimeout, () => $"Scrolling up after the trim stayed at line {OldestShown("t-")}.");

        Message Line(int i)
        {
            var message = new Message
            {
                MessageID      = $"t-{i}",
                ConversationID = "c-trim",
                ParticipantID  = "p-trim",
                Timestamp      = time += 1_000_000,
                MessageStatus  = new MessageStatus { Status = MessageStatusType.IncomingComplete },
            };
            message.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = $"Trim {i}" } });
            return message;
        }
    }

    /// <summary>
    /// A scroll up with the mouse wheel, which is real input and not UI Automation, loads older pages. The
    /// scroll-to-bottom button shows when the list is far up.
    /// </summary>
    [TestMethod]
    public async Task WheelUp_LoadsOlder_ShowsScrollToBottom()
    {
        // The chat has three pages of texts.
        Conversation chat = FakeChats.NewConversation("c-wheel", "Wheel Pal", false, ConversationStatus.Active, false, false, FakeChats.Person("p-wheel", "Wheel Pal", "+15550000007"));
        long time         = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() * 1000;
        // The heights are mixed, as in a real chat: every third message wraps over several lines.
        Message[] texts   = [.. Enumerable.Range(1, ChatStore.PageSize * 3).Select(i => Line(i % 3 == 0 ? $"Line {i} " + string.Concat(Enumerable.Repeat("wrapping words ", 30)) : $"Line {i}"))];
        Server.Phone.Chats.Add(chat, texts);

        await OpenMainAsync();
        App.Find("Row.c-wheel").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Shows("w-", NewestLine), AppDriver.DefaultTimeout, () => $"The wheel chat showed {Bubbles().Count} bubbles, without its newest.");

        // The wheel scrolls up over the bubbles until the older page comes in and the button shows.
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
    /// Switching back and forth between two chats that the user opened before leaves each chat on its newest message.
    /// The bubbles show from what the app kept. The chat does not stop a few pixels short of the newest message under
    /// the bottom fade.
    /// </summary>
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
    /// Clicks between chats, both never opened and opened before, show no flash. The test samples about every 8 ms
    /// from the click. The message box keeps its focused look, because a click on a row once took the focus for a
    /// moment. No frame paints the gray scrolling placeholders of the list over the rows.
    /// </summary>
    [TestMethod]
    public async Task SwitchChats_ByClick_NoFlash()
    {
        AddTwoPageChats("fa", "fb", "fc");

        await OpenMainAsync();
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
        App.Find("Row.c-fa").Click();
        WaitForNewest("fa");
        Wait.Until(() => App.Find("Chat.Draft").Properties.HasKeyboardFocus.ValueOrDefault, AppDriver.DefaultTimeout, () => "Opening fa didn't focus the message box.");
        Thread.Sleep(1000);

        // The order alternates between a chat never opened and a chat opened before.
        foreach (string id in new[] { "fb", "fa", "fc", "fb" })
        {
            AssertNoFlash(id);
        }
    }

    /// <summary>
    /// The message box shows one color at rest, under the pointer and focused. The color is its focused, active fill.
    /// Typing still works after those changes. The owner decided this behavior.
    /// </summary>
    [TestMethod]
    public async Task DraftBox_KeepsOneColor_AtRestHoverAndFocused()
    {
        await OpenAnnAsync();
        AutomationElement draft         = App.Find("Chat.Draft");
        System.Drawing.Rectangle box    = draft.BoundingRectangle;
        System.Drawing.Rectangle header = App.Find("Chat.Header").BoundingRectangle;

        // The spot is empty. It is left of Send, away from the caret and the accent line at the bottom.
        System.Drawing.Rectangle spot = new(box.Right - 60, box.Top + (box.Height / 2), 1, 1);

        // The box is focused and the pointer is away.
        PointerInput.Hover(header);
        System.Drawing.Color focused = SettledPixel(spot);

        // At rest, the focus is on a bubble and the pointer is away.
        AutomationElement[] rows = App.Find("Chat.Messages").FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem));
        rows[^1].Focus();
        Wait.Until(() => !draft.Properties.HasKeyboardFocus.ValueOrDefault, AppDriver.DefaultTimeout, () => "The message box kept the focus.");
        System.Drawing.Color rest = SettledPixel(spot);

        // The pointer is over the box and the box is not focused.
        PointerInput.Hover(box);
        System.Drawing.Color hover = SettledPixel(spot);

        TestContext.WriteLine($"Message box: focused {focused}, at rest {rest}, hover {hover}.");
        Assert.IsTrue(Near(rest, focused), $"At rest the message box was {rest}, not its focused {focused}.");
        Assert.IsTrue(Near(hover, focused), $"Under the pointer the message box was {hover}, not its focused {focused}.");

        draft.Click();
        Keyboard.Type("Hi");
        Wait.Until(() => draft.Patterns.Value.Pattern.Value.ValueOrDefault == "Hi", AppDriver.DefaultTimeout,
            () => "Typing in the message box after the hover and focus changes didn't show the text.");

        static bool Near(System.Drawing.Color a, System.Drawing.Color b) => Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2;
    }

    /// <summary>
    /// While an older page loads and lands, the bubbles on screen stay where they are. They do not blank or jump.
    /// The oldest shown bubble keeps its place on screen within 4 px. This holds from the time the loading ring shows
    /// until 1 s after the page lands.
    /// </summary>
    [TestMethod]
    public async Task OlderPage_KeepsVisibleBubblesInPlace()
    {
        // The older pages of this long chat take 2 s. The baseline settles within 1 s before the page lands.
        Server.Phone.Chats.AddLongChat();
        Server.Phone.OlderPagesDelay = TimeSpan.FromSeconds(2);
        const string Oldest          = "Bubble.c-history-096";
        const string NextPage        = "Bubble.c-history-071";

        await OpenMainAsync();
        App.Find("Row.c-history").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Any(b => b.Text.StartsWith("long 120", StringComparison.Ordinal)), AppDriver.DefaultTimeout,
            () => $"The long chat didn't show 'long 120'; bubbles: [{string.Join(", ", Bubbles().Select(b => b.Id))}].");

        // The test scrolls up until the app asks for the older page.
        // The page lands without a move of the shown bubbles.
        AutomationElement list = App.Find("Chat.Messages");
        var scroll             = list.Patterns.Scroll.Pattern;
        AssertOlderPageKeepsInPlace(list, Oldest, () => scroll.SetScrollPercent(-1, 0));

        // The test scrolls to the older page if it sits above the realized bubbles.
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
    /// The same check with the mouse wheel in a chat like a real one. Most messages are one-liners, some wrap and a
    /// few are very tall. The older pages take as long as the older pages of a real phone. The shown bubbles stay in
    /// place. No second page follows at once after the page that landed while the view still sits at its top.
    /// </summary>
    [TestMethod]
    public async Task OlderPage_WheelInMixedChat_KeepsVisibleBubblesInPlace()
    {
        // The chat has four pages of mixed heights. The older pages take 0.7 s, as in the recording of the owner.
        const int Count   = ChatStore.PageSize * 4;
        Conversation chat = FakeChats.NewConversation("c-mixed", "Mixed Pal", false, ConversationStatus.Active, false, false, FakeChats.Person("p-mixed", "Mixed Pal", "+15550000008"));
        long time         = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() * 1000;
        Server.Phone.Chats.Add(chat, [.. Enumerable.Range(1, Count).Select(Line)]);
        Server.Phone.OlderPagesDelay = TimeSpan.FromMilliseconds(700);

        await OpenMainAsync();
        App.Find("Row.c-mixed").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Any(b => b.Id == $"Bubble.m-{Count:000}"), AppDriver.DefaultTimeout, () => "The mixed chat didn't show its newest bubble.");

        // The wheel scrolls up over the bubbles until the app asks for the older page.
        // The app asks half a screen short of the top. The oldest bubble of the page can then still sit above the
        // view, past the tall m-077. The watched bubble is the topmost shown one.
        AutomationElement list        = App.Find("Chat.Messages");
        System.Drawing.Rectangle over = list.BoundingRectangle;
        FlaUI.Core.Input.Mouse.MoveTo(over.X + (over.Width / 2), over.Y + (over.Height / 2));
        AssertOlderPageKeepsInPlace(list, null, () => FlaUI.Core.Input.Mouse.Scroll(5));

        Message Line(int i)
        {
            bool fromMe  = (i / 2 + i / 7) % 2 == 0;
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
                ParticipantID  = fromMe ? FakeChats.MyParticipantId : "p-mixed",
                Timestamp      = time += 1_000_000,
                MessageStatus  = new MessageStatus { Status = fromMe ? MessageStatusType.OutgoingComplete : MessageStatusType.IncomingComplete },
            };
            message.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = text } });
            return message;
        }
    }

    /// <summary>
    /// The same check in a chat where every third message is a photo, tall and flat by turns. The shown bubbles stay
    /// in place while the older page lands. A wheel scroll up through the pictures moves the list only up and never
    /// back down.
    /// </summary>
    [TestMethod]
    public async Task OlderPage_WheelThroughPhotos_KeepsPlaceAndNeverJumpsBack()
    {
        const int Count = ChatStore.PageSize * 4;
        Server.Phone.Chats.AddPhotoPagesChat("c-pics", Count, MediaTests.Png(200, 600, MediaTests.SplitColors), MediaTests.Png(600, 60, MediaTests.SplitColors));
        Server.Phone.OlderPagesDelay = TimeSpan.FromMilliseconds(700);

        await OpenMainAsync();
        App.Find("Row.c-pics").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Any(b => b.Id == $"Bubble.c-pics-{Count:000}"), AppDriver.DefaultTimeout, () => "The picture chat didn't show its newest bubble.");
        Thread.Sleep(2000);

        AutomationElement list        = App.Find("Chat.Messages");
        System.Drawing.Rectangle over = list.BoundingRectangle;
        FlaUI.Core.Input.Mouse.MoveTo(over.X + (over.Width / 2), over.Y + (over.Height / 2));
        AssertOlderPageKeepsInPlace(list, null, () => FlaUI.Core.Input.Mouse.Scroll(5));

        // The wheel scrolls up through the pictures notch by notch. A bubble on screen only moves down the screen,
        // and never by more than one notch can move it. This includes pages that land above.
        var trace = new List<string>();
        for (int i = 0; i < 60; i++)
        {
            (string? Id, int? Top) top = TopmostWholeBubble(list);
            if (top.Id is null)
            {
                trace.Add("none");
                FlaUI.Core.Input.Mouse.Scroll(1);
                Thread.Sleep(150);
                continue;
            }

            FlaUI.Core.Input.Mouse.Scroll(1);
            Thread.Sleep(150);
            int? after = BubbleTop(list, top.Id);
            trace.Add($"{top.Id[7..]}:{top.Top}>{after?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "off"}");
            AssertNotchKeptPlace(list, top, after, i + 1, trace);
        }

        TestContext.WriteLine($"Notches: {string.Join(" ", trace)}");
    }

    /// <summary>
    /// An older page of short texts that lands above a screen of tall photos keeps the shown photo in place. The list
    /// guesses the heights of the new rows from the tall shown rows, which is far too high. The view used to land a
    /// screen too far down. The owner reported this bug with a recording.
    /// </summary>
    [TestMethod]
    public async Task OlderPage_TextsUnderTallPhotos_KeepsVisibleBubblesInPlace()
    {
        const int Count = ChatStore.PageSize * 3;
        Server.Phone.Chats.AddPhotoPagesChat("c-pics", Count, MediaTests.Png(200, 600, MediaTests.SplitColors), MediaTests.Png(200, 600, MediaTests.SplitColors), i => i > Count - ChatStore.PageSize);
        Server.Phone.OlderPagesDelay = TimeSpan.FromMilliseconds(700);

        await OpenMainAsync();
        App.Find("Row.c-pics").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Any(b => b.Id == $"Bubble.Photo.c-pics-{Count:000}"), AppDriver.DefaultTimeout, () => "The picture chat didn't show its newest bubble.");
        Thread.Sleep(2000);

        AutomationElement list        = App.Find("Chat.Messages");
        System.Drawing.Rectangle over = list.BoundingRectangle;
        FlaUI.Core.Input.Mouse.MoveTo(over.X + (over.Width / 2), over.Y + (over.Height / 2));
        AssertOlderPageKeepsInPlace(list, null, () => FlaUI.Core.Input.Mouse.Scroll(5));
    }

    /// <summary>
    /// A wheel scroll up without a pause through the picture chat makes older pages land during the scroll. No
    /// reading shows the list moved back down, which means a bubble on screen that moves up the screen.
    /// </summary>
    [TestMethod]
    public async Task OlderPages_ContinuousWheelThroughPhotos_NeverJumpsBack()
    {
        const int Count = ChatStore.PageSize * 5;
        Server.Phone.Chats.AddPhotoPagesChat("c-pics", Count, MediaTests.Png(200, 600, MediaTests.SplitColors), MediaTests.Png(600, 60, MediaTests.SplitColors));
        Server.Phone.OlderPagesDelay = TimeSpan.FromMilliseconds(700);

        await OpenMainAsync();
        App.Find("Row.c-pics").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Any(b => b.Id == $"Bubble.c-pics-{Count:000}"), AppDriver.DefaultTimeout, () => "The picture chat didn't show its newest bubble.");
        Thread.Sleep(2000);

        AutomationElement list        = App.Find("Chat.Messages");
        System.Drawing.Rectangle over = list.BoundingRectangle;
        FlaUI.Core.Input.Mouse.MoveTo(over.X + (over.Width / 2), over.Y + (over.Height / 2));

        var trace = new List<string>();
        try
        {
            for (int i = 0; i < 150; i++)
            {
                (string? Id, int? Top) top = TopmostWholeBubble(list);
                FlaUI.Core.Input.Mouse.Scroll(1);
                Thread.Sleep(40);
                if (top.Id is null)
                {
                    trace.Add("none");
                    continue;
                }

                int? after = BubbleTop(list, top.Id);
                trace.Add($"{top.Id[7..]}:{top.Top}>{after?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "off"}");
                AssertNotchKeptPlace(list, top, after, i + 1, trace);
            }
        }
        finally
        {
            TestContext.WriteLine($"Notches: {string.Join(" ", trace)}");
        }
    }

    /// <summary>
    /// Scroll to bottom, pressed while an older page is still loading, ends on the newest message. The list stays
    /// there after the page lands. The anchoring of the older page does not pull the list back up.
    /// </summary>
    [TestMethod]
    public async Task ScrollToNewest_WhileOlderPageLoads_EndsAtNewest()
    {
        // The older pages of this long chat take 2 s.
        Server.Phone.Chats.AddLongChat();
        Server.Phone.OlderPagesDelay = TimeSpan.FromSeconds(2);

        await OpenMainAsync();
        App.Find("Row.c-history").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Any(b => b.Text.StartsWith("long 120", StringComparison.Ordinal)), AppDriver.DefaultTimeout,
            () => $"The long chat didn't show 'long 120'; bubbles: [{string.Join(", ", Bubbles().Select(b => b.Id))}].");

        // The test scrolls up until the app asks for the older page.
        AutomationElement list = App.Find("Chat.Messages");
        var scroll             = list.Patterns.Scroll.Pattern;
        Wait.Until(() =>
        {
            scroll.SetScrollPercent(-1, 0);
            return App.TryFind("Chat.LoadingOlder") is not null;
        }, AppDriver.DefaultTimeout, () => "Scrolling up never showed the loading ring.");

        // The button is used while the page is still loading.
        AutomationElement down = App.Find("Chat.ScrollToBottom");
        Wait.Until(() => down.Properties.IsKeyboardFocusable.ValueOrDefault, AppDriver.DefaultTimeout, () => "Scroll to bottom didn't show at the top of the chat.");
        down.Patterns.Invoke.Pattern.Invoke();

        // The page lands and then the list settles.
        Wait.Until(() => App.TryFind("Chat.LoadingOlder") is null, AppDriver.DefaultTimeout, () => "The older page never landed.");
        Thread.Sleep(1000);
        AssertAtNewest("Scroll to bottom pressed while the older page loaded");

        // The list stays there for 1 s.
        DateTime until = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(100);
            AssertAtNewest("A second after the older page landed, the list");
        }
    }

    /// <summary>
    /// Reopening a chat that the app kept shows its bubbles at once. No ring, big or small, shows while the page of
    /// the phone loads. The owner decided this behavior.
    /// </summary>
    [TestMethod]
    public async Task Reopen_CachedChat_ShowsNoRingWhilePhoneLoads()
    {
        // The test opens Ann and Team once, so the app keeps both.
        await OpenAnnAsync();
        App.Find("Row.c-team").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Any(b => b.Text == "Works for me"), AppDriver.DefaultTimeout, () => "Team never showed its bubbles.");

        // The test returns to Ann while the phone is slow.
        Server.Phone.MessagesDelay = TimeSpan.FromSeconds(3);
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();

        Wait.Until(() => Bubbles().Count == 3, TimeSpan.FromSeconds(2), () => $"Ann's kept bubbles didn't show at once; bubbles: {Bubbles().Count}.");

        // No ring shows while the phone is slow.
        DateTime until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
        {
            Assert.IsNull(App.TryFind("Chat.LoadingOlder"), "The top ring showed over kept bubbles.");
            Assert.IsTrue(App.TryFind("Chat.Loading") is not { IsOffscreen: false }, "The big ring showed over kept bubbles.");
            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// After a restart, a chat that the user opened before shows its bubbles from the encrypted database.
    /// This happens before the phone answers.
    /// </summary>
    [TestMethod]
    public async Task Restart_OpensChatFromDatabase_BeforePhoneAnswers()
    {
        await OpenAnnAsync();

        await App.RestartAsync();

        // The phone is slow, so only the database can show the bubbles in time.
        Server.Phone.MessagesDelay = TimeSpan.FromSeconds(5);
        Tray.LeftClick();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();

        Wait.Until(() => Bubbles().Count == 3, TimeSpan.FromSeconds(2), () => $"Ann's saved chat showed {Bubbles().Count} bubbles within 2 s, not 3.");
    }

    /// <summary>
    /// The text typed in the message box returns after the window closes and opens again. It returns again after the
    /// app restarts.
    /// </summary>
    [TestMethod]
    public async Task Draft_OutlivesWindowAndRestart()
    {
        await OpenAnnAsync();
        Keyboard.Type("see you at 5");
        WaitForDraft("see you at 5");

        CloseMainWindow();
        Tray.LeftClick();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        WaitForDraft("see you at 5");

        // A launch by the user opens the window after the restart.
        await App.RestartAsync();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        WaitForDraft("see you at 5");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Counts the bubbles that have animated in so far. The app logs each entrance by message ID.
    private int AnimatedCount() =>
        ReadLog().Split('\n').Count(line => line.Contains("Bubble animated in (", StringComparison.Ordinal));

    // Returns true when the list holds at least one bubble. A blanked list holds none.
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

    // Adds chats like real ones, named c-{id} and Pal {id}. Each chat has two pages of sent and received one-liners,
    // from {id}-1 up, and some lines have emoji. Each page is about one and a half screens.
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

    // Waits for the newest bubble of a chat, which is the last line of the second page.
    private void WaitForNewest(string id) =>
        Wait.Until(() => App.TryFind($"Bubble.{id}-{ChatStore.PageSize * 2}") is not null, AppDriver.DefaultTimeout, () => $"Chat {id} didn't show its newest bubble.");

    // Clicks the row of a chat with real input and reads the screen about every 8 ms for 800 ms.
    // Each reading holds two values. The first is the brightness of the message box. The second is the difference
    // in brightness between the empty middle of the list and its left padding. The code averages it down the list.
    // The gray placeholders cover the rows but not the padding. The middle column holds no bubble here,
    // because received bubbles sit left and sent bubbles sit right. Before the click, both areas read the same.
    private void AssertNoFlash(string id)
    {
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
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
            double draftLuma             = Luma(bitmap.GetPixel(draft.Left + 120 - strip.Left, draft.Top + (draft.Height / 2) - strip.Top));
            double lighter               = 0;
            int rows                     = 0;
            for (int y = 40; y < view.Height - 40; y += 40, rows++)
            {
                lighter += Luma(bitmap.GetPixel(view.Width / 2, y)) - Luma(bitmap.GetPixel(5, y));
            }

            return (draftLuma, lighter / rows);
        }

        // Before the click, the message box is focused and the middle of the list is as dark as its padding.
        (double draftBefore, double lighterBefore) = Read();
        Assert.IsTrue(Math.Abs(lighterBefore) < 1, $"Before opening {id}, the list's middle read {lighterBefore:0.0} off its padding; the check needs it empty.");

        // The click starts a second thread that samples, so the readings keep their pace. The code keeps a failed
        // reading and throws it here after the thread ends. The failure then fails the test and not the test host.
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

        string trace = string.Join(" ", samples.Select(s => $"{s.Ms:0}:{s.Draft - draftBefore:0}/{s.Lighter:0.0}"));
        TestContext.WriteLine($"{id}: {samples.Count} readings (ms:box/list) {trace}");
        WaitForNewest(id);
        Assert.IsTrue(samples.Count >= 40, $"Only {samples.Count} readings in 800 ms; too slow to catch a one-frame flash.");
        Assert.IsFalse(samples.Any(s => Math.Abs(s.Draft - draftBefore) > 8), $"Opening {id}, the message box lost its focused look for a moment. Readings: {trace}");
        Assert.IsFalse(samples.Any(s => Math.Abs(s.Lighter) >= 1.5), $"Opening {id}, the list's rows flashed gray. Readings: {trace}");
        Thread.Sleep(1000);
    }

    private static double Luma(System.Drawing.Color pixel) => (0.299 * pixel.R) + (0.587 * pixel.G) + (0.114 * pixel.B);

    // Reads one screen pixel after the brush transitions of the control have had time to end.
    private System.Drawing.Color SettledPixel(System.Drawing.Rectangle spot)
    {
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
        Thread.Sleep(500);
        using FlaUI.Core.Capturing.CaptureImage capture = FlaUI.Core.Capturing.Capture.Rectangle(spot);
        return capture.Bitmap.GetPixel(0, 0);
    }

    // Scrolls up until the loading ring shows. Then it checks every 50 ms that the bubble stays in place.
    // The limit is 4 px from its settled place, until the ring has been gone for 1 s. The page lands above.
    // The bubble must not move. The list must not blank. The app must not ask for another page
    // before the view is put back.
    private void AssertOlderPageKeepsInPlace(AutomationElement list, string? watched, Action scrollUp)
    {
        Wait.Until(() =>
        {
            scrollUp();
            return App.TryFind("Chat.LoadingOlder") is not null;
        }, AppDriver.DefaultTimeout, () => "Scrolling up never showed the loading ring.");

        // If the caller names no bubble, the topmost bubble that is wholly on screen when the ring shows is used.
        watched ??= TopmostWholeBubble(list).Id ?? throw new AssertFailedException("No bubble was wholly on screen as the ring showed.");

        // The place of the bubble after the scroll settles.
        int? recorded = null;
        Wait.Until(() =>
        {
            int? y    = BubbleTop(list, watched);
            bool same = y is not null && y == recorded;
            recorded  = y;
            return same;
        }, TimeSpan.FromSeconds(1), () => $"{watched} never settled on screen (last top: {recorded?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "gone"}).");

        // The bubble stays in place every 50 ms until the page lands and for 1 s more.
        var samples         = new List<string>();
        DateTime landedCap  = DateTime.UtcNow + AppDriver.DefaultTimeout;
        DateTime? until     = null;
        while (until is null || DateTime.UtcNow < until)
        {
            int? y   = BubbleTop(list, watched);
            bool any = AnyBubble(list);

            // UI Automation can see the list with no rows for a moment while the page goes in.
            // The screen never stops showing the rows. Frames that were captured every 8 ms around six such
            // readings all showed the bubbles in place. A reading with no bubbles counts when 3 readings 50 ms
            // apart agree. The sample list marks such a sample with "~".
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

            Assert.IsTrue(DateTime.UtcNow < landedCap, $"The older page never landed. Samples: {string.Join(" ", samples)}.");
            Thread.Sleep(50);
        }

        TestContext.WriteLine($"{watched} tops: {string.Join(" ", samples)}");
    }

    // Reads again every 50 ms until 3 readings in a row agree. It waits up to 1 s and returns the last reading.
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

    // One wheel notch up moves the bubble down the screen a little. A bubble near the bottom can leave the screen.
    // The bubble never moves up, because that means the list moved back down. It never moves by hundreds of pixels.
    // A bubble in the upper half never leaves the bottom. That means an older page took the view and pushed the
    // bubbles off.
    private static void AssertNotchKeptPlace(AutomationElement list, (string? Id, int? Top) before, int? after, int notch, List<string> trace)
    {
        System.Drawing.Rectangle view = list.BoundingRectangle;
        if (after is { } y)
        {
            Assert.IsTrue(y >= before.Top - 4, $"Notch {notch} up moved the list back down ({before.Id} from y={before.Top} to y={y}). Trace: {string.Join(" ", trace)}");
            Assert.IsTrue(y - before.Top <= 400, $"Notch {notch} up leaped ({before.Id} from y={before.Top} to y={y}). Trace: {string.Join(" ", trace)}");
            return;
        }

        Assert.IsTrue(before.Top > view.Top + (view.Height / 2), $"Notch {notch} up pushed {before.Id} off the screen from y={before.Top} (the list's top is {view.Top}, its bottom {view.Bottom}). Trace: {string.Join(" ", trace)}");
    }

    // Returns the topmost bubble whose top edge is on screen, with its top, or null when none is. UI Automation cuts
    // the rectangle of a bubble to the list. A bubble that the top of the list cuts reads the top of the list
    // wherever it scrolls.
    private (string? Id, int? Top) TopmostWholeBubble(AutomationElement list)
    {
        int listTop = list.BoundingRectangle.Top;
        return Bubbles().Select(b => (Id: (string?)b.Id, Top: BubbleTop(list, b.Id))).Where(b => b.Top > listTop + 1).OrderBy(b => b.Top).FirstOrDefault();
    }

    // Returns the screen top of a bubble, or null when the bubble is gone, off screen or outside the list.
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

    // In a chat of three pages, NewestLine is the newest line. OlderPageLast is the last line of the page that is
    // older than the newest page.
    private const int NewestLine    = ChatStore.PageSize * 3;
    private const int OlderPageLast = ChatStore.PageSize * 2;

    // Returns whether the bubble of a line, for example "m-12", is in the list. The list makes only the rows in and
    // near the view. The number of bubbles that UI Automation sees depends on their heights, because narrow windows
    // wrap more. The tests look for particular lines.
    private bool Shows(string prefix, int line) => Bubbles().Any(b => b.Id == $"Bubble.{prefix}{line}");

    // Returns the oldest line among the bubbles in the list, or int.MaxValue when the list has none.
    private int OldestShown(string prefix) =>
        Bubbles().Select(b => b.Id.StartsWith("Bubble." + prefix, StringComparison.Ordinal) && int.TryParse(b.Id.AsSpan(7 + prefix.Length), out int line) ? line : int.MaxValue)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

    // Asserts that the message list sits on its newest message. The list must be less than 1 px short of its end,
    // so no bottom fade or arrow shows.
    private void AssertAtNewest(string what)
    {
        AutomationElement list = App.Find("Chat.Messages");
        var scroll             = list.Patterns.Scroll.Pattern;
        double viewSize        = scroll.VerticalViewSize.ValueOrDefault;
        double viewport        = list.BoundingRectangle.Height;
        double scrollable      = viewSize is > 0 and < 100 ? viewport * 100 / viewSize - viewport : 0;
        double shortBy         = (100 - scroll.VerticalScrollPercent.ValueOrDefault) / 100 * scrollable;
        Assert.IsTrue(shortBy < 1, $"{what} left the list {shortBy:0.0} px short of the newest message.");
    }

    // Opens the main window and the chat of Ann, and waits for her three messages.
    private async Task OpenAnnAsync()
    {
        await OpenMainAsync();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Count == 3, AppDriver.DefaultTimeout, () => $"Ann's chat showed {Bubbles().Count} bubbles, not 3.");

        // Opening the chat puts the cursor in the message box. The test waits for that with the window in front.
        // The box then cannot take the focus from a row later.
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
        Wait.Until(() => App.Find("Chat.Draft").Properties.HasKeyboardFocus.ValueOrDefault, AppDriver.DefaultTimeout, () => "Opening Ann's chat didn't focus the message box.");
    }

    // Waits for the message box to hold the text.
    private void WaitForDraft(string text)
    {
        string? last = null;
        Wait.Until(() => (last = App.Find("Chat.Draft").Patterns.Value.Pattern.Value.ValueOrDefault) == text, AppDriver.DefaultTimeout,
            () => $"The message box shows '{last}', not '{text}'.");
    }

    // Types each line in the message box with Shift + Enter between the lines, and then presses Enter to send.
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

    // Returns the bubbles in the open chat from top to bottom, each with its AutomationId and text.
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
            // The list changed during the walk. The caller reads again.
            return [];
        }
    }

    // Returns the "Not sent" button of the failed bubble, or null.
    private AutomationElement? FindRetry() =>
        App.TryFind("Chat.Messages") is { } list ? AppDriver.Search(list, e => AppDriver.SafeName(e) == ChatCopy.NotSentRetry) : null;

    private AutomationElement WaitForRetry()
    {
        AutomationElement? retry = null;
        Wait.Until(() => (retry = FindRetry()) is not null, AppDriver.DefaultTimeout, () => $"'{ChatCopy.NotSentRetry}' never showed.");
        return retry!;
    }
}
