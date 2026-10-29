using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Events;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;

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

    private static ChatViewModel NewChat(MainRig rig, Conversation? conv = null, params Message[] page)
    {
        rig.Client.Conversations["a"] = conv ?? MainRig.Conv("a", "Ann");
        rig.Client.MessagesPage = new ListMessagesResponse();

        // The Phone Answers Newest First
        rig.Client.MessagesPage.Messages.AddRange(page.Reverse());
        return new ChatViewModel(rig.Store, "a", TimeProvider.System, a => a());
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
    public async Task LoadOlder_InsertsAboveInOrder_StopsWhenCursorRunsOut()
    {
        using var rig = new MainRig();
        using ChatViewModel chat = NewChat(rig, null, Msg("m3", "three"), Msg("m4", "four"));
        rig.Client.MessagesPage.Cursor = new Cursor { LastItemID = "older" };
        await chat.LoadAsync();

        // The Older Page Is Newest First, With No Cursor Left
        var older = new ListMessagesResponse();
        older.Messages.Add(Msg("m2", "two"));
        older.Messages.Add(Msg("m1", "one"));
        rig.Client.MessagePages.Enqueue(older);
        int fetches = rig.Client.FetchCount;

        await chat.LoadOlderAsync();
        await chat.LoadOlderAsync();

        Assert.AreEqual("one,two,three,four", string.Join(",", chat.Messages.Select(b => b.Text)));
        Assert.AreEqual(fetches + 1, rig.Client.FetchCount);
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
}
