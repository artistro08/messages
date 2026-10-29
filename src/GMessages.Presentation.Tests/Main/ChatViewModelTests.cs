using System.Net;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Events;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GMessages.Presentation.Tests.Main;

[TestClass]
public sealed class ChatViewModelTests
{
    private static Message Msg(string id, string text, string tmp = "", string from = "p-a", MessageStatusType status = MessageStatusType.IncomingDelivered, long ts = 1_761_700_000_000_000)
    {
        var m = new Message { MessageID = id, TmpID = tmp, ConversationID = "a", ParticipantID = from, Timestamp = ts, MessageStatus = new MessageStatus { Status = status } };
        m.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = text } });
        return m;
    }

    private static Message Mine(string id, string text, string tmp, MessageStatusType status) => Msg(id, text, tmp, "me", status);

    private static ChatViewModel NewChat(MainRig rig, Conversation? conv = null, params Message[] page) => NewChat(rig, TimeProvider.System, conv, page);

    private static ChatViewModel NewChat(MainRig rig, TimeProvider time, Conversation? conv = null, params Message[] page)
    {
        rig.Client.Conversations["a"] = conv ?? MainRig.Conv("a", "Ann");
        rig.Client.MessagesPage = new ListMessagesResponse();

        // The Phone Answers Newest First
        rig.Client.MessagesPage.Messages.AddRange(page.Reverse());
        return new ChatViewModel(rig.Store, "a", time, a => a());
    }

    [TestMethod]
    public async Task Load_MarksUnreadAsRead()
    {
        using var rig = new MainRig();
        Conversation conv = MainRig.Conv("a", "Ann");
        conv.Unread          = true;
        conv.LatestMessageID = "m2";
        using ChatViewModel chat = NewChat(rig, conv, Msg("m1", "one"), Msg("m2", "two"));

        // Not Cached Yet: Load Fetches It
        await chat.LoadAsync();

        Assert.AreEqual("Ann", chat.Title);
        Assert.AreEqual("A", chat.Initial);
        Assert.AreEqual(2, chat.Messages.Count);
        Assert.AreEqual("one", chat.Messages[0].Text);
        Assert.AreEqual(1, rig.Client.MarkedRead.Count);
        Assert.AreEqual(("a", "m2"), rig.Client.MarkedRead[0]);
    }

    /// <summary>
    /// R12: a group chat's header stacks its first two other people, the same as its list row.
    /// </summary>
    [TestMethod]
    public async Task Header_Group_StacksTwoPeople()
    {
        using var rig = new MainRig();
        Conversation conv = MainRig.Conv("a", "Team");
        conv.IsGroupChat  = true;
        conv.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "p-cy" }, FullName = "Cy Ray", AvatarHexColor = "#123456" });
        conv.Participants[1].FullName       = "Bo Ng";
        conv.Participants[1].AvatarHexColor = "#1a73e8";
        using ChatViewModel chat = NewChat(rig, conv, Msg("m1", "one"));

        await chat.LoadAsync();

        Assert.AreEqual("B", chat.Initial);
        Assert.AreEqual("p-a", chat.PhotoId);
        Assert.AreEqual(0xFF1A73E8u, chat.AvatarColor);
        Assert.AreEqual("C", chat.SecondInitial);
        Assert.AreEqual(0xFF123456u, chat.SecondColor);
        Assert.AreEqual("p-cy", chat.SecondPhotoId);
    }

    /// <summary>
    /// A too-short list fills with older pages only while they work: a failed older load (phone silent) stops the
    /// pane's fill after one try instead of asking forever, and the next good newest page re-arms it.
    /// </summary>
    [TestMethod]
    public async Task FillOlder_FailedLoadStopsUntilNextGoodLoad()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig, null, Msg("m1", "one"));
        rig.Client.MessagesPage.Cursor = new Cursor { LastItemID = "m1" };
        await chat.LoadAsync();
        Assert.IsTrue(chat.CanFillOlder, "once after the newest page");

        // The Pane's Fill Loop, Bounded Here: It Runs Only While CanFillOlder Holds
        rig.Client.OlderFetchThrows = new PhoneNotRespondingException();
        for (int i = 0; i < 10 && chat.CanFillOlder; i++)
        {
            await chat.LoadOlderAsync();
        }

        Assert.AreEqual(2, rig.Client.FetchCount, "the newest page and one older try, not a loop");
        Assert.IsFalse(chat.CanFillOlder);

        rig.Client.OlderFetchThrows = null;
        await chat.LoadAsync();
        Assert.IsTrue(chat.CanFillOlder);
    }

    /// <summary>
    /// The fill goes on after an older page that added bubbles, and stops after one that added none.
    /// </summary>
    [TestMethod]
    public async Task FillOlder_ContinuesOnlyAfterBubblesWereAdded()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig, null, Msg("m3", "three"));
        rig.Client.MessagesPage.Cursor = new Cursor { LastItemID = "m3" };
        await chat.LoadAsync();

        var with_bubble = new ListMessagesResponse { Cursor = new Cursor { LastItemID = "m2" } };
        with_bubble.Messages.Add(Msg("m2", "two"));
        var only_system = new ListMessagesResponse { Cursor = new Cursor { LastItemID = "m1" } };
        only_system.Messages.Add(Msg("m1", "joined", status: (MessageStatusType)200));
        rig.Client.MessagePages.Enqueue(with_bubble);
        rig.Client.MessagePages.Enqueue(only_system);

        await chat.LoadOlderAsync();
        Assert.IsTrue(chat.CanFillOlder, "a bubble was added");

        await chat.LoadOlderAsync();
        Assert.IsFalse(chat.CanFillOlder, "nothing was added");
    }

    [TestMethod]
    public async Task Load_Read_DoesNotMarkRead()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig, null, Msg("m1", "one"));

        await chat.LoadAsync();

        Assert.AreEqual(0, rig.Client.MarkedRead.Count);
    }

    [TestMethod]
    public async Task Send_AddsSendingBubble_ClearsDraft()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig);
        await chat.LoadAsync();
        chat.Draft = "hello";

        await chat.SendAsync();

        Assert.AreEqual(string.Empty, chat.Draft);
        Assert.AreEqual(1, chat.Messages.Count);
        Assert.AreEqual("hello", chat.Messages[0].Text);
        Assert.IsTrue(chat.Messages[0].IsSending);
        Assert.IsTrue(chat.Messages[0].FromMe);
        Assert.AreEqual(chat.Messages[0].TmpId, rig.Client.Sent[0].TmpID);
    }

    [TestMethod]
    public async Task Send_EchoWithTmpId_ReplacesBubble_NoDuplicate()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig);
        await chat.LoadAsync();
        chat.Draft = "hello";
        await chat.SendAsync();
        string tmp = chat.Messages[0].TmpId;

        await rig.LiveMessage(Mine("m9", "hello", tmp, MessageStatusType.OutgoingSending));

        Assert.AreEqual(1, chat.Messages.Count);
        Assert.AreEqual("m9", chat.Messages[0].Id);
    }

    /// <summary>
    /// Sending adds one bubble and the phone's echo and delivery update that same bubble: the bubbles already shown are
    /// never cleared, removed, moved or replaced, so the list only animates the new one in.
    /// </summary>
    [TestMethod]
    public async Task Send_AddsOneBubble_EchoAndDeliveryChangeItInPlace_NothingRebuilt()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig, null, Msg("m1", "hi"), Msg("m2", "there"));
        await chat.LoadAsync();
        MessageBubbleViewModel[] shown = [.. chat.Messages];
        List<System.Collections.Specialized.NotifyCollectionChangedAction> changes = [];
        chat.Messages.CollectionChanged += (_, e) => changes.Add(e.Action);

        chat.Draft = "hello";
        await chat.SendAsync();
        MessageBubbleViewModel sent = chat.Messages[^1];
        await rig.LiveMessage(Mine("m9", "hello", sent.TmpId, MessageStatusType.OutgoingSending));
        await rig.LiveMessage(Mine("m9", "hello", sent.TmpId, MessageStatusType.OutgoingDelivered));

        Assert.AreEqual("Add", string.Join(",", changes), "Only the sent bubble's Add: no Reset, Remove, Move or Replace.");
        CollectionAssert.AreEqual(shown.Append(sent).ToArray(), chat.Messages.ToArray());
        Assert.AreEqual("m9", sent.Id);
        Assert.IsFalse(sent.IsSending);
    }

    [TestMethod]
    public async Task Send_StatusUpdatesSameId_UpdateInPlace()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig);
        await chat.LoadAsync();
        chat.Draft = "hello";
        await chat.SendAsync();
        string tmp = chat.Messages[0].TmpId;
        await rig.LiveMessage(Mine("m9", "hello", tmp, MessageStatusType.OutgoingSending));

        await rig.LiveMessage(Mine("m9", "hello", tmp, MessageStatusType.OutgoingComplete));

        Assert.AreEqual(1, chat.Messages.Count);
        Assert.IsFalse(chat.Messages[0].IsSending);
        Assert.IsFalse(chat.Messages[0].IsFailed);

        await rig.LiveMessage(Mine("m9", "hello", tmp, MessageStatusType.OutgoingFailedGeneric));

        Assert.AreEqual(1, chat.Messages.Count);
        Assert.IsTrue(chat.Messages[0].IsFailed);
    }

    [TestMethod]
    public async Task Send_NotConnected_BubbleFailed_DraftNotLost()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig);
        await chat.LoadAsync();
        rig.Connected = false;
        chat.Draft    = "hello";

        await chat.SendAsync();

        Assert.AreEqual(1, chat.Messages.Count);
        Assert.IsTrue(chat.Messages[0].IsFailed);
        Assert.IsFalse(chat.Messages[0].IsSending);
        Assert.AreEqual("hello", chat.Messages[0].Text);

        // Retry Resends The Same Text With A New Temporary ID
        string first_tmp = chat.Messages[0].TmpId;
        rig.Connected    = true;
        await chat.RetryAsync(chat.Messages[0]);

        Assert.AreEqual(1, chat.Messages.Count);
        Assert.IsTrue(chat.Messages[0].IsSending);
        Assert.IsFalse(chat.Messages[0].IsFailed);
        Assert.AreNotEqual(first_tmp, chat.Messages[0].TmpId);
        Assert.AreEqual(1, rig.Client.Sent.Count);
        Assert.AreEqual("hello", rig.Client.Sent[0].MessagePayload.MessageInfo[0].MessageContent.Content);
        Assert.AreEqual(chat.Messages[0].TmpId, rig.Client.Sent[0].TmpID);
    }

    /// <summary>
    /// Every failure (network, timeout, server, decode, not only phone problems) fails the bubble with its text, and a
    /// retry that fails again fails it again; nothing escapes the fire-and-forget send.
    /// </summary>
    [TestMethod]
    public async Task Send_AnyFailure_BubbleFailedAndRetryable_TextKept()
    {
        foreach (Exception failure in MainRig.Failures())
        {
            using var rig = new MainRig();
            using ChatViewModel chat = NewChat(rig);
            await chat.LoadAsync();
            rig.Client.SendThrows = failure;
            chat.Draft            = "hello";

            await chat.SendAsync();

            string name = failure.GetType().Name;
            Assert.IsTrue(chat.Messages[0].IsFailed, name);
            Assert.IsFalse(chat.Messages[0].IsSending, name);
            Assert.AreEqual("hello", chat.Messages[0].Text, name);

            await chat.RetryAsync(chat.Messages[0]);
            Assert.IsTrue(chat.Messages[0].IsFailed, name + " retry fails again");

            rig.Client.SendThrows = null;
            await chat.RetryAsync(chat.Messages[0]);
            Assert.IsTrue(chat.Messages[0].IsSending, name + " retry sends");
            Assert.AreEqual(3, rig.Client.Sent.Count, name);
        }
    }

    /// <summary>
    /// A load or older-page load that fails with any error (not only phone problems) leaves the bubbles alone and stops
    /// the short-list fill, so the view never loops.
    /// </summary>
    [TestMethod]
    public async Task Load_AnyFailure_NoThrow_StopsFill()
    {
        foreach (Exception failure in MainRig.Failures())
        {
            using var rig = new MainRig();
            using ChatViewModel chat = NewChat(rig, null, Msg("m1", "one"));
            rig.Client.MessagesPage.Cursor = new Cursor { LastItemID = "m1" };
            await chat.LoadAsync();
            Assert.IsTrue(chat.CanFillOlder);

            string name = failure.GetType().Name;
            rig.Client.OlderFetchThrows = failure;
            await chat.LoadOlderAsync();
            Assert.IsFalse(chat.CanFillOlder, name);
            Assert.IsFalse(chat.IsLoading, name);
            Assert.AreEqual(1, chat.Messages.Count, name);
        }
    }

    [TestMethod]
    public async Task Send_PhoneTimeout_BubbleFailed()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig);
        await chat.LoadAsync();
        rig.Client.SendThrows = new PhoneNotRespondingException();
        chat.Draft = "hello";

        await chat.SendAsync();

        Assert.IsTrue(chat.Messages[0].IsFailed);
    }

    [TestMethod]
    public async Task Send_PhoneRefuses_BubbleFailed()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig);
        await chat.LoadAsync();
        rig.Client.SendAnswer = new SendMessageResponse { Status = SendMessageResponse.Types.Status.Failure2 };
        chat.Draft = "hello";

        await chat.SendAsync();

        Assert.IsTrue(chat.Messages[0].IsFailed);

        // Screen Readers Hear The Text And That It Failed (A List Item Reads Its Item's ToString)
        Assert.AreEqual("hello, " + ChatCopy.NotSentRetry, chat.Messages[0].ToString());
    }

    [TestMethod]
    public void WhitespaceDraft_CannotSend()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig);

        chat.Draft = "   \t";
        Assert.IsFalse(chat.CanSend);

        chat.Draft = " hi ";
        Assert.IsTrue(chat.CanSend);
    }

    [TestMethod]
    public async Task GroupReceived_FirstOfRunShowsSender()
    {
        using var rig = new MainRig();
        Conversation conv = MainRig.Conv("a", "Team");
        conv.IsGroupChat = true;
        conv.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "p-b" }, FullName = "Bo" });
        using ChatViewModel chat = NewChat(rig, conv, Msg("m1", "one", from: "p-a"), Msg("m2", "two", from: "p-a"), Msg("m3", "three", from: "p-b"));

        await chat.LoadAsync();

        Assert.AreEqual("Team", chat.Messages[0].SenderName);
        Assert.IsTrue(chat.Messages[0].HasSenderName);
        Assert.IsNull(chat.Messages[1].SenderName);
        Assert.IsFalse(chat.Messages[1].HasSenderName);
        Assert.AreEqual("Bo", chat.Messages[2].SenderName);
    }

    /// <summary>
    /// A group sender's name takes the color of that person's letter circle; a sender without a color gets the
    /// avatar's fallback gray.
    /// </summary>
    [TestMethod]
    public async Task GroupReceived_SenderNameUsesAvatarColor()
    {
        using var rig = new MainRig();
        Conversation conv = MainRig.Conv("a", "Team");
        conv.IsGroupChat                    = true;
        conv.Participants[1].AvatarHexColor = "#1a73e8";
        conv.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "p-b" }, FullName = "Bo" });
        using ChatViewModel chat = NewChat(rig, conv, Msg("m1", "one", from: "p-a"), Msg("m2", "two", from: "p-b"));

        await chat.LoadAsync();

        Assert.AreEqual(0xFF1A73E8u, chat.Messages[0].SenderColor);
        Assert.AreEqual(0xFF5F6368u, chat.Messages[1].SenderColor);
    }

    // =========================================================================
    // CACHE AND LOADING STATE
    // =========================================================================

    /// <summary>
    /// A conversation opened before shows its cached page at once, with no loading ring, while the phone refreshes it.
    /// </summary>
    [TestMethod]
    public async Task Load_CacheHit_ShowsCachedPageAtOnce()
    {
        using var rig = new MainRig();
        using (ChatViewModel first = NewChat(rig, null, Msg("m1", "one"), Msg("m2", "two")))
        {
            await first.LoadAsync();
        }

        var gate             = new TaskCompletionSource();
        rig.Client.FetchGate = gate;
        using var chat       = new ChatViewModel(rig.Store, "a", TimeProvider.System, a => a());

        Task load = chat.LoadAsync();

        Assert.AreEqual("one,two", string.Join(",", chat.Messages.Select(b => b.Text)));
        Assert.IsFalse(chat.IsLoadingHistory, "a cached page shows no loading ring");
        gate.SetResult();
        await load;
    }

    /// <summary>
    /// The refresh after a cache hit merges into the bubbles already shown: known ones are kept (and updated), new ones
    /// go in, ones the phone no longer has go away.
    /// </summary>
    [TestMethod]
    public async Task Load_CacheHit_RefreshMerges()
    {
        using var rig = new MainRig();
        using (ChatViewModel first = NewChat(rig, null, Msg("m1", "one"), Msg("m2", "two")))
        {
            await first.LoadAsync();
        }

        using var chat = new ChatViewModel(rig.Store, "a", TimeProvider.System, a => a());
        rig.Client.MessagesPage = new ListMessagesResponse();
        rig.Client.MessagesPage.Messages.AddRange([Msg("m3", "three"), Msg("m2", "two edited")]);
        var gate             = new TaskCompletionSource();
        rig.Client.FetchGate = gate;

        Task load = chat.LoadAsync();
        MessageBubbleViewModel cached_two = chat.Messages[1];
        gate.SetResult();
        await load;

        Assert.AreEqual("two edited,three", string.Join(",", chat.Messages.Select(b => b.Text)));
        Assert.AreSame(cached_two, chat.Messages[0], "the shown bubble was updated, not replaced");
    }

    /// <summary>
    /// A message that arrives while the chat is closed updates the cached copy, so reopening shows it.
    /// </summary>
    [TestMethod]
    public async Task ArrivalWhileClosed_ShowsOnReopen()
    {
        using var rig = new MainRig();
        using (ChatViewModel first = NewChat(rig, null, Msg("m1", "one")))
        {
            await first.LoadAsync();
        }

        await rig.LiveMessage(Msg("m2", "two", ts: 1_761_700_000_000_001));
        rig.Client.FetchGate = new TaskCompletionSource();
        using var chat       = new ChatViewModel(rig.Store, "a", TimeProvider.System, a => a());

        _ = chat.LoadAsync();

        Assert.AreEqual("one,two", string.Join(",", chat.Messages.Select(b => b.Text)));
    }

    /// <summary>
    /// With no cached copy the history ring shows until the first page is in.
    /// </summary>
    [TestMethod]
    public async Task Load_NoCache_IsLoadingHistoryUntilPageArrives()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig, null, Msg("m1", "one"));
        var gate                 = new TaskCompletionSource();
        rig.Client.FetchGate     = gate;

        Task load = chat.LoadAsync();
        Assert.IsTrue(chat.IsLoadingHistory);

        gate.SetResult();
        await load;
        Assert.IsFalse(chat.IsLoadingHistory);
    }

    /// <summary>
    /// Over a cached page the small top ring shows (not the big one) until the phone answers.
    /// </summary>
    [TestMethod]
    public async Task Load_WithCachedPage_IsRefreshingUntilPhoneAnswers()
    {
        using var rig = new MainRig();
        using (ChatViewModel first = NewChat(rig, null, Msg("m1", "one")))
        {
            await first.LoadAsync();
        }

        var gate             = new TaskCompletionSource();
        rig.Client.FetchGate = gate;
        using var chat       = new ChatViewModel(rig.Store, "a", TimeProvider.System, a => a());

        Task load = chat.LoadAsync();
        Assert.IsTrue(chat.IsRefreshing);
        Assert.IsTrue(chat.ShowsTopRing);
        Assert.IsFalse(chat.IsLoadingHistory);

        gate.SetResult();
        await load;
        Assert.IsFalse(chat.IsRefreshing);
        Assert.IsFalse(chat.ShowsTopRing);
    }

    /// <summary>
    /// With nothing cached only the big ring shows.
    /// </summary>
    [TestMethod]
    public async Task Load_NoCache_BigRingOnly()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig, null, Msg("m1", "one"));
        var gate                 = new TaskCompletionSource();
        rig.Client.FetchGate     = gate;

        Task load = chat.LoadAsync();
        Assert.IsTrue(chat.IsLoadingHistory);
        Assert.IsFalse(chat.IsRefreshing);
        Assert.IsFalse(chat.ShowsTopRing);

        gate.SetResult();
        await load;
    }

    /// <summary>
    /// A message that arrives while a refresh is in flight keeps its bubble when the (older) page lands.
    /// </summary>
    [TestMethod]
    public async Task ArrivalDuringRefresh_KeepsBubble()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig, null, Msg("m1", "one"));
        await chat.LoadAsync();

        var gate             = new TaskCompletionSource();
        rig.Client.FetchGate = gate;
        Task load            = chat.LoadAsync();
        await rig.LiveMessage(Msg("m2", "two", ts: 1_761_700_000_001_000));
        gate.SetResult();
        await load;

        Assert.AreEqual("one,two", string.Join(",", chat.Messages.Select(b => b.Text)));
    }

    /// <summary>
    /// The small ring shows only while an older page loads.
    /// </summary>
    [TestMethod]
    public async Task LoadOlder_IsLoadingOlderWhileFetching()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig, null, Msg("m3", "three"));
        rig.Client.MessagesPage.Cursor = new Cursor { LastItemID = "m3" };
        await chat.LoadAsync();
        Assert.IsFalse(chat.IsLoadingOlder);

        var gate             = new TaskCompletionSource();
        rig.Client.FetchGate = gate;
        Task older           = chat.LoadOlderAsync();
        Assert.IsTrue(chat.IsLoadingOlder);
        Assert.IsFalse(chat.IsLoadingHistory, "older pages use the small ring only");

        gate.SetResult();
        await older;
        Assert.IsFalse(chat.IsLoadingOlder);
    }

    [TestMethod]
    public async Task Tombstones_AreHidden()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig, null, Msg("m1", "one"), Msg("m2", "left the group", status: (MessageStatusType)200));

        await chat.LoadAsync();

        Assert.AreEqual(1, chat.Messages.Count);

        await rig.LiveMessage(Msg("m3", "joined", status: (MessageStatusType)201));

        Assert.AreEqual(1, chat.Messages.Count);
    }

    [TestMethod]
    public async Task LiveIncoming_WhileActive_MarksRead()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig);
        await chat.LoadAsync();

        await rig.LiveMessage(Msg("m1", "one"));
        Assert.AreEqual(0, rig.Client.MarkedRead.Count);

        chat.IsActive = true;
        await rig.LiveMessage(Msg("m2", "two"));

        Assert.AreEqual(2, chat.Messages.Count);
        Assert.AreEqual(1, rig.Client.MarkedRead.Count);
        Assert.AreEqual(("a", "m2"), rig.Client.MarkedRead[0]);
    }

    [TestMethod]
    public async Task Resync_ReloadsAndKeepsUnsentBubbles()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig, null, Msg("m1", "one"));
        await chat.LoadAsync();
        rig.Connected = false;
        chat.Draft    = "unsent";
        await chat.SendAsync();
        rig.Connected = true;
        rig.Client.MessagesPage.Messages.Insert(0, Msg("m2", "two"));

        await rig.Store.HandleEventAsync(new ListenRecovered());

        Assert.AreEqual(3, chat.Messages.Count);
        Assert.AreEqual("two", chat.Messages[1].Text);
        Assert.AreEqual("unsent", chat.Messages[2].Text);
        Assert.IsTrue(chat.Messages[2].IsFailed);
    }

    [TestMethod]
    public async Task LoadOlder_InsertsAboveInOrder_StopsAtAnEmptyPage()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig, null, Msg("m3", "three"), Msg("m4", "four"));
        rig.Client.MessagesPage.Cursor = new Cursor { LastItemID = "older" };
        await chat.LoadAsync();

        // The Older Page Is Newest First, With No Cursor (As Google Sends It), Then An Empty Page: The End
        var older = new ListMessagesResponse();
        older.Messages.Add(Msg("m2", "two"));
        older.Messages.Add(Msg("m1", "one"));
        rig.Client.MessagePages.Enqueue(older);
        rig.Client.MessagePages.Enqueue(new ListMessagesResponse());
        int fetches = rig.Client.FetchCount;

        await chat.LoadOlderAsync();
        await chat.LoadOlderAsync();
        await chat.LoadOlderAsync();

        Assert.AreEqual("one,two,three,four", string.Join(",", chat.Messages.Select(b => b.Text)));
        Assert.AreEqual(fetches + 2, rig.Client.FetchCount);
    }

    [TestMethod]
    public async Task Resynced_DuringLoadOlder_ReloadsLatestPage()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig, null, Msg("m3", "three"));
        rig.Client.MessagesPage.Cursor = new Cursor { LastItemID = "older" };
        await chat.LoadAsync();

        var gate             = new TaskCompletionSource();
        rig.Client.FetchGate = gate;
        Task older           = chat.LoadOlderAsync();

        rig.Client.MessagesPage.Cursor = null;
        rig.Client.MessagesPage.Messages.Insert(0, Msg("m4", "four"));
        await rig.Store.HandleEventAsync(new ListenRecovered());
        gate.SetResult();
        await older;

        Assert.AreEqual("four", chat.Messages[^1].Text);
    }
    /// <summary>
    /// A bubble's time is hidden until the bubble is clicked, and a second click hides it again.
    /// </summary>
    [TestMethod]
    public void ToggleTime_ShowsThenHides()
    {
        var bubble  = new MessageBubbleViewModel(ChatMessage.From(Msg("m1", "one")));
        int raised  = 0;
        bubble.PropertyChanged += (_, _) => raised++;

        Assert.IsFalse(bubble.IsTimeShown);
        bubble.ToggleTime();
        Assert.IsTrue(bubble.IsTimeShown);
        bubble.ToggleTime();
        Assert.IsFalse(bubble.IsTimeShown);
        Assert.AreEqual(2, raised);
    }

    // =========================================================================
    // SAVED COPY (DATABASE)
    // =========================================================================

    // A Database In Its Own Temp Folder Holding One Saved Message Of Chat "a"; A Short Wait On A Locked File
    private static (MessageDatabase Database, string Path) SavedDatabase()
    {
        string folder = Path.Combine(Path.GetTempPath(), "chat-vm-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "messages.db");
        var database = new MessageDatabase(path, NullLogger.Instance, busyTimeoutSeconds: 5);
        database.SaveMessages("a", [Msg("m0", "saved")]);
        return (database, path);
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (int i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), what);
    }

    /// <summary>
    /// A chat not in memory shows its saved copy from the database before the phone answers, under the small top ring.
    /// </summary>
    [TestMethod]
    public async Task Load_SavedCopy_ShowsBeforePhoneAnswers()
    {
        (MessageDatabase database, string path) = SavedDatabase();
        using var rig            = new MainRig(database);
        using ChatViewModel chat = NewChat(rig, null, Msg("m1", "one"));
        var gate                 = new TaskCompletionSource();
        rig.Client.FetchGate     = gate;

        Task load = chat.LoadAsync();
        await WaitUntil(() => chat.Messages.Count == 1, "the saved copy never showed while the phone was slow");

        Assert.AreEqual("saved", chat.Messages[0].Text);
        Assert.IsFalse(chat.IsLoadingHistory);
        Assert.IsTrue(chat.IsRefreshing);

        gate.SetResult();
        await load;
        Assert.AreEqual("one", string.Join(",", chat.Messages.Select(b => b.Text)));
        await rig.Store.FlushAsync();
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    /// <summary>
    /// A locked database never holds the chat: the big ring shows and the phone is asked at once, its page shows, and
    /// the saved copy that lands later is dropped.
    /// </summary>
    [TestMethod]
    public async Task Load_LockedDatabase_PhoneFetchStartsAtOnce()
    {
        (MessageDatabase database, string path) = SavedDatabase();
        using var rig            = new MainRig(database);
        using ChatViewModel chat = NewChat(rig, null, Msg("m1", "one"));

        // Another Process Holds The File
        using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            holder.Open();
            using (SqliteCommand command = holder.CreateCommand())
            {
                command.CommandText = "BEGIN EXCLUSIVE";
                command.ExecuteNonQuery();
            }

            var gate             = new TaskCompletionSource();
            rig.Client.FetchGate = gate;
            Task load            = chat.LoadAsync();
            Assert.IsTrue(chat.IsLoadingHistory, "no loading ring while the database is locked");
            Assert.AreEqual(1, rig.Client.FetchCount, "the phone wasn't asked while the database is locked");

            gate.SetResult();
            await load.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual("one", string.Join(",", chat.Messages.Select(b => b.Text)));
        }

        // The Saved Copy Lands After The Phone's Page: Dropped
        await rig.Store.FlushAsync();
        await Task.Delay(50);
        Assert.AreEqual("one", string.Join(",", chat.Messages.Select(b => b.Text)));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    // =========================================================================
    // REACT, REPLY, STATUS, TYPING AND ENCRYPTION
    // =========================================================================

    private const string Like  = "\U0001F44D";
    private const string Laugh = "\U0001F602";

    // An RCS Chat Sent As "me", With A SIM For "me", So A Request's SIM Can Be Checked
    private static async Task<ChatViewModel> RcsChat(MainRig rig, TimeProvider time, params Message[] page)
    {
        Conversation conv      = MainRig.Conv("a", "Ann");
        conv.DefaultOutgoingID = "me";
        conv.Type              = ConversationType.Rcs;

        var settings = new GMessages.Protocol.Proto.Settings();
        settings.SIMCards.Add(new SIMCard { SIMData = new SIMData { SIMPayload = new SIMPayload { SIMNumber = 3 } }, SIMParticipant = new SIMParticipant { ID = "me" } });
        await rig.Store.HandleEventAsync(new SettingsChanged(settings));
        await rig.Live(conv);

        ChatViewModel chat = NewChat(rig, time, conv, page);
        await chat.LoadAsync();
        return chat;
    }

    private static Message Reacted(string id, string emoji, string by)
    {
        Message m = Msg(id, "one");
        var entry = new ReactionEntry { Data = GMessages.Protocol.EmojiTypes.MakeReactionData(emoji) };
        entry.ParticipantIDs.Add(by);
        m.Reactions.Add(entry);
        return m;
    }

    [TestMethod]
    public async Task React_None_SendsAdd()
    {
        using var rig            = new MainRig();
        using ChatViewModel chat = await RcsChat(rig, TimeProvider.System, Reacted("m1", Laugh, "p-a"));

        await chat.ReactAsync(chat.Messages[0], Like);

        SendReactionRequest sent = rig.Client.Reactions.Single();
        Assert.AreEqual("m1", sent.MessageID);
        Assert.AreEqual(SendReactionRequest.Types.Action.Add, sent.Action);
        Assert.AreEqual(EmojiType.Like, sent.ReactionData.Type);
        Assert.AreEqual(3, sent.SIMPayload.SIMNumber);
        Assert.IsNull(chat.Messages[0].MyReaction, "not optimistic: the phone's echo changes the bubble");
    }

    [TestMethod]
    public async Task React_Same_SendsRemoveWithoutSim()
    {
        using var rig            = new MainRig();
        using ChatViewModel chat = await RcsChat(rig, TimeProvider.System, Reacted("m1", Like, "me"));
        Assert.AreEqual(Like, chat.Messages[0].MyReaction);

        await chat.ReactAsync(chat.Messages[0], Like);

        SendReactionRequest sent = rig.Client.Reactions.Single();
        Assert.AreEqual(SendReactionRequest.Types.Action.Remove, sent.Action);
        Assert.IsNull(sent.SIMPayload);
    }

    [TestMethod]
    public async Task React_Other_SendsSwitch()
    {
        using var rig            = new MainRig();
        using ChatViewModel chat = await RcsChat(rig, TimeProvider.System, Reacted("m1", Like, "me"));

        await chat.ReactAsync(chat.Messages[0], Laugh);

        SendReactionRequest sent = rig.Client.Reactions.Single();
        Assert.AreEqual(SendReactionRequest.Types.Action.Switch, sent.Action);
        Assert.AreEqual(EmojiType.Laugh, sent.ReactionData.Type);
        Assert.AreEqual(3, sent.SIMPayload.SIMNumber);

        // A Refusal Or Failure Is Logged And Swallowed
        rig.Client.ReactionAnswer = new SendReactionResponse { Success = false };
        await chat.ReactAsync(chat.Messages[0], Laugh);
        rig.Client.ReactionThrows = new PhoneNotRespondingException();
        await chat.ReactAsync(chat.Messages[0], Laugh);
        Assert.AreEqual(3, rig.Client.Reactions.Count);
    }

    [TestMethod]
    public async Task Reply_ToRcs_SendsReplyPayload()
    {
        using var rig            = new MainRig();
        using ChatViewModel chat = await RcsChat(rig, TimeProvider.System, Msg("m1", "one"));

        chat.StartReply(chat.Messages[0]);
        Assert.IsTrue(chat.IsReplying);
        chat.Draft = "answer";
        await chat.SendAsync();

        Assert.AreEqual("m1", rig.Client.Sent.Single().Reply?.MessageID);

        // The Optimistic Bubble Shows The Quote
        MessageBubbleViewModel sent = chat.Messages[^1];
        Assert.IsTrue(sent.HasQuote);
        Assert.AreEqual("one", sent.QuoteText);
        Assert.AreEqual("Ann", sent.QuoteSender);
    }

    [TestMethod]
    public async Task Send_InRcsChat_OptimisticBubbleIsRcs()
    {
        using var rig            = new MainRig();
        using ChatViewModel chat = await RcsChat(rig, TimeProvider.System, Msg("m1", "one"));

        chat.Draft = "hi";
        Task send  = chat.SendAsync();

        // Before The Echo: Already In The RCS Colors, So It Doesn't Flash The SMS Ones
        Assert.IsTrue(chat.Messages[^1].IsRcs);
        await send;
    }

    [TestMethod]
    public async Task Reply_ToSms_SendsPlainText()
    {
        using var rig = new MainRig();
        Message sms   = Msg("m1", "one");
        sms.Type      = 1;
        using ChatViewModel chat = await RcsChat(rig, TimeProvider.System, sms);

        chat.StartReply(chat.Messages[0]);
        chat.Draft = "answer";
        await chat.SendAsync();

        Assert.IsNull(rig.Client.Sent.Single().Reply);
        Assert.IsFalse(chat.Messages[^1].HasQuote);
    }

    [TestMethod]
    public async Task Reply_ClearsAfterSend()
    {
        using var rig            = new MainRig();
        using ChatViewModel chat = await RcsChat(rig, TimeProvider.System, Msg("m1", "one"));
        chat.StartReply(chat.Messages[0]);
        chat.CancelReply();
        Assert.IsFalse(chat.IsReplying);

        chat.StartReply(chat.Messages[0]);
        chat.Draft = "answer";
        await chat.SendAsync();

        Assert.IsFalse(chat.IsReplying);
        Assert.IsNull(chat.ReplyingTo);

        chat.Draft = "again";
        await chat.SendAsync();
        Assert.IsNull(rig.Client.Sent[1].Reply);
    }

    [TestMethod]
    public async Task Quote_FromShownBubble()
    {
        using var rig      = new MainRig();
        Message reply      = Msg("m2", "two");
        reply.ReplyMessage = new ReplyMessage { MessageID = "m1" };
        using ChatViewModel chat = await RcsChat(rig, TimeProvider.System, Msg("m1", "one"), reply);

        Assert.AreEqual("one", chat.Messages[1].QuoteText);
        Assert.AreEqual("Ann", chat.Messages[1].QuoteSender);
    }

    [TestMethod]
    public async Task Quote_Unknown_UsesOriginalMessageCopy()
    {
        using var rig      = new MainRig();
        Message reply      = Msg("m2", "two");
        reply.ReplyMessage = new ReplyMessage { MessageID = "gone" };
        using ChatViewModel chat = await RcsChat(rig, TimeProvider.System, reply);

        Assert.AreEqual(ChatCopy.OriginalMessage, chat.Messages[0].QuoteText);
        Assert.AreEqual(string.Empty, chat.Messages[0].QuoteSender);
    }

    [TestMethod]
    public async Task Status_OnlyNewestSentBubble()
    {
        using var rig            = new MainRig();
        using ChatViewModel chat = await RcsChat(rig, TimeProvider.System, Mine("m1", "one", "t1", MessageStatusType.OutgoingDelivered), Msg("m2", "two"));
        Assert.AreEqual(ChatCopy.Delivered, chat.Messages[0].StatusText);

        // A Newer Send Still Sending: The Older One Keeps Its Status
        chat.Draft = "three";
        await chat.SendAsync();
        MessageBubbleViewModel sent = chat.Messages[^1];
        Assert.AreEqual(ChatCopy.Delivered, chat.Messages[0].StatusText);
        Assert.AreEqual(string.Empty, sent.StatusText);

        // The Phone's Echo: The Newer One Takes The Status, The Older One Loses It
        await rig.LiveMessage(Mine("m3", "three", sent.TmpId, MessageStatusType.OutgoingComplete));
        Assert.AreEqual(string.Empty, chat.Messages[0].StatusText);
        Assert.AreEqual(ChatCopy.Sent, sent.StatusText);

        await rig.LiveMessage(Mine("m3", "three", sent.TmpId, MessageStatusType.OutgoingDisplayed));
        Assert.AreEqual(ChatCopy.Read, sent.StatusText);
    }

    private static TypingChanged Typing(bool started) => new(new TypingData
    {
        ConversationID = "a",
        User           = new User { Number = "+15550100" },
        Type           = started ? TypingTypes.StartedTyping : TypingTypes.StoppedTyping,
    });

    // Ann Types From +15550100
    private static async Task<ChatViewModel> TypingChat(MainRig rig, TimeProvider time)
    {
        ChatViewModel chat             = await RcsChat(rig, time);
        Conversation conv              = rig.Client.Conversations["a"];
        conv.Participants[1].ID.Number = "+15550100";
        await rig.Live(conv);
        return chat;
    }

    [TestMethod]
    public async Task TypingIn_ClearsAfter15Seconds()
    {
        var time                 = new FakeTimeProvider(MainRig.Now);
        using var rig            = new MainRig();
        using ChatViewModel chat = await TypingChat(rig, time);

        await rig.Store.HandleEventAsync(Typing(true));
        Assert.IsTrue(chat.IsTyping);
        time.Advance(TimeSpan.FromSeconds(14));
        Assert.IsTrue(chat.IsTyping);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.IsFalse(chat.IsTyping);

        // A Stop Clears It At Once
        await rig.Store.HandleEventAsync(Typing(true));
        Assert.IsTrue(chat.IsTyping);
        await rig.Store.HandleEventAsync(Typing(false));
        Assert.IsFalse(chat.IsTyping);
    }

    /// <summary>
    /// The timeout's post can run after the chat closed: it does nothing instead of touching the disposed timer.
    /// </summary>
    // A Fake Clock Whose Timers Throw After Dispose, As System.Threading.Timer Does
    private sealed class StrictTimeProvider(DateTimeOffset now) : FakeTimeProvider(now)
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new StrictTimer(base.CreateTimer(callback, state, dueTime, period));

        private sealed class StrictTimer(ITimer inner) : ITimer
        {
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => _disposed ? throw new ObjectDisposedException(nameof(ITimer)) : inner.Change(dueTime, period);

            public void Dispose()
            {
                _disposed = true;
                inner.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    [TestMethod]
    public async Task TypingIn_TimeoutPostedAfterDispose_NoThrow()
    {
        var time                        = new StrictTimeProvider(MainRig.Now);
        using var rig                   = new MainRig();
        using ChatViewModel typing_chat = await TypingChat(rig, time);
        typing_chat.Dispose();

        List<Action> posted = [];
        var chat            = new ChatViewModel(rig.Store, "a", time, posted.Add);
        await rig.Store.HandleEventAsync(Typing(true));
        time.Advance(ChatViewModel.TypingTimeout);
        Assert.AreEqual(1, posted.Count, "the timeout posted");

        chat.Dispose();
        posted[0]();
    }

    /// <summary>
    /// A bubble made before its conversation was cached learns which reaction is mine once the conversation arrives.
    /// </summary>
    [TestMethod]
    public async Task React_ConversationArrivesLater_SameEmojiSendsRemove()
    {
        using var rig = new MainRig();

        // Not Cached Yet (The Phone Doesn't Know It Either): The Bubble Is Made Without It
        using var chat = new ChatViewModel(rig.Store, "a", TimeProvider.System, a => a());
        await rig.LiveMessage(Reacted("m1", Like, "me"));
        Assert.IsNull(chat.Messages[0].MyReaction);

        await rig.Live(MainRig.Conv("a", "Ann"));
        await rig.LiveMessage(Msg("m2", "two"));

        Assert.AreEqual(Like, chat.Messages[0].MyReaction);
        await chat.ReactAsync(chat.Messages[0], Like);
        Assert.AreEqual(SendReactionRequest.Types.Action.Remove, rig.Client.Reactions.Single().Action);
    }

    [TestMethod]
    public async Task TypingIn_ClearsOnTheirMessage()
    {
        var time                 = new FakeTimeProvider(MainRig.Now);
        using var rig            = new MainRig();
        using ChatViewModel chat = await TypingChat(rig, time);

        await rig.Store.HandleEventAsync(Typing(true));
        await rig.LiveMessage(Mine("m5", "mine", "t5", MessageStatusType.OutgoingComplete));
        Assert.IsTrue(chat.IsTyping, "my own message doesn't stop theirs");

        await rig.LiveMessage(Msg("m6", "theirs"));
        Assert.IsFalse(chat.IsTyping);
    }

    [TestMethod]
    public async Task TypingOut_RcsOnly_AtMostEvery10Seconds()
    {
        var time                 = new FakeTimeProvider(MainRig.Now);
        using var rig            = new MainRig();
        using ChatViewModel chat = await RcsChat(rig, time);

        chat.Draft = "h";
        chat.Draft = "he";
        Assert.AreEqual(1, rig.Client.Typing.Count);
        Assert.AreEqual("a", rig.Client.Typing[0].ConversationId);
        Assert.AreEqual(3, rig.Client.Typing[0].Sim?.SIMNumber);

        time.Advance(TimeSpan.FromSeconds(10));
        chat.Draft = string.Empty;
        Assert.AreEqual(1, rig.Client.Typing.Count, "clearing the draft isn't typing");
        chat.Draft = "hey";
        Assert.AreEqual(2, rig.Client.Typing.Count);

        // An SMS Chat Never Says It
        using var sms_rig            = new MainRig();
        using ChatViewModel sms_chat = NewChat(sms_rig, time);
        await sms_chat.LoadAsync();
        sms_chat.Draft = "h";
        Assert.AreEqual(0, sms_rig.Client.Typing.Count);
    }

    [TestMethod]
    public async Task IsEncrypted_FollowsStore()
    {
        using var rig            = new MainRig();
        Message on               = Msg("t1", string.Empty, status: MessageStatusType.TombstoneProtocolSwitchToEncryptedRcs, ts: 1_000_000);
        using ChatViewModel chat = await RcsChat(rig, TimeProvider.System, on);
        Assert.IsTrue(chat.IsEncrypted);

        await rig.LiveMessage(Msg("t2", string.Empty, status: MessageStatusType.TombstoneProtocolSwitchToText, ts: 2_000_000));
        Assert.IsFalse(chat.IsEncrypted);
    }

    // =========================================================================
    // LINK PREVIEWS
    // =========================================================================

    private static FakeHttpHandler PreviewPage() => new()
    {
        Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "text/html", "<title>Page Title</title>"u8.ToArray()),
    };

    [TestMethod]
    public async Task InboxChat_LinkBubble_GetsPreview()
    {
        using var rig       = new MainRig();
        var handler         = PreviewPage();
        using var previews  = new LinkPreviewer(handler, false, NullLogger.Instance);
        rig.Client.Conversations["a"] = MainRig.Conv("a", "Ann");
        rig.Client.MessagesPage       = new ListMessagesResponse();
        rig.Client.MessagesPage.Messages.Add(Msg("m1", "look https://www.example.com/a."));
        using var chat = new ChatViewModel(rig.Store, "a", TimeProvider.System, a => a(), previews);

        await chat.LoadAsync();

        MessageBubbleViewModel bubble = chat.Messages.Single();
        for (int i = 0; i < 100 && !bubble.HasPreview; i++)
        {
            await Task.Delay(20);
        }

        Assert.IsTrue(bubble.HasPreview);
        Assert.AreEqual("Page Title", bubble.Preview!.Title);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual("https://www.example.com/a", handler.Requests[0].Url);
    }

    /// <summary>
    /// Review Focus 5: a spam or blocked chat's links are never fetched.
    /// </summary>
    [TestMethod]
    [DataRow(ConversationStatus.SpamFolder)]
    [DataRow(ConversationStatus.BlockedFolder)]
    [DataRow(ConversationStatus.Archived)]
    public async Task SpamChat_NoPreviewRequest(ConversationStatus status)
    {
        using var rig      = new MainRig();
        var handler        = PreviewPage();
        using var previews = new LinkPreviewer(handler, false, NullLogger.Instance);
        rig.Client.Conversations["a"] = MainRig.Conv("a", "Ann", status: status);
        rig.Client.MessagesPage       = new ListMessagesResponse();
        rig.Client.MessagesPage.Messages.Add(Msg("m1", "look https://www.example.com/a"));
        using var chat = new ChatViewModel(rig.Store, "a", TimeProvider.System, a => a(), previews);

        await chat.LoadAsync();
        await rig.LiveMessage(Msg("m2", "and https://www.example.com/b"));
        await Task.Delay(100);

        Assert.AreEqual(2, chat.Messages.Count);
        Assert.AreEqual(0, handler.Requests.Count);
        Assert.IsFalse(chat.Messages.Any(b => b.HasPreview));
    }
}
