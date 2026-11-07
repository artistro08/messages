using Google.Protobuf;
using GMessages.Presentation.Connection;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Notifications;
using GMessages.Protocol;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.Proto;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class ChatStoreTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // =========================================================================
    // RIG
    // =========================================================================

    // This is the default message time of Msg. It makes the default messages recent.
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_761_700_000);

    private sealed class Rig : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "chat-test-" + Guid.NewGuid().ToString("N"));

        public Rig(bool connected = true, bool withDatabase = false)
        {
            Controller = new ConnectionController(
                new AppPaths(_folder),
                GmEndpoints.Production,
                TimeProvider.System,
                NullLoggerFactory.Instance,
                a => a());

            DatabasePath = Path.Combine(_folder, "messages.db");
            if (withDatabase)
            {
                Directory.CreateDirectory(_folder);
                Database = new MessageDatabase(DatabasePath, NullLogger.Instance);
            }

            Media = new MediaStore(() => connected ? Client : null, Path.Combine(_folder, "Media"), NullLogger.Instance);
            Store = new ChatStore(Controller, () => connected ? Client : null, NullLogger.Instance, Database, Time, Media);
        }

        public ConnectionController Controller { get; }

        public FakeMessagingClient Client { get; } = new();

        public FakeTimeProvider Time { get; } = new(Now);

        public MessageDatabase? Database { get; }

        public MediaStore Media { get; }

        public string DatabasePath { get; }

        public ChatStore Store { get; }

        public void Dispose()
        {
            Store.Dispose();
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
    }

    private static Conversation Conv(string id, string name = "Ann", string outgoing = "me1")
    {
        var c = new Conversation { ConversationID = id, Name = name, DefaultOutgoingID = outgoing, Status = ConversationStatus.Active };
        c.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "me1" }, IsMe = true, FullName = "Me" });
        return c;
    }

    private static Message Msg(string id, string conversationId, string text = "hi", long ts = 1_761_700_000_000_000)
    {
        var m = new Message { MessageID = id, ConversationID = conversationId, Timestamp = ts, MessageStatus = new MessageStatus { Status = MessageStatusType.OutgoingComplete } };
        m.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = text } });
        return m;
    }

    // =========================================================================
    // LIVE EVENTS
    // =========================================================================

    [TestMethod]
    public async Task LiveConversationUpdated_UpdatesCacheAndRaises()
    {
        using var rig = new Rig();
        var raised    = new List<ConversationInfo>();
        rig.Store.ConversationChanged += (_, c) => raised.Add(c);

        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1", "Ann")));

        Assert.AreEqual("Ann", rig.Store.Find("c1")?.Name);
        Assert.AreEqual(1, raised.Count);
        Assert.AreEqual("c1", raised[0].Id);
        Assert.IsTrue(rig.Store.Me?.IsMe);
    }

    /// <summary>
    /// A message sent here moves its row and shows as the snippet at once. It does not wait for the conversation
    /// update from Google, because that update does not always come.
    /// </summary>
    [TestMethod]
    public async Task LiveSentMessage_BringsItsOwnSnippet()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        var raised = new List<ConversationInfo>();
        rig.Store.ConversationChanged += (_, c) => raised.Add(c);

        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m1", "c1", "on my way"), false));

        ConversationInfo shown = raised.Single();
        Assert.AreEqual("on my way", shown.Snippet);
        Assert.IsTrue(shown.SnippetFromMe);
        Assert.AreEqual("m1", shown.LatestMessageId);
        Assert.AreEqual("on my way", rig.Store.Find("c1")?.Snippet);
    }

    [TestMethod]
    public async Task CatchUpMessage_RaisesWithIsOldTrue()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        MessageArrivedEventArgs? got = null;
        rig.Store.MessageArrived += (_, e) => got = e;

        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m1", "c1"), true));

        Assert.IsNotNull(got);
        Assert.IsTrue(got.IsOld);
        Assert.IsTrue(got.IsNew);
        Assert.AreEqual("c1", got.Conversation?.Id);
    }

    [TestMethod]
    public async Task SameMessageIdTwice_SecondIsNotNew()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        var got = new List<MessageArrivedEventArgs>();
        rig.Store.MessageArrived += (_, e) => got.Add(e);

        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m1", "c1"), false));
        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m1", "c1"), false));

        Assert.AreEqual(2, got.Count);
        Assert.IsTrue(got[0].IsNew);
        Assert.IsFalse(got[1].IsNew);
    }

    /// <summary>
    /// The user reads a chat on the phone. The phone then sends the message again as a status update. The whole-second
    /// MMS time of the message is 1 millisecond later than the time of the conversation. The chat stays read.
    /// </summary>
    [TestMethod]
    public async Task ReadOnPhone_ThenTheMessageAgain_StaysRead()
    {
        using var rig = new Rig();
        Conversation c = Conv("c1");
        c.LastMessageTimestamp = 1_761_699_999_999_000;
        c.Unread               = true;
        await rig.Store.HandleEventAsync(new ConversationUpdated(c));

        Message m = Msg("m1", "c1");
        m.MessageStatus.Status = MessageStatusType.IncomingComplete;
        await rig.Store.HandleEventAsync(new MessageReceived(m, false));

        Conversation read = c.Clone();
        read.Unread       = false;
        await rig.Store.HandleEventAsync(new ConversationUpdated(read));
        await rig.Store.HandleEventAsync(new MessageReceived(m, false));

        Assert.IsFalse(rig.Store.Find("c1")?.Unread);
    }

    /// <summary>
    /// A group MMS first arrives undownloaded in the one-on-one chat of the sender. It then moves, with the same ID,
    /// into the group. The stand-in raises nothing, and the real message still notifies.
    /// </summary>
    [TestMethod]
    public async Task UndownloadedMms_IsSkipped_AndTheMovedMessageStillNotifies()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c2")));
        var got = new List<MessageArrivedEventArgs>();
        rig.Store.MessageArrived += (_, e) => got.Add(e);

        var stub = new Message { MessageID = "m1", ConversationID = "c1", Timestamp = 1_761_700_000_000_000, MessageStatus = new MessageStatus { Status = MessageStatusType.IncomingAutoDownloading } };
        stub.MessageInfo.Add(new MessageInfo { ActionMessageID = "a1" });
        await rig.Store.HandleEventAsync(new MessageReceived(stub, false));
        await rig.Store.HandleEventAsync(new MessageReceived(new Message { MessageID = "m0", ConversationID = "c1", MessageStatus = new MessageStatus { Status = MessageStatusType.MessageDeleted } }, false));

        Message real = Msg("m1", "c2", "Thanks!");
        real.MessageStatus.Status = MessageStatusType.IncomingComplete;
        await rig.Store.HandleEventAsync(new MessageReceived(real, false));

        Assert.AreEqual(1, got.Count);
        Assert.AreEqual("c2", got[0].Message.ConversationId);
        Assert.IsTrue(got[0].IsNew);
    }

    /// <summary>
    /// The phone makes a one-on-one chat for the sender of a group MMS. After the message moves into the group, this
    /// chat holds only its created line. The store raises the chat but keeps it out of the main list, because the phone
    /// shows it nowhere. My first message in the chat shows it at once, with no wait for the update of the phone.
    /// </summary>
    [TestMethod]
    public async Task EmptyOneOnOneChat_HiddenUntilAMessageLandsInIt()
    {
        using var rig = new Rig();
        List<ConversationInfo> raised = [];
        rig.Store.ConversationChanged += (_, c) => raised.Add(c);

        Conversation c         = Conv("c1");
        c.LatestMessageID      = "t1";
        c.LastMessageTimestamp = 1_761_700_000_000_000;
        c.LatestMessage        = new LatestMessage { DisplayContent = "You started this chat", LatestMessageStatus = new LatestMessageStatus { Status = MessageStatusType.TombstoneOneOnOneSmsCreated } };
        await rig.Store.HandleEventAsync(new ConversationUpdated(c));

        Assert.AreEqual(1, raised.Count);
        Assert.IsTrue(raised[0].IsEmpty);
        Assert.IsFalse(raised[0].IsMainView);

        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m1", "c1", "hey", ts: 1_761_700_001_000_000), false));

        Assert.IsTrue(rig.Store.Find("c1")?.IsMainView);
        Assert.IsTrue(raised[^1].IsMainView, "shown as soon as my message lands");
        Assert.AreEqual("hey", raised[^1].Snippet);
        Assert.IsTrue(raised[^1].SnippetFromMe);
    }

    /// <summary>
    /// Someone texts me in such a chat, and the message arrives before the update of the phone. The chat shows at once
    /// as unread. Its snippet is that message, not the created line.
    /// </summary>
    [TestMethod]
    public async Task EmptyOneOnOneChat_ReceivedMessageShowsItWithItsSnippet()
    {
        using var rig = new Rig();
        Conversation c         = Conv("c1");
        c.LatestMessageID      = "t1";
        c.LastMessageTimestamp = 1_761_700_000_000_000;
        c.LatestMessage        = new LatestMessage { DisplayContent = "You started this chat", LatestMessageStatus = new LatestMessageStatus { Status = MessageStatusType.TombstoneOneOnOneSmsCreated } };
        await rig.Store.HandleEventAsync(new ConversationUpdated(c));
        List<ConversationInfo> raised = [];
        rig.Store.ConversationChanged += (_, c) => raised.Add(c);

        Message theirs                = Msg("m1", "c1", "hi there", ts: 1_761_700_001_000_000);
        theirs.ParticipantID          = "p-ann";
        theirs.MessageStatus.Status   = MessageStatusType.IncomingComplete;
        await rig.Store.HandleEventAsync(new MessageReceived(theirs, false));

        ConversationInfo? shown = rig.Store.Find("c1");
        Assert.IsTrue(shown?.IsMainView);
        Assert.IsTrue(shown?.Unread);
        Assert.AreEqual("hi there", shown?.Snippet);
        Assert.IsFalse(shown?.SnippetFromMe);
        Assert.AreEqual("m1", shown?.LatestMessageId);
        Assert.AreEqual(1, raised.Count);
    }

    /// <summary>
    /// A live system line, such as "someone joined" or "chat created", is not from me, but it has nothing to read.
    /// It does not turn the chat unread and does not add to the live unread count.
    /// </summary>
    [TestMethod]
    public async Task LiveSystemLine_DoesNotTurnTheChatUnread()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        Conversation unread = Conv("c2");
        unread.Unread       = true;
        await rig.Store.HandleEventAsync(new ConversationUpdated(unread));

        await rig.Store.HandleEventAsync(new MessageReceived(new Message { MessageID = "t1", ConversationID = "c1", Timestamp = 1_761_700_000_000_000, MessageStatus = new MessageStatus { Status = MessageStatusType.TombstoneParticipantJoined } }, false));
        await rig.Store.HandleEventAsync(new MessageReceived(new Message { MessageID = "t2", ConversationID = "c2", Timestamp = 1_761_700_000_000_000, MessageStatus = new MessageStatus { Status = MessageStatusType.TombstoneParticipantJoined } }, false));

        Assert.IsFalse(rig.Store.Find("c1")?.Unread);
        Assert.AreEqual(0, rig.Store.LiveUnreadTexts("c2").Count);
    }

    /// <summary>
    /// An unpair calls Reset. A newly paired phone can use conversation IDs that the old phone used. The new phone
    /// sees none of the old conversations, owner, SIMs or notified IDs.
    /// </summary>
    [TestMethod]
    public async Task Reset_NewPhoneSeesNothingOfTheOldOne()
    {
        using var rig = new Rig();

        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1", "Old Ann", outgoing: "me1")));
        var settings = new GMessages.Protocol.Proto.Settings();
        settings.SIMCards.Add(Sim("me1", "old"));
        await rig.Store.HandleEventAsync(new SettingsChanged(settings));
        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m1", "c1"), false));
        Assert.AreEqual("me1", rig.Store.Me?.Id);

        rig.Store.Reset();

        Assert.IsNull(rig.Store.Find("c1"));
        Assert.IsNull(rig.Store.Me);

        // The new phone reuses ID c1 and a message ID that the old phone notified.
        var fresh = new Conversation { ConversationID = "c1", Name = "New Bo", DefaultOutgoingID = "me2", Status = ConversationStatus.Active };
        fresh.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "me2" }, IsMe = true, FullName = "Me Too" });
        rig.Client.Conversations["c1"] = fresh;
        MessageArrivedEventArgs? got  = null;
        rig.Store.MessageArrived     += (_, e) => got = e;

        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m1", "c1"), false));

        Assert.AreEqual("New Bo", got?.Conversation?.Name, "fetched from the new phone, not the old cache");
        Assert.IsTrue(got!.IsNew, "the notified ring was cleared");
        Assert.AreEqual("me2", rig.Store.Me?.Id);

        await rig.Store.SendTextAsync("c1", "hi", "tmp-1", null, Ct);
        SendMessageRequest req = rig.Client.Sent.Single();
        Assert.AreEqual("me2", req.MessagePayload.ParticipantID);
        Assert.IsNull(req.SIMPayload, "the old phone's SIMs are gone");
    }

    /// <summary>
    /// A received message can arrive before the conversation update of the phone. It moves a read chat up as unread.
    /// If the store raised the chat as read, the chat would remove the toast that the message just made.
    /// </summary>
    [TestMethod]
    public async Task ReceivedBeforeConversationUpdate_ReadChatTurnsUnread()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        List<ConversationInfo> raised  = [];
        rig.Store.ConversationChanged += (_, c) => raised.Add(c);

        Message received              = Msg("m1", "c1");
        received.ParticipantID        = "p-ann";
        received.MessageStatus.Status = MessageStatusType.IncomingComplete;
        await rig.Store.HandleEventAsync(new MessageReceived(received, false));

        Assert.IsTrue(rig.Store.Find("c1")?.Unread);
        Assert.IsTrue(raised.All(c => c.Unread), "never raised as read");
    }

    [TestMethod]
    public async Task MessageForUnknownConversation_FetchesConversationFirst()
    {
        using var rig = new Rig();
        rig.Client.Conversations["c9"] = Conv("c9", "Fetched Name");
        MessageArrivedEventArgs? got = null;
        rig.Store.MessageArrived += (_, e) => got = e;

        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m1", "c9"), false));

        Assert.AreEqual("Fetched Name", got?.Conversation?.Name);
        Assert.AreEqual("c9", rig.Client.GetConversationCalls.Single());
        Assert.AreEqual("Fetched Name", rig.Store.Find("c9")?.Name);
    }

    [TestMethod]
    public async Task MessageForUnknownConversation_FetchFails_StillRaisesWithNullConversation()
    {
        using var rig = new Rig();
        rig.Client.GetConversationThrows = true;
        MessageArrivedEventArgs? got     = null;
        rig.Store.MessageArrived += (_, e) => got = e;

        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m1", "c9"), false));

        Assert.IsNotNull(got);
        Assert.IsNull(got.Conversation);

        // A missing conversation, which is a null answer, has the same result as a failed fetch.
        rig.Client.GetConversationThrows = false;
        got = null;
        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m2", "c9"), false));

        Assert.IsNotNull(got);
        Assert.IsNull(got.Conversation);
    }

    [TestMethod]
    public async Task ListenRecovered_RaisesResynced()
    {
        using var rig = new Rig();
        int count     = 0;
        rig.Store.Resynced += (_, _) => count++;

        await rig.Store.HandleEventAsync(new ListenRecovered());

        Assert.AreEqual(1, count);
    }

    // =========================================================================
    // MESSAGE CACHE
    // =========================================================================

    private static ListMessagesResponse PageOf(params Message[] oldestFirst)
    {
        var page = new ListMessagesResponse();

        // The phone answers with the newest message first.
        page.Messages.AddRange(oldestFirst.Reverse());
        return page;
    }

    private static string Ids(IReadOnlyList<ChatMessage>? messages) => messages is null ? "(none)" : string.Join(",", messages.Select(m => m.Id.Length > 0 ? m.Id : m.TmpId));

    /// <summary>
    /// The store keeps the newest page of a conversation in memory. It does not keep an older page.
    /// </summary>
    [TestMethod]
    public async Task LoadMessages_NewestPageIsCached_OlderPageIsNot()
    {
        using var rig = new Rig();
        Assert.IsNull(rig.Store.CachedMessages("c1"));

        rig.Client.MessagesPage = PageOf(Msg("m1", "c1"), Msg("m2", "c1"));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);

        rig.Client.MessagesPage = PageOf(Msg("m0", "c1"));
        await rig.Store.LoadMessagesAsync("c1", new Cursor { LastItemID = "m1" }, Ct);

        Assert.AreEqual("m1,m2", Ids(rig.Store.CachedMessages("c1")));
    }

    /// <summary>
    /// An arriving message updates the cached page. A new message goes in by time. A known message is replaced.
    /// The echo of a sent message replaces its pending copy.
    /// </summary>
    [TestMethod]
    public async Task Arrival_UpdatesCachedPage()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1", ts: 1_000), Msg("m2", "c1", ts: 2_000));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);

        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m3", "c1", "three", ts: 3_000), false));
        Assert.AreEqual("m1,m2,m3", Ids(rig.Store.CachedMessages("c1")));

        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m2", "c1", "edited", ts: 2_000), false));
        Assert.AreEqual("edited", rig.Store.CachedMessages("c1")![1].Text);

        await rig.Store.SendTextAsync("c1", "mine", "tmp-9", null, Ct);
        Assert.AreEqual("m1,m2,m3,tmp-9", Ids(rig.Store.CachedMessages("c1")));
        Message echo = Msg("m4", "c1", "mine", ts: 4_000);
        echo.TmpID = "tmp-9";
        await rig.Store.HandleEventAsync(new MessageReceived(echo, false));
        Assert.AreEqual("m1,m2,m3,m4", Ids(rig.Store.CachedMessages("c1")));

        // The store does not cache a message of another conversation, because the user never opened that conversation.
        await rig.Store.HandleEventAsync(new MessageReceived(Msg("x1", "c2"), false));
        Assert.IsNull(rig.Store.CachedMessages("c2"));
    }

    /// <summary>
    /// After a full-size request, the phone sends the same message again with its full media ID. The cached copy and
    /// the raised message have the new media part. The message is not new a second time.
    /// </summary>
    [TestMethod]
    public async Task MediaEdit_ReplacesMediaId()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1", ts: 1_000));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);
        var raised = new List<MessageArrivedEventArgs>();
        rig.Store.MessageArrived += (_, e) => raised.Add(e);

        await rig.Store.HandleEventAsync(new MessageReceived(Photo("m2", new MediaContent { Format = MediaFormats.ImageJpeg, ThumbnailMediaID = "thumb-1" }), false));
        await rig.Store.HandleEventAsync(new MessageReceived(Photo("m2", new MediaContent { Format = MediaFormats.ImageJpeg, ThumbnailMediaID = "thumb-1", MediaID = "full-1" }), false));

        ChatMessage cached = rig.Store.CachedMessages("c1")!.Single(m => m.Id == "m2");
        Assert.AreEqual("full-1", cached.Media?.MediaId);
        Assert.AreEqual("full-1", raised[1].Message.Media?.MediaId);
        Assert.IsTrue(raised[0].IsNew);
        Assert.IsFalse(raised[1].IsNew);

        static Message Photo(string id, MediaContent content)
        {
            var m = new Message { MessageID = id, ConversationID = "c1", Timestamp = 1_761_700_000_000_000, MessageStatus = new MessageStatus { Status = MessageStatusType.IncomingComplete } };
            m.MessageInfo.Add(new MessageInfo { MediaContent = content });
            return m;
        }
    }

    [TestMethod]
    [DataRow(MediaFormats.ImageJpeg, "Image")]
    [DataRow(MediaFormats.ImageGif, "GIF")]
    [DataRow(MediaFormats.VideoMp4, "Video")]
    public async Task ConversationUpdate_AfterMedia_NamesTheMedia(MediaFormats format, string expected)
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1", ts: 1_000));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);
        await rig.Store.HandleEventAsync(new MessageReceived(MediaMsg("m2", format), false));

        // The phone gives an empty snippet for media with no caption.
        Conversation update = Conv("c1");
        update.LatestMessageID = "m2";
        update.LatestMessage   = new LatestMessage { FromMe = 1 };
        await rig.Store.HandleEventAsync(new ConversationUpdated(update));

        Assert.AreEqual(expected, rig.Store.Find("c1")?.SnippetMedia);
        Assert.AreEqual("You: " + expected, ChatFormat.Snippet(rig.Store.Find("c1")!));
    }

    [TestMethod]
    public async Task MediaMessage_AfterItsConversationUpdate_NamesTheMedia()
    {
        using var rig = new Rig();
        Conversation update = Conv("c1");
        update.LatestMessageID = "m2";
        update.LatestMessage   = new LatestMessage();
        await rig.Store.HandleEventAsync(new ConversationUpdated(update));
        var raised = new List<ConversationInfo>();
        rig.Store.ConversationChanged += (_, c) => raised.Add(c);

        // The chat is not open, so no page is in memory. The message itself gives the media name.
        await rig.Store.HandleEventAsync(new MessageReceived(MediaMsg("m2", MediaFormats.ImageJpeg), false));

        Assert.AreEqual("Image", rig.Store.Find("c1")?.SnippetMedia);
        Assert.AreEqual("Image", raised[^1].SnippetMedia);
    }

    /// <summary>
    /// Opening a chat reads its saved page at once. The read never waits behind the queued saves, which can number
    /// hundreds after a catch-up. With a held queue, the saved page still comes back.
    /// </summary>
    [TestMethod]
    public async Task SavedPage_ReadsAheadOfQueuedSaves()
    {
        using var rig = new Rig(withDatabase: true);
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1"), Msg("m2", "c1"));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);
        await rig.Store.FlushAsync();

        // A fresh store uses the same file and has nothing in memory. A slow save holds its queue.
        using var reopened = new ChatStore(rig.Controller, () => rig.Client, NullLogger.Instance, rig.Database, rig.Time, rig.Media);
        using var gate     = new ManualResetEventSlim();
        Task held          = reopened.EnqueueAsync(_ => gate.Wait(TimeSpan.FromSeconds(10)));

        Task<IReadOnlyList<ChatMessage>?> read = reopened.CachedMessagesAsync("c1");
        Task first                             = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(3)));
        gate.Set();
        await held;

        Assert.AreSame(read, first, "The saved page waited behind the queued save.");
        Assert.AreEqual("m1,m2", Ids(await read));
    }

    private static Message MediaMsg(string id, MediaFormats format)
    {
        var m = new Message { MessageID = id, ConversationID = "c1", Timestamp = 1_761_700_000_000_000, MessageStatus = new MessageStatus { Status = MessageStatusType.OutgoingComplete } };
        m.MessageInfo.Add(new MessageInfo { MediaContent = new MediaContent { Format = format, MediaID = "x" } });
        return m;
    }

    /// <summary>
    /// The cache keeps the last 20 opened conversations. Loading a 21st conversation drops the one that the user
    /// used longest ago.
    /// </summary>
    [TestMethod]
    public async Task Cache_DropsTheLeastRecentlyUsed()
    {
        using var rig = new Rig();
        rig.Client.MessagesPage = PageOf(Msg("m1", "any"));
        for (int i = 0; i < ChatStore.CachedChatCount; i++)
        {
            await rig.Store.LoadMessagesAsync("c" + i, null, Ct);
        }

        // Opening c0 again makes c1 the oldest conversation.
        Assert.IsNotNull(rig.Store.CachedMessages("c0"));
        await rig.Store.LoadMessagesAsync("c-new", null, Ct);

        Assert.IsNotNull(rig.Store.CachedMessages("c0"));
        Assert.IsNull(rig.Store.CachedMessages("c1"));
        Assert.IsNotNull(rig.Store.CachedMessages("c-new"));
    }

    /// <summary>
    /// The echo of the phone can arrive before the send call returns. The pending copy that the store caches after
    /// the echo must not replace the delivered copy.
    /// </summary>
    [TestMethod]
    public async Task EchoBeforeSendReturns_KeepsDeliveredCopy()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1", ts: 1_000));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);

        // The echo arrives while the send still waits for its answer.
        Message echo = Msg("m2", "c1", "mine", ts: 2_000);
        echo.TmpID   = "tmp-9";
        rig.Client.SendAnswerGate = new TaskCompletionSource();
        Task send = rig.Store.SendTextAsync("c1", "mine", "tmp-9", null, Ct);
        await rig.Store.HandleEventAsync(new MessageReceived(echo, false));
        rig.Client.SendAnswerGate.SetResult();
        await send;

        IReadOnlyList<ChatMessage> page = rig.Store.CachedMessages("c1")!;
        Assert.AreEqual("m1,m2", Ids(page));
        Assert.AreEqual(MessageStatusType.OutgoingComplete, page[1].Status);
    }

    /// <summary>
    /// A message that arrives during the fetch of the newest page stays cached when the older page arrives.
    /// </summary>
    [TestMethod]
    public async Task ArrivalDuringFetch_StaysCached()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1", ts: 1_000));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);

        var gate             = new TaskCompletionSource();
        rig.Client.FetchGate = gate;
        Task<(IReadOnlyList<ChatMessage> Messages, Cursor? Older)> load = rig.Store.LoadMessagesAsync("c1", null, Ct);
        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m2", "c1", ts: 2_000), false));
        gate.SetResult();
        await load;

        Assert.AreEqual("m1,m2", Ids(rig.Store.CachedMessages("c1")));
    }

    /// <summary>
    /// Reset drops every cached page.
    /// </summary>
    [TestMethod]
    public async Task Reset_ClearsCachedMessages()
    {
        using var rig = new Rig();
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1"));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);

        rig.Store.Reset();

        Assert.IsNull(rig.Store.CachedMessages("c1"));
    }

    /// <summary>
    /// Reset, on sign-out or unpair, clears the decrypted media in memory and in the temp folder. Review Focus 3
    /// requires this.
    /// </summary>
    [TestMethod]
    public async Task Reset_Clears()
    {
        using var rig           = new Rig();
        rig.Client.Media["m-1"] = [1, 2, 3];
        var part                = new MediaPart("m-1", new byte[32], "", [], [], "a.bin", 3, 0, 0, MessageKind.File, "application/octet-stream");
        string path             = await rig.Store.Media!.SaveToTempAsync(part, Ct);

        rig.Store.Reset();

        Assert.IsFalse(File.Exists(path));
        await rig.Store.Media.GetAsync(part, thumbnail: false, Ct);
        Assert.HasCount(2, rig.Client.DownloadCalls);
    }

    // =========================================================================
    // DATABASE
    // =========================================================================

    /// <summary>
    /// Saved conversations fill the cache, so that the list shows at once after a restart. The load raises no event.
    /// </summary>
    [TestMethod]
    public async Task LoadSaved_FillsConversations_NoEvents()
    {
        using var rig = new Rig(withDatabase: true);
        rig.Database!.SaveConversation(Conv("c1", "Ann"));
        int raised = 0;
        rig.Store.ConversationChanged += (_, _) => raised++;

        await rig.Store.LoadSavedAsync();

        Assert.AreEqual("Ann", rig.Store.Find("c1")?.Name);
        Assert.AreEqual("me1", rig.Store.Me?.Id);
        Assert.AreEqual(0, raised);
    }

    /// <summary>
    /// Saved drafts return after a restart.
    /// </summary>
    [TestMethod]
    public async Task LoadSaved_PutsBackDrafts()
    {
        using var rig = new Rig(withDatabase: true);
        rig.Database!.SaveDraft("c1", "see you");

        await rig.Store.LoadSavedAsync();

        Assert.AreEqual("see you", rig.Store.Draft("c1"));
        Assert.AreEqual(string.Empty, rig.Store.Draft("c2"));
    }

    /// <summary>
    /// An unpair or sign-out can happen while the saved conversations wait in the queue.
    /// The rows of the old account never reach memory.
    /// </summary>
    [TestMethod]
    public async Task Reset_WhileLoadSavedQueued_LoadsNothing()
    {
        using var rig = new Rig(withDatabase: true);
        rig.Database!.SaveConversation(Conv("c1", "Old Ann"));

        // Another process holds the file. A save blocks the queue, and the load waits behind the save.
        using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = rig.DatabasePath, Pooling = false }.ToString()))
        {
            holder.Open();
            using (SqliteCommand command = holder.CreateCommand())
            {
                command.CommandText = "BEGIN EXCLUSIVE";
                command.ExecuteNonQuery();
            }

            await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c2", "Bo")));
            Task load = rig.Store.LoadSavedAsync();

            rig.Store.Reset();
            holder.Close();
            await load;
        }

        await rig.Store.FlushAsync();

        Assert.IsNull(rig.Store.Find("c1"));
        Assert.IsNull(rig.Store.Me);
        Assert.AreEqual(0, rig.Store.CachedConversations().Count);
    }

    /// <summary>
    /// A conversation with no page in memory shows the newest messages of the database. These messages then stay in
    /// memory.
    /// </summary>
    [TestMethod]
    public async Task CachedMessages_MemoryMiss_ReadsDatabase()
    {
        using var rig = new Rig(withDatabase: true);
        rig.Database!.SaveMessages("c1", [Msg("m1", "c1", ts: 1_000), Msg("m2", "c1", ts: 2_000)]);
        Assert.IsNull(rig.Store.CachedMessages("c1"));

        Assert.AreEqual("m1,m2", Ids(await rig.Store.CachedMessagesAsync("c1")));
        Assert.AreEqual("m1,m2", Ids(rig.Store.CachedMessages("c1")), "now in memory");
        Assert.IsNull(await rig.Store.CachedMessagesAsync("c2"));
    }

    /// <summary>
    /// Dropping the pages loses nothing. The newest page reads back unchanged from the database.
    /// </summary>
    [TestMethod]
    public async Task DropPages_ThenCachedMessages_ReadsSameFromDatabase()
    {
        using var rig = new Rig(withDatabase: true);
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        rig.Client.MessagesPage = PageOf([.. Enumerable.Range(1, 25).Select(i => Msg($"m{i:00}", "c1", ts: i * 1_000_000L))]);
        await rig.Store.LoadMessagesAsync("c1", null, Ct);
        string before = Ids(rig.Store.CachedMessages("c1"));

        await rig.Store.DropPagesAsync();

        Assert.IsNull(rig.Store.CachedMessages("c1"), "dropped from memory");
        Assert.AreEqual(before, Ids(await rig.Store.CachedMessagesAsync("c1")));
    }

    /// <summary>
    /// A pending send and a failed send exist only in memory, so their page stays after the drop.
    /// </summary>
    [TestMethod]
    public async Task DropPages_KeepsPendingAndFailedSends()
    {
        using var rig = new Rig(withDatabase: true);
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c2")));
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1"));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);
        await rig.Store.SendTextAsync("c1", "sending", "tmp-1", null, Ct);

        rig.Client.MessagesPage = PageOf(Msg("m2", "c2"));
        await rig.Store.LoadMessagesAsync("c2", null, Ct);
        Message failed = Msg("m3", "c2", ts: 1_761_700_001_000_000);
        failed.MessageStatus.Status = MessageStatusType.OutgoingFailedGeneric;
        await rig.Store.HandleEventAsync(new MessageReceived(failed, false));

        await rig.Store.DropPagesAsync();

        Assert.AreEqual("m1,tmp-1", Ids(rig.Store.CachedMessages("c1")));
        Assert.AreEqual("m2,m3", Ids(rig.Store.CachedMessages("c2")));
    }

    /// <summary>
    /// A message in a dropped page is not new when the phone echoes it later. This holds when the message is not in
    /// the notified ring, because a page loaded the message and the ring never saw it.
    /// </summary>
    [TestMethod]
    public async Task DropPages_KeepsNotificationDeDup()
    {
        using var rig = new Rig(withDatabase: true);
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1"));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);
        var got = new List<MessageArrivedEventArgs>();
        rig.Store.MessageArrived += (_, e) => got.Add(e);

        await rig.Store.DropPagesAsync();
        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m1", "c1"), false));
        await rig.Store.FlushAsync();

        Assert.IsFalse(got.Single().IsNew);
    }

    [TestMethod]
    public async Task LiveMessage_IsSaved()
    {
        using var rig = new Rig(withDatabase: true);

        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1", "Ann")));
        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m1", "c1"), false));
        await rig.Store.FlushAsync();

        Assert.AreEqual("m1", string.Join(",", rig.Database!.LoadMessages("c1", 25).Select(m => m.MessageID)));
        Assert.AreEqual("Ann", rig.Database.LoadConversations().Single().Name);
    }

    [TestMethod]
    public async Task OlderPage_IsSaved()
    {
        using var rig = new Rig(withDatabase: true);
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1", ts: 1_000_000), Msg("m2", "c1", ts: 2_000_000));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);

        rig.Client.MessagesPage = PageOf(Msg("m0", "c1", ts: 500_000));
        await rig.Store.LoadMessagesAsync("c1", new Cursor { LastItemID = "m1", LastItemTimestamp = 1_000 }, Ct);
        await rig.Store.FlushAsync();

        Assert.AreEqual("m0,m1,m2", string.Join(",", rig.Database!.LoadMessages("c1", 25).Select(m => m.MessageID)));
    }

    [TestMethod]
    public async Task Reset_DeletesDatabase()
    {
        using var rig = new Rig(withDatabase: true);
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        await rig.Store.FlushAsync();
        Assert.IsTrue(File.Exists(rig.DatabasePath));

        // A chat opens during the unpair. Its read must not create the file again.
        Task<IReadOnlyList<ChatMessage>?> read = rig.Store.CachedMessagesAsync("c1");
        rig.Store.Reset();
        Assert.IsNull(await read);
        await rig.Store.FlushAsync();

        Assert.IsFalse(File.Exists(rig.DatabasePath));
    }

    /// <summary>
    /// A fetch that starts before an unpair or sign-out drops its result. Nothing of the old account reaches memory
    /// or the database, and the store raises no event.
    /// </summary>
    [TestMethod]
    public async Task Reset_DuringFetch_WritesNothing()
    {
        using var rig = new Rig(withDatabase: true);
        int raised = 0;
        rig.Store.ConversationChanged += (_, _) => raised++;
        rig.Store.MessageArrived      += (_, _) => raised++;

        var fetchGate           = new TaskCompletionSource();
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1"));
        rig.Client.FetchGate    = fetchGate;
        Task<(IReadOnlyList<ChatMessage> Messages, Cursor? Older)> messages = rig.Store.LoadMessagesAsync("c1", null, Ct);

        var listGate                 = new TaskCompletionSource();
        rig.Client.ConversationsPage = new ListConversationsResponse();
        rig.Client.ConversationsPage.Conversations.Add(Conv("c2", "Ann"));
        rig.Client.ListGate = listGate;
        Task<(IReadOnlyList<ConversationInfo> Conversations, Cursor? Next)> folder = rig.Store.LoadFolderAsync(ConversationFolder.Inbox, null, Ct);

        // A live message arrives while the store still fetches its conversation.
        var conversationGate           = new TaskCompletionSource();
        rig.Client.Conversations["c3"] = Conv("c3", "Bo");
        rig.Client.GetConversationGate = conversationGate;
        Task live = rig.Store.HandleEventAsync(new MessageReceived(Msg("m3", "c3"), false));

        rig.Store.Reset();
        fetchGate.SetResult();
        listGate.SetResult();
        conversationGate.SetResult();
        await messages;
        await folder;
        await live;
        await rig.Store.FlushAsync();

        Assert.IsNull(rig.Store.CachedMessages("c1"));
        Assert.IsNull(rig.Store.Find("c2"));
        Assert.IsNull(rig.Store.Find("c3"));
        Assert.AreEqual(0, raised);
        Assert.AreEqual(0, rig.Database!.LoadMessages("c1", 25).Count);
        Assert.AreEqual(0, rig.Database.LoadConversations().Count);
        Assert.AreEqual(0, rig.Database.LoadMessages("c3", 25).Count);
    }

    /// <summary>
    /// A status or reaction update is not new when its message is older than 1 day and the store never saw it before,
    /// or the message left the 256-ID ring. Such an update never makes a toast. This is the rule of mautrix-gmessages
    /// for reactions to old messages.
    /// </summary>
    [TestMethod]
    public async Task ReactionOnDayOldMessage_IsNotNew()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        MessageArrivedEventArgs? got = null;
        rig.Store.MessageArrived += (_, e) => got = e;

        long twoDaysAgo = Now.AddDays(-2).ToUnixTimeMilliseconds() * 1000;
        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m-old", "c1", ts: twoDaysAgo), false));

        Assert.IsNotNull(got);
        Assert.IsFalse(got.IsNew);
    }

    /// <summary>
    /// The phone can echo a recent incoming message that has my reaction on it, after a restart, when the notified
    /// ring does not know the message. This echo is not new and makes no toast. A status echo of a message in the
    /// cached page is not new either.
    /// </summary>
    [TestMethod]
    public async Task MyReaction_NoToast()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        var got = new List<MessageArrivedEventArgs>();
        rig.Store.MessageArrived += (_, e) => got.Add(e);

        Message reacted = Incoming("m1");
        reacted.Reactions.Add(new ReactionEntry { Data = new ReactionData { Type = EmojiType.Like }, ParticipantIDs = { "me1" } });
        await rig.Store.HandleEventAsync(new MessageReceived(reacted, false));

        rig.Client.MessagesPage = PageOf(Incoming("m2"));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);
        await rig.Store.HandleEventAsync(new MessageReceived(Incoming("m2"), false));

        await rig.Store.HandleEventAsync(new MessageReceived(Incoming("m3"), false));

        Assert.HasCount(3, got);
        Assert.IsFalse(got[0].IsNew);
        Assert.IsFalse(NotificationPolicy.ShouldNotify(got[0], true, null, false, false));
        Assert.IsFalse(got[1].IsNew);
        Assert.IsTrue(NotificationPolicy.ShouldNotify(got[2], true, null, false, false));

        static Message Incoming(string id)
        {
            Message m              = Msg(id, "c1");
            m.ParticipantID        = "p-ann";
            m.MessageStatus.Status = MessageStatusType.IncomingComplete;
            return m;
        }
    }

    /// <summary>
    /// A first-seen message with only the reaction of another person still makes a toast. Only my own reaction marks
    /// the message as an echo.
    /// </summary>
    [TestMethod]
    public async Task FirstSeen_WithOthersReaction_Toasts()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        MessageArrivedEventArgs? got = null;
        rig.Store.MessageArrived += (_, e) => got = e;

        Message m              = Msg("m1", "c1");
        m.ParticipantID        = "p-ann";
        m.MessageStatus.Status = MessageStatusType.IncomingComplete;
        m.Reactions.Add(new ReactionEntry { Data = new ReactionData { Type = EmojiType.Like }, ParticipantIDs = { "p-ann" } });
        await rig.Store.HandleEventAsync(new MessageReceived(m, false));

        Assert.IsNotNull(got);
        Assert.IsTrue(got.IsNew);
    }

    /// <summary>
    /// After a restart, the seen ring is empty. A phone echo of a message with my reaction is not new.
    /// </summary>
    [TestMethod]
    public async Task Echo_OfMyReaction_AfterRestart_NoToast()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        MessageArrivedEventArgs? got = null;
        rig.Store.MessageArrived += (_, e) => got = e;

        Message m              = Msg("m1", "c1");
        m.ParticipantID        = "p-ann";
        m.MessageStatus.Status = MessageStatusType.IncomingComplete;
        m.Reactions.Add(new ReactionEntry { Data = new ReactionData { Type = EmojiType.Like }, ParticipantIDs = { "p-ann", "me1" } });
        await rig.Store.HandleEventAsync(new MessageReceived(m, false));

        Assert.IsNotNull(got);
        Assert.IsFalse(got.IsNew);
    }

    /// <summary>
    /// On a phone with 2 SIMs, each chat has its own participant ID for me. The echo of my reaction in a chat on the
    /// second SIM is not new.
    /// </summary>
    [TestMethod]
    public async Task Echo_OfMyReaction_OnTheSecondSim_NoToast()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        Conversation second                     = Conv("c2", outgoing: "me2");
        second.Participants[0].ID.ParticipantID = "me2";
        await rig.Store.HandleEventAsync(new ConversationUpdated(second));
        MessageArrivedEventArgs? got = null;
        rig.Store.MessageArrived += (_, e) => got = e;

        Message m              = Msg("m1", "c2");
        m.ParticipantID        = "p-ann";
        m.MessageStatus.Status = MessageStatusType.IncomingComplete;
        m.Reactions.Add(new ReactionEntry { Data = new ReactionData { Type = EmojiType.Like }, ParticipantIDs = { "me2" } });
        await rig.Store.HandleEventAsync(new MessageReceived(m, false));

        Assert.IsNotNull(got);
        Assert.IsFalse(got.IsNew);
    }

    // =========================================================================
    // SENDING
    // =========================================================================

    private static SIMCard Sim(string participantId, string payload)
    {
        var data = new SIMData { SIMPayload = new SIMPayload { Two = 7, SIMNumber = payload.Length } };
        return new SIMCard { SIMData = data, SIMParticipant = new SIMParticipant { ID = participantId } };
    }

    [TestMethod]
    public async Task SendText_BuildsRequestLikeTheBridge()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1", outgoing: "sim-b")));
        var settings = new GMessages.Protocol.Proto.Settings();
        settings.SIMCards.Add(Sim("sim-a", "a"));
        settings.SIMCards.Add(Sim("sim-b", "bb"));
        await rig.Store.HandleEventAsync(new SettingsChanged(settings));

        ChatMessage sent = await rig.Store.SendTextAsync("c1", "hello", "tmp-1", null, Ct);

        SendMessageRequest req = rig.Client.Sent.Single();
        Assert.AreEqual("c1", req.ConversationID);
        Assert.AreEqual("tmp-1", req.TmpID);
        Assert.AreEqual("tmp-1", req.MessagePayload.TmpID);
        Assert.AreEqual("tmp-1", req.MessagePayload.TmpID2);
        Assert.AreEqual("c1", req.MessagePayload.ConversationID);
        Assert.AreEqual("sim-b", req.MessagePayload.ParticipantID);
        Assert.AreEqual(2, req.SIMPayload.SIMNumber);
        Assert.IsFalse(req.ForceRCS);
        Assert.AreEqual(1, req.MessagePayload.MessageInfo.Count);
        Assert.AreEqual("hello", req.MessagePayload.MessageInfo[0].MessageContent.Content);
        Assert.AreEqual("tmp-1", sent.TmpId);
        Assert.AreEqual("hello", sent.Text);
        Assert.IsTrue(sent.FromMe);
    }

    [TestMethod]
    public async Task SendText_FailureStatus_Throws()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        rig.Client.SendAnswer = new SendMessageResponse { Status = SendMessageResponse.Types.Status.Failure2 };

        await Assert.ThrowsExactlyAsync<MessageSendException>(() => rig.Store.SendTextAsync("c1", "x", "t", null, Ct));
    }

    [TestMethod]
    public async Task SendText_Reply_SetsReplyPayload()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));

        ChatMessage sent = await rig.Store.SendTextAsync("c1", "x", "t", "m1", Ct);
        await rig.Store.SendTextAsync("c1", "y", "u", null, Ct);

        Assert.AreEqual("m1", rig.Client.Sent[0].Reply?.MessageID);
        Assert.AreEqual("m1", sent.ReplyToId);
        Assert.IsNull(rig.Client.Sent[1].Reply);
    }

    /// <summary>
    /// An attachment uploads, then goes as the first MessageInfo. For SMS and MMS, the caption is the second
    /// MessageInfo.
    /// </summary>
    [TestMethod]
    public async Task SendMedia_BuildsMediaMessageInfo()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        double reported = -1;

        ChatMessage sent = await SendMediaAsync(rig, [1, 2, 3], "cat.png", "image/png", "look", new InlineProgress(p => reported = p));

        SendMessageRequest req = rig.Client.Sent.Single();
        Assert.AreEqual("tmp-1", req.TmpID);
        Assert.AreEqual("me1", req.MessagePayload.ParticipantID);
        Assert.AreEqual(2, req.MessagePayload.MessageInfo.Count);
        Assert.AreEqual("media-1", req.MessagePayload.MessageInfo[0].MediaContent.MediaID);
        Assert.AreEqual("cat.png", req.MessagePayload.MessageInfo[0].MediaContent.MediaName);
        Assert.AreEqual("look", req.MessagePayload.MessageInfo[1].MessageContent.Content);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, rig.Client.Media["media-1"]);
        Assert.AreEqual(1, reported);
        Assert.AreEqual(MessageKind.Photo, sent.Kind);
        Assert.AreEqual("media-1", sent.Media?.MediaId);
        Assert.AreEqual("look", sent.Text);
    }

    /// <summary>
    /// RCS refuses captions on media. The media goes alone, and the caption follows as its own text message.
    /// </summary>
    [TestMethod]
    public async Task SendMedia_RcsCaption_SeparateText()
    {
        using var rig = new Rig();
        Conversation conv = Conv("c1");
        conv.Type         = ConversationType.Rcs;
        await rig.Store.HandleEventAsync(new ConversationUpdated(conv));

        ChatMessage sent = await SendMediaAsync(rig, [1], "a.pdf", "application/pdf", "read this");

        Assert.HasCount(2, rig.Client.Sent);
        Assert.AreEqual(1, rig.Client.Sent[0].MessagePayload.MessageInfo.Count);
        Assert.AreEqual("media-1", rig.Client.Sent[0].MessagePayload.MessageInfo[0].MediaContent.MediaID);
        Assert.AreEqual("read this", rig.Client.Sent[1].MessagePayload.MessageInfo.Single().MessageContent.Content);
        Assert.AreNotEqual("tmp-1", rig.Client.Sent[1].TmpID);
        Assert.AreEqual(string.Empty, sent.Text);
    }

    /// <summary>
    /// When only the RCS caption message fails, the media still counts as sent and nothing throws. A retry then
    /// never sends the media twice.
    /// </summary>
    [TestMethod]
    public async Task SendMedia_RcsCaptionFails_MediaStillSent()
    {
        using var rig = new Rig();
        Conversation conv = Conv("c1");
        conv.Type         = ConversationType.Rcs;
        await rig.Store.HandleEventAsync(new ConversationUpdated(conv));
        rig.Client.SendFails = r => r.MessagePayload.MessageInfo.Any(i => i.MessageContent is not null);

        ChatMessage sent = await SendMediaAsync(rig, [1], "a.pdf", "application/pdf", "read this");

        Assert.HasCount(2, rig.Client.Sent);
        Assert.AreEqual("tmp-1", sent.TmpId);
    }

    /// <summary>
    /// The store refuses a file over 100 MiB before it uploads or sends anything.
    /// </summary>
    [TestMethod]
    public async Task SendMedia_TooBig_Refused()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        byte[] big = new byte[ChatStore.MaxAttachmentBytes + 1];

        await Assert.ThrowsExactlyAsync<AttachmentTooBigException>(() => SendMediaAsync(rig, big, "big.bin", "application/octet-stream", null));

        Assert.IsEmpty(rig.Client.Media);
        Assert.IsEmpty(rig.Client.Sent);
    }

    // This method uploads the file, then sends it in chat c1 with the temporary ID tmp-1, as the chat view does.
    private static async Task<ChatMessage> SendMediaAsync(Rig rig, byte[] bytes, string fileName, string mime, string? caption, IProgress<double>? progress = null)
    {
        MediaContent media = await rig.Store.UploadAttachmentAsync(bytes, fileName, mime, progress, Ct);
        return await rig.Store.SendUploadedMediaAsync("c1", media, caption, "tmp-1", null, Ct);
    }

    // This progress reports at the time of the call. Progress<T> posts the report to a context instead.
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [TestMethod]
    public async Task SendReaction_UsesSimExceptForRemove_RefusalThrows()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1", outgoing: "sim-b")));
        var settings = new GMessages.Protocol.Proto.Settings();
        settings.SIMCards.Add(Sim("sim-b", "bb"));
        await rig.Store.HandleEventAsync(new SettingsChanged(settings));

        await rig.Store.SendReactionAsync("c1", "m1", "\U0001F44D", SendReactionRequest.Types.Action.Add, Ct);
        await rig.Store.SendReactionAsync("c1", "m1", "\U0001F44D", SendReactionRequest.Types.Action.Remove, Ct);

        Assert.AreEqual("m1", rig.Client.Reactions[0].MessageID);
        Assert.AreEqual(EmojiType.Like, rig.Client.Reactions[0].ReactionData.Type);
        Assert.AreEqual(2, rig.Client.Reactions[0].SIMPayload.SIMNumber);
        Assert.IsNull(rig.Client.Reactions[1].SIMPayload);

        rig.Client.ReactionAnswer = new SendReactionResponse { Success = false };
        MessageSendException refused = await Assert.ThrowsExactlyAsync<MessageSendException>(() => rig.Store.SendReactionAsync("c1", "m1", "\U0001F44D", SendReactionRequest.Types.Action.Add, Ct));
        Assert.AreEqual("ReactionRefused", refused.Status);
    }

    [TestMethod]
    public async Task SendTyping_UsesTheConversationSim()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1", outgoing: "sim-b")));
        var settings = new GMessages.Protocol.Proto.Settings();
        settings.SIMCards.Add(Sim("sim-a", "a"));
        settings.SIMCards.Add(Sim("sim-b", "bb"));
        await rig.Store.HandleEventAsync(new SettingsChanged(settings));

        await rig.Store.SendTypingAsync("c1", Ct);

        Assert.AreEqual("c1", rig.Client.Typing.Single().ConversationId);
        Assert.AreEqual(2, rig.Client.Typing.Single().Sim?.SIMNumber);
    }

    // =========================================================================
    // CONVERSATION ACTIONS
    // =========================================================================

    private static Participant Person(string id, string number, bool isMe = false, bool isVisible = true) =>
        new() { ID = new SmallInfo { ParticipantID = id, Number = number }, IsMe = isMe, IsVisible = isVisible };

    // This is a one-on-one chat. Me and the other person are both visible and have numbers.
    private static ConversationInfo Dm()
    {
        var c = new Conversation { ConversationID = "c1", Status = ConversationStatus.Active };
        c.Participants.Add(Person("me1", "+15550000", isMe: true));
        c.Participants.Add(Person("p1", "+15550001"));
        return ConversationInfo.From(c);
    }

    /// <summary>
    /// Each action builds the LibGM request. Archive and mute fill only <c>UpdateData</c> (conversation_builder.go).
    /// Delete, block and unblock fill <c>DeleteData</c> with the number of the other person (methods.go,
    /// handlematrix.go). Only a block has <c>Action5</c>.
    /// </summary>
    [TestMethod]
    [DataRow(ConversationAction.Archive, UpdateConversationRequest.DataOneofCase.UpdateData, ConversationActionStatus.UnknownActionStatus, false, "Status=Archived")]
    [DataRow(ConversationAction.Unarchive, UpdateConversationRequest.DataOneofCase.UpdateData, ConversationActionStatus.UnknownActionStatus, false, "Status=Active")]
    [DataRow(ConversationAction.Mute, UpdateConversationRequest.DataOneofCase.UpdateData, ConversationActionStatus.UnknownActionStatus, false, "Mute=Mute")]
    [DataRow(ConversationAction.Unmute, UpdateConversationRequest.DataOneofCase.UpdateData, ConversationActionStatus.UnknownActionStatus, false, "Mute=Unmute")]
    [DataRow(ConversationAction.Delete, UpdateConversationRequest.DataOneofCase.DeleteData, ConversationActionStatus.Delete, false, "Phone=+15550001")]
    [DataRow(ConversationAction.Block, UpdateConversationRequest.DataOneofCase.DeleteData, ConversationActionStatus.Block, true, "Phone=+15550001")]
    [DataRow(ConversationAction.BlockAndReport, UpdateConversationRequest.DataOneofCase.DeleteData, ConversationActionStatus.BlockAndReport, true, "Phone=+15550001")]
    [DataRow(ConversationAction.Unblock, UpdateConversationRequest.DataOneofCase.DeleteData, ConversationActionStatus.Unblock, false, "Phone=+15550001")]
    public void BuildRequest_Table(ConversationAction action, UpdateConversationRequest.DataOneofCase data, ConversationActionStatus status, bool action5, string inner)
    {
        UpdateConversationRequest request = ChatStore.BuildRequest(Dm(), action);

        Assert.AreEqual(data, request.DataCase);
        Assert.AreEqual(status, request.Action);
        Assert.AreEqual(action5, request.Action5 is not null);
        if (action5)
        {
            Assert.IsTrue(request.Action5!.Field2);
        }

        string got = request.DataCase == UpdateConversationRequest.DataOneofCase.UpdateData
            ? request.UpdateData.DataCase == UpdateConversationData.DataOneofCase.Status ? $"Status={request.UpdateData.Status}" : $"Mute={request.UpdateData.Mute}"
            : $"Phone={request.DeleteData.Phone}";
        Assert.AreEqual(inner, got);

        // Archive and mute leave the top-level ID empty. The other actions set the ID twice.
        if (data == UpdateConversationRequest.DataOneofCase.UpdateData)
        {
            Assert.AreEqual("c1", request.UpdateData.ConversationID);
            Assert.AreEqual(string.Empty, request.ConversationID);
        }
        else
        {
            Assert.AreEqual("c1", request.DeleteData.ConversationID);
            Assert.AreEqual("c1", request.ConversationID);
        }
    }

    [TestMethod]
    [DataRow(ConversationAction.Archive, ConversationStatus.Archived)]
    [DataRow(ConversationAction.Unarchive, ConversationStatus.Active)]
    [DataRow(ConversationAction.Mute, null)]
    [DataRow(ConversationAction.Unmute, null)]
    [DataRow(ConversationAction.Block, ConversationStatus.BlockedFolder)]
    [DataRow(ConversationAction.BlockAndReport, ConversationStatus.BlockedFolder)]
    [DataRow(ConversationAction.Unblock, ConversationStatus.Active)]
    [DataRow(ConversationAction.Delete, ConversationStatus.Deleted)]
    public void StatusAfter_Table(ConversationAction action, ConversationStatus? expected)
    {
        Assert.AreEqual(expected, ChatStore.StatusAfter(action));
    }

    [TestMethod]
    public void BuildRequest_Unmute_SerializesMuteField()
    {
        // Unmute is the zero value of proto3. Inside a oneof, the zero value must still go on the wire.
        byte[] wire = ChatStore.BuildRequest(Dm(), ConversationAction.Unmute).ToByteArray();

        UpdateConversationRequest parsed = UpdateConversationRequest.Parser.ParseFrom(wire);

        Assert.AreEqual(UpdateConversationData.DataOneofCase.Mute, parsed.UpdateData.DataCase);
        Assert.AreEqual(ConversationMuteStatus.Unmute, parsed.UpdateData.Mute);
    }

    [TestMethod]
    public void BuildRequest_Group_NoPhone()
    {
        // All members have visible numbers. Only the group check leaves the phone empty,
        // because findPhoneForConversation works for a one-on-one chat only.
        var c = new Conversation { ConversationID = "g1", IsGroupChat = true };
        c.Participants.Add(Person("me1", "+15550000", isMe: true));
        c.Participants.Add(Person("p1", "+15550001"));
        c.Participants.Add(Person("p2", "+15550002"));
        ConversationInfo group = ConversationInfo.From(c);

        Assert.IsFalse(ChatStore.BuildRequest(group, ConversationAction.Delete).DeleteData.HasPhone);
        Assert.IsFalse(ChatStore.BuildRequest(group, ConversationAction.Block).DeleteData.HasPhone);
    }

    [TestMethod]
    public void BuildRequest_DmHiddenOrMe_SkipsThem()
    {
        // The order is: me, a hidden participant, a visible participant with no number, the other person.
        var c = new Conversation { ConversationID = "c1" };
        c.Participants.Add(Person("me1", "+15550000", isMe: true));
        c.Participants.Add(Person("p0", "+15550009", isVisible: false));
        c.Participants.Add(Person("p2", string.Empty));
        c.Participants.Add(Person("p1", "+15550001"));

        UpdateConversationRequest request = ChatStore.BuildRequest(ConversationInfo.From(c), ConversationAction.Block);

        Assert.AreEqual("+15550001", request.DeleteData.Phone);
    }

    [TestMethod]
    public async Task Update_Archive_AppliesStatusAndRaisesChanged()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        var raised = new List<ConversationInfo>();
        rig.Store.ConversationChanged += (_, c) => raised.Add(c);

        await rig.Store.UpdateConversationAsync("c1", ConversationAction.Archive, Ct);

        Assert.AreEqual(ConversationStatus.Archived, rig.Client.ConversationUpdates.Single().UpdateData.Status);
        Assert.AreEqual(ConversationStatus.Archived, rig.Store.Find("c1")?.Status);
        Assert.AreEqual(ConversationStatus.Archived, raised.Single().Status);
    }

    /// <summary>
    /// A chat archived or muted here stays archived or muted after a restart. The saved row changes too, so the chat
    /// does not show in its old place before the page of the phone arrives.
    /// </summary>
    [TestMethod]
    public async Task Update_ArchiveAndMute_SavedSoReopenedStoreLoadsThem()
    {
        using var rig = new Rig(withDatabase: true);
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c2", "Bo")));

        await rig.Store.UpdateConversationAsync("c1", ConversationAction.Archive, Ct);
        await rig.Store.UpdateConversationAsync("c2", ConversationAction.Mute, Ct);
        await rig.Store.FlushAsync();

        using var reopened = new ChatStore(rig.Controller, () => rig.Client, NullLogger.Instance, rig.Database, rig.Time, rig.Media);
        await reopened.LoadSavedAsync();
        Assert.AreEqual(ConversationStatus.Archived, reopened.Find("c1")?.Status);
        Assert.IsTrue(reopened.Find("c2")?.IsMuted);
        Assert.AreEqual(ConversationStatus.Active, reopened.Find("c2")?.Status);
    }

    /// <summary>
    /// A chat deleted here leaves the database with its messages. The store saves an unmute too.
    /// </summary>
    [TestMethod]
    public async Task Update_DeleteAndUnmute_Saved()
    {
        using var rig = new Rig(withDatabase: true);
        Conversation muted = Conv("c2", "Bo");
        muted.Muted        = ConversationMuteStatus.Mute;
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        await rig.Store.HandleEventAsync(new ConversationUpdated(muted));
        rig.Database!.SaveMessages("c1", [Msg("m1", "c1")]);
        rig.Store.SaveDraft("c1", "unsent");

        await rig.Store.UpdateConversationAsync("c1", ConversationAction.Delete, Ct);
        await rig.Store.UpdateConversationAsync("c2", ConversationAction.Unmute, Ct);
        await rig.Store.FlushAsync();

        using var reopened = new ChatStore(rig.Controller, () => rig.Client, NullLogger.Instance, rig.Database, rig.Time, rig.Media);
        await reopened.LoadSavedAsync();
        Assert.IsNull(reopened.Find("c1"));
        Assert.AreEqual(0, rig.Database.LoadMessages("c1", 10).Count);
        Assert.AreEqual(string.Empty, rig.Store.Draft("c1"));
        Assert.IsFalse(rig.Database.LoadDrafts().ContainsKey("c1"));
        Assert.IsFalse(reopened.Find("c2")?.IsMuted);
    }

    /// <summary>
    /// A reply from a toast makes my reply the newest message. Marking the chat read then still saves it as read.
    /// </summary>
    [TestMethod]
    public async Task MarkRead_NewestIsMyReply_SavesRead()
    {
        using var rig = new Rig(withDatabase: true);
        Conversation unread    = Conv("c1", "Ann");
        unread.Unread          = true;
        unread.LatestMessageID = "m2";
        unread.LatestMessage   = new LatestMessage { DisplayContent = "on my way", FromMe = 1 };
        await rig.Store.HandleEventAsync(new ConversationUpdated(unread));

        await rig.Store.MarkReadAsync("c1", "m1", Ct);
        await rig.Store.FlushAsync();

        using var reopened = new ChatStore(rig.Controller, () => rig.Client, NullLogger.Instance, rig.Database, rig.Time, rig.Media);
        await reopened.LoadSavedAsync();
        Assert.IsFalse(reopened.Find("c1")?.Unread);
    }

    [TestMethod]
    public async Task Update_PhoneRefuses_NoLocalChange()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        int raised = 0;
        rig.Store.ConversationChanged += (_, _) => raised++;
        rig.Client.RefuseUpdates       = true;

        MessageSendException refused = await Assert.ThrowsExactlyAsync<MessageSendException>(() => rig.Store.UpdateConversationAsync("c1", ConversationAction.Archive, Ct));

        Assert.AreEqual("ConversationUpdateRefused", refused.Status);
        Assert.AreEqual(1, rig.Client.ConversationUpdates.Count);
        Assert.AreEqual(ConversationStatus.Active, rig.Store.Find("c1")?.Status);
        Assert.AreEqual(0, raised);
    }

    [TestMethod]
    public async Task Update_NotConnected_Throws()
    {
        using var rig = new Rig(connected: false);
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        int raised = 0;
        rig.Store.ConversationChanged += (_, _) => raised++;

        await Assert.ThrowsExactlyAsync<MessagingUnavailableException>(() => rig.Store.UpdateConversationAsync("c1", ConversationAction.Archive, Ct));

        Assert.AreEqual(0, rig.Client.ConversationUpdates.Count);
        Assert.AreEqual(ConversationStatus.Active, rig.Store.Find("c1")?.Status);
        Assert.AreEqual(0, raised);
    }

    /// <summary>
    /// An unpair or sign-out can happen while the phone answers. The answer then changes nothing, even in a
    /// conversation of a new phone that has the same ID.
    /// </summary>
    /// <param name="action">A status action, or mute.</param>
    [TestMethod]
    [DataRow(ConversationAction.Archive)]
    [DataRow(ConversationAction.Mute)]
    public async Task Update_ResetDuringRequest_AppliesNothing(ConversationAction action)
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1", "Old Ann")));
        var gate              = new TaskCompletionSource();
        rig.Client.UpdateGate = gate;
        Task update           = rig.Store.UpdateConversationAsync("c1", action, Ct);

        rig.Store.Reset();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1", "New Ann")));
        int raised = 0;
        rig.Store.ConversationChanged += (_, _) => raised++;
        gate.SetResult();
        await update;

        Assert.AreEqual(ConversationStatus.Active, rig.Store.Find("c1")?.Status);
        Assert.IsFalse(rig.Store.Find("c1")?.IsMuted);
        Assert.AreEqual(0, raised);
    }

    [TestMethod]
    public async Task Update_Mute_SetsIsMutedAndRaisesChanged()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        var raised = new List<ConversationInfo>();
        rig.Store.ConversationChanged += (_, c) => raised.Add(c);

        await rig.Store.UpdateConversationAsync("c1", ConversationAction.Mute, Ct);

        Assert.IsTrue(rig.Store.Find("c1")?.IsMuted);
        Assert.IsTrue(raised.Single().IsMuted);
        Assert.AreEqual(ConversationStatus.Active, raised.Single().Status);
    }

    /// <summary>
    /// A message in a chat muted here does not notify. After the user unmutes the chat here, the next message
    /// notifies. Review Focus 3 requires this.
    /// </summary>
    [TestMethod]
    public async Task Unmuted_NotifiesAgain()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        var arrived = new List<MessageArrivedEventArgs>();
        rig.Store.MessageArrived += (_, e) => arrived.Add(e);
        Message first  = Msg("m1", "c1");
        Message second = Msg("m2", "c1");
        first.MessageStatus.Status  = MessageStatusType.IncomingComplete;
        second.MessageStatus.Status = MessageStatusType.IncomingComplete;

        await rig.Store.UpdateConversationAsync("c1", ConversationAction.Mute, Ct);
        await rig.Store.HandleEventAsync(new MessageReceived(first, false));
        await rig.Store.UpdateConversationAsync("c1", ConversationAction.Unmute, Ct);
        await rig.Store.HandleEventAsync(new MessageReceived(second, false));

        Assert.IsFalse(NotificationPolicy.ShouldNotify(arrived[0], true, null, false, false), "muted");
        Assert.IsTrue(NotificationPolicy.ShouldNotify(arrived[1], true, null, false, false), "unmuted");
    }

    /// <summary>
    /// The phone has the final word. Its echo, or a mute that the user makes on the phone, sets the state.
    /// </summary>
    [TestMethod]
    public async Task PhoneEcho_MuteField_SetsIsMuted()
    {
        using var rig = new Rig();
        Conversation muted = Conv("c1");
        muted.Muted        = ConversationMuteStatus.Mute;

        await rig.Store.HandleEventAsync(new ConversationUpdated(muted));
        bool afterMute = rig.Store.Find("c1")?.IsMuted == true;
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));

        Assert.IsTrue(afterMute);
        Assert.IsFalse(rig.Store.Find("c1")?.IsMuted);
    }

    // =========================================================================
    // LOADING AND CONNECTION
    // =========================================================================

    [TestMethod]
    public void NotConnected_RequireClientThrowsMessagingUnavailable()
    {
        using var rig = new Rig(connected: false);

        Assert.ThrowsExactly<MessagingUnavailableException>(() => rig.Store.RequireClient());
    }

    [TestMethod]
    public async Task LoadMessages_ReturnsOldestFirst_AndOlderCursor()
    {
        using var rig = new Rig();
        rig.Client.MessagesPage = new ListMessagesResponse { Cursor = new Cursor { LastItemID = "older" } };
        rig.Client.MessagesPage.Messages.Add(Msg("m3", "c1", ts: 3_000_000));
        rig.Client.MessagesPage.Messages.Add(Msg("m2", "c1", ts: 2_000_000));
        rig.Client.MessagesPage.Messages.Add(Msg("m1", "c1", ts: 1_000_000));

        (IReadOnlyList<ChatMessage> messages, Cursor? older) = await rig.Store.LoadMessagesAsync("c1", null, Ct);

        Assert.AreEqual("m1,m2,m3", string.Join(",", messages.Select(m => m.Id)));
        Assert.AreEqual("older", older?.LastItemID);
    }

    [TestMethod]
    public async Task LoadMessages_NoCursorFromPhone_OlderCursorIsTheOldestMessage()
    {
        // Google usually sends no cursor. As mautrix-gmessages does, the store uses the oldest message as the cursor.
        // The cursor time is in milliseconds.
        using var rig = new Rig();
        rig.Client.MessagesPage = new ListMessagesResponse();
        rig.Client.MessagesPage.Messages.Add(Msg("m3", "c1", ts: 3_000_000));
        rig.Client.MessagesPage.Messages.Add(Msg("m2", "c1", ts: 2_000_000));

        (_, Cursor? older) = await rig.Store.LoadMessagesAsync("c1", null, Ct);

        Assert.AreEqual("m2", older?.LastItemID);
        Assert.AreEqual(2_000, older?.LastItemTimestamp);
    }

    [TestMethod]
    public async Task LoadMessages_EmptyPage_NoOlderCursor()
    {
        using var rig = new Rig();
        rig.Client.MessagesPage = new ListMessagesResponse();

        (_, Cursor? older) = await rig.Store.LoadMessagesAsync("c1", new Cursor { LastItemID = "m1", LastItemTimestamp = 1_000 }, Ct);

        Assert.IsNull(older);
    }

    [TestMethod]
    public async Task LoadMessages_OlderPage_DropsTheCursorMessageAndNewer()
    {
        // The phone can answer with the message of the cursor, or with newer messages that the chat already shows.
        using var rig = new Rig();
        rig.Client.MessagesPage = new ListMessagesResponse();
        rig.Client.MessagesPage.Messages.Add(Msg("m3", "c1", ts: 3_000_000));
        rig.Client.MessagesPage.Messages.Add(Msg("m2", "c1", ts: 2_000_000));
        rig.Client.MessagesPage.Messages.Add(Msg("m1", "c1", ts: 1_000_000));

        (IReadOnlyList<ChatMessage> messages, Cursor? older) = await rig.Store.LoadMessagesAsync("c1", new Cursor { LastItemID = "m2", LastItemTimestamp = 2_000 }, Ct);

        Assert.AreEqual("m1", Ids(messages));
        Assert.AreEqual("m1", older?.LastItemID);
    }

    [TestMethod]
    public async Task LoadFolder_FillsCache_AndAsks25()
    {
        using var rig = new Rig();
        rig.Client.ConversationsPage = new ListConversationsResponse();
        rig.Client.ConversationsPage.Conversations.Add(Conv("c1", "Ann"));

        rig.Client.ConversationsPage.Cursor = new Cursor { LastItemID = "next" };
        var raised = new List<ConversationInfo>();
        rig.Store.ConversationChanged += (_, c) => raised.Add(c);

        (IReadOnlyList<ConversationInfo> page, Cursor? next) = await rig.Store.LoadFolderAsync(ConversationFolder.SpamBlocked, null, Ct);

        Assert.AreEqual("next", next?.LastItemID);
        Assert.AreEqual(1, raised.Count);
        Assert.AreEqual(1, page.Count);
        Assert.AreEqual("Ann", rig.Store.Find("c1")?.Name);
        Assert.AreEqual((ListConversationsRequest.Types.Folder.SpamBlocked, 25L), rig.Client.ListCalls.Single());
    }

    [TestMethod]
    public async Task MarkRead_ClearsCachedUnread_AndRaises()
    {
        using var rig = new Rig();
        Conversation unread = Conv("c1", "Ann");
        unread.Unread = true;
        await rig.Store.HandleEventAsync(new ConversationUpdated(unread));
        var raised = new List<ConversationInfo>();
        rig.Store.ConversationChanged += (_, c) => raised.Add(c);

        await rig.Store.MarkReadAsync("c1", "m1", Ct);

        // The row stops being bold at once. It does not wait for the phone to send the conversation back.
        Assert.IsFalse(rig.Store.Find("c1")?.Unread);
        Assert.IsFalse(raised.Single().Unread);

        await rig.Store.MarkReadAsync("c1", "m1", Ct);
        Assert.AreEqual(1, raised.Count);
    }

    /// <summary>
    /// A chat read here stays read after a restart. The saved row is marked read, so the list does not show the
    /// chat as unread before the page of the phone arrives.
    /// </summary>
    [TestMethod]
    public async Task MarkRead_SavesRead_SoReopenedStoreLoadsItRead()
    {
        using var rig = new Rig(withDatabase: true);
        Conversation unread    = Conv("c1", "Ann");
        unread.Unread          = true;
        unread.LatestMessageID = "m1";
        await rig.Store.HandleEventAsync(new ConversationUpdated(unread));

        await rig.Store.MarkReadAsync("c1", "m1", Ct);
        await rig.Store.FlushAsync();

        using var reopened = new ChatStore(rig.Controller, () => rig.Client, NullLogger.Instance, rig.Database, rig.Time, rig.Media);
        await reopened.LoadSavedAsync();
        Assert.IsFalse(reopened.Find("c1")?.Unread);
    }

    /// <summary>
    /// A message newer than the read message can arrive while the store tells the phone. This message keeps the saved
    /// row unread, so a restart never hides a message that the user did not see.
    /// </summary>
    [TestMethod]
    public async Task MarkRead_NewerMessageCameIn_KeepsSavedUnread()
    {
        using var rig = new Rig(withDatabase: true);
        Conversation unread    = Conv("c1", "Ann");
        unread.Unread          = true;
        unread.LatestMessageID = "m2";
        await rig.Store.HandleEventAsync(new ConversationUpdated(unread));

        await rig.Store.MarkReadAsync("c1", "m1", Ct);
        await rig.Store.FlushAsync();

        using var reopened = new ChatStore(rig.Controller, () => rig.Client, NullLogger.Instance, rig.Database, rig.Time, rig.Media);
        await reopened.LoadSavedAsync();
        Assert.IsTrue(reopened.Find("c1")?.Unread);
    }

    [TestMethod]
    public async Task SendText_UnknownConversation_NotFound_ThrowsBeforeSending()
    {
        using var rig = new Rig();

        await Assert.ThrowsExactlyAsync<MessageSendException>(() => rig.Store.SendTextAsync("nope", "x", "t", null, Ct));

        Assert.AreEqual(0, rig.Client.Sent.Count);
    }

    [TestMethod]
    public async Task SendText_UncachedConversation_IsFetchedCachedAndRaised()
    {
        using var rig = new Rig();
        rig.Client.Conversations["c5"] = Conv("c5", "Bo", outgoing: "me1");
        var raised = new List<ConversationInfo>();
        rig.Store.ConversationChanged += (_, c) => raised.Add(c);

        await rig.Store.SendTextAsync("c5", "x", "t", null, Ct);

        Assert.AreEqual("me1", rig.Client.Sent.Single().MessagePayload.ParticipantID);
        Assert.AreEqual("Bo", rig.Store.Find("c5")?.Name);
        Assert.AreEqual(1, raised.Count);
    }

    private sealed class OneThreadContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = [];
        private readonly Thread _thread;

        public OneThreadContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach ((SendOrPostCallback cb, object? state) in _queue.GetConsumingEnumerable())
                {
                    cb(state);
                }
            });
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public Task RunAsync(Func<Task> work)
        {
            var tcs = new TaskCompletionSource();
            Post(async _ =>
            {
                try
                {
                    await work();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, null);
            return tcs.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
        }
    }

    [TestMethod]
    public async Task MessageForUnknownConversation_AsyncFetch_RaisesOnTheCallerThread()
    {
        using var rig = new Rig();
        using var ui  = new OneThreadContext();
        rig.Client.Conversations["c9"] = Conv("c9", "Fetched");
        rig.Client.YieldInFetch        = true;
        var threads                    = new List<int>();
        rig.Store.ConversationChanged += (_, _) => threads.Add(Environment.CurrentManagedThreadId);
        rig.Store.MessageArrived      += (_, _) => threads.Add(Environment.CurrentManagedThreadId);

        await ui.RunAsync(() => rig.Store.HandleEventAsync(new MessageReceived(Msg("m1", "c9"), false)));

        Assert.AreEqual(2, threads.Count);
        Assert.IsTrue(threads.All(t => t == ui.ThreadId));
    }

    // =========================================================================
    // ENCRYPTION, LOOKUP AND TYPING
    // =========================================================================

    private static Message Tomb(string id, string conversationId, MessageStatusType status, long ts) =>
        new() { MessageID = id, ConversationID = conversationId, Timestamp = ts, MessageStatus = new MessageStatus { Status = status } };

    [TestMethod]
    public async Task Store_IsEncrypted_NewestTombstoneWins()
    {
        using var rig = new Rig(withDatabase: true);
        rig.Client.MessagesPage = PageOf(
            Tomb("t1", "c1", MessageStatusType.TombstoneEncryptedOneOnOneRcsCreated, 1_000_000),
            Tomb("t2", "c1", MessageStatusType.TombstoneProtocolSwitchToText, 2_000_000));
        Assert.IsFalse(rig.Store.IsEncrypted("c1"));

        await rig.Store.LoadMessagesAsync("c1", null, Ct);
        Assert.IsFalse(rig.Store.IsEncrypted("c1"));

        // A newer switch arrives live, then an older switch replays. The newer switch stays.
        await rig.Store.HandleEventAsync(new MessageReceived(Tomb("t3", "c1", MessageStatusType.TombstoneProtocolSwitchToEncryptedRcs, 3_000_000), false));
        await rig.Store.HandleEventAsync(new MessageReceived(Tomb("t1", "c1", MessageStatusType.TombstoneProtocolSwitchToText, 1_000_000), true));
        Assert.IsTrue(rig.Store.IsEncrypted("c1"));
        await rig.Store.FlushAsync();

        // After a restart, the lock comes back from the database.
        using var restarted = new ChatStore(rig.Controller, () => null, NullLogger.Instance, rig.Database);
        await restarted.LoadSavedAsync();
        Assert.IsTrue(restarted.IsEncrypted("c1"));
    }

    /// <summary>
    /// A list page can be requested before a live update, for example when the user reads the chat while the page is
    /// on its way. The older, unread copy of the page does not replace the live copy.
    /// A conversation that did not change takes the copy of the page.
    /// </summary>
    [TestMethod]
    public async Task ListPage_AskedBeforeALiveChange_KeepsTheLiveCopy()
    {
        using var rig       = new Rig();
        Conversation unread = Conv("c1");
        unread.Unread       = true;
        Conversation other  = Conv("c2", "Bo");
        await rig.Store.HandleEventAsync(new ConversationUpdated(unread));

        var listGate                 = new TaskCompletionSource();
        rig.Client.ConversationsPage = new ListConversationsResponse();
        rig.Client.ConversationsPage.Conversations.Add(unread);
        rig.Client.ConversationsPage.Conversations.Add(other);
        rig.Client.ListGate          = listGate;
        Task<(IReadOnlyList<ConversationInfo> Conversations, Cursor? Next)> folder = rig.Store.LoadFolderAsync(ConversationFolder.Inbox, null, Ct);

        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        listGate.SetResult();
        (IReadOnlyList<ConversationInfo> page, _) = await folder;

        Assert.IsFalse(rig.Store.Find("c1")!.Unread, "The older page put the read chat back as unread.");
        Assert.IsFalse(page[0].Unread);
        Assert.IsNotNull(rig.Store.Find("c2"));
    }

    /// <summary>
    /// The encryption flag of the conversation (field 39) decides the lock. Without the flag, the newest encryption
    /// line decides.
    /// </summary>
    [TestMethod]
    public async Task Store_IsEncrypted_TheConversationsOwnFlagWins()
    {
        using var rig = new Rig();

        // The conversation has no flag, and the tombstone says encrypted.
        await rig.Store.HandleEventAsync(new MessageReceived(Tomb("t1", "c1", MessageStatusType.TombstoneEncryptedOneOnOneRcsCreated, 1_000_000), true));
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        Assert.IsTrue(rig.Store.IsEncrypted("c1"));

        // The phone first says the conversation is not encrypted, then says it is encrypted.
        Conversation plain    = Conv("c1");
        plain.EncryptionState = new EncryptionState { Encrypted = false };
        await rig.Store.HandleEventAsync(new ConversationUpdated(plain));
        Assert.IsFalse(rig.Store.IsEncrypted("c1"));

        Conversation secure    = Conv("c1");
        secure.EncryptionState = new EncryptionState { Encrypted = true };
        await rig.Store.HandleEventAsync(new ConversationUpdated(secure));
        Assert.IsTrue(rig.Store.IsEncrypted("c1"));
    }

    [TestMethod]
    public async Task Store_FindMessage_MemoryElseDatabase()
    {
        using var rig = new Rig(withDatabase: true);
        rig.Database!.SaveMessages("c1", [Msg("m1", "c1", "saved")]);
        rig.Client.MessagesPage = PageOf(Msg("m2", "c1", "fresh"));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);

        Assert.AreEqual("fresh", (await rig.Store.FindMessageAsync("c1", "m2"))?.Text);
        Assert.AreEqual("saved", (await rig.Store.FindMessageAsync("c1", "m1"))?.Text);
        Assert.IsNull(await rig.Store.FindMessageAsync("c1", "m9"));
    }

    /// <summary>
    /// An empty ID finds nothing. It does not find a message that is still sending, because that message has no ID yet.
    /// </summary>
    [TestMethod]
    public async Task Store_FindMessage_EmptyId_FindsNothing()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1"));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);
        await rig.Store.SendTextAsync("c1", "sending", "tmp-1", null, Ct);

        Assert.IsNull(await rig.Store.FindMessageAsync("c1", string.Empty));
    }

    /// <summary>
    /// A page that arrives after a reset leaves no lock state of the old account.
    /// </summary>
    [TestMethod]
    public async Task Store_IsEncrypted_ResetMidLoad_KeepsNoLock()
    {
        using var rig           = new Rig();
        rig.Client.MessagesPage = PageOf(Tomb("t1", "c1", MessageStatusType.TombstoneProtocolSwitchToEncryptedRcs, 1_000_000));
        var gate                = new TaskCompletionSource();
        rig.Client.FetchGate    = gate;

        Task load = rig.Store.LoadMessagesAsync("c1", null, Ct);
        rig.Store.Reset();
        gate.SetResult();
        await load;

        Assert.IsFalse(rig.Store.IsEncrypted("c1"));
    }

    [TestMethod]
    public async Task Store_Typing_MapsNumberToParticipant()
    {
        using var rig = new Rig();
        Conversation c = Conv("c1");
        c.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "p2", Number = "+15550100" }, FullName = "Ann" });
        await rig.Store.HandleEventAsync(new ConversationUpdated(c));
        var raised = new List<TypingEventArgs>();
        rig.Store.TypingChanged += (_, e) => raised.Add(e);

        await rig.Store.HandleEventAsync(new TypingChanged(new TypingData { ConversationID = "c1", User = new User { Number = "+15550100" }, Type = TypingTypes.StartedTyping }));
        await rig.Store.HandleEventAsync(new TypingChanged(new TypingData { ConversationID = "c1", User = new User { Number = "+15550100" }, Type = TypingTypes.StoppedTyping }));

        Assert.AreEqual(2, raised.Count);
        Assert.AreEqual("c1", raised[0].ConversationId);
        Assert.AreEqual("p2", raised[0].ParticipantId);
        Assert.IsTrue(raised[0].IsTyping);
        Assert.IsFalse(raised[1].IsTyping);
    }

    [TestMethod]
    public async Task Store_Typing_UnknownNumber_NoEvent()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        int raised = 0;
        rig.Store.TypingChanged += (_, _) => raised++;

        await rig.Store.HandleEventAsync(new TypingChanged(new TypingData { ConversationID = "c1", User = new User { Number = "+15559999" }, Type = TypingTypes.StartedTyping }));
        await rig.Store.HandleEventAsync(new TypingChanged(new TypingData { ConversationID = "c9", User = new User { Number = "+15559999" }, Type = TypingTypes.StartedTyping }));

        Assert.AreEqual(0, raised);
    }

    private static UserAlert Alert(AlertType type) => new(new UserAlertEvent { AlertType = type });

    /// <summary>Low sets the flag and raises once (a repeat is silent); restored clears it and raises.</summary>
    [TestMethod]
    public async Task BatteryLow_SetsAndRaisesOnce_RestoredClears()
    {
        using var rig = new Rig();
        int raised = 0;
        rig.Store.PhoneBatteryChanged += (_, _) => raised++;
        Assert.IsFalse(rig.Store.IsPhoneBatteryLow);

        await rig.Store.HandleEventAsync(Alert(AlertType.MobileBatteryLow));
        await rig.Store.HandleEventAsync(Alert(AlertType.MobileBatteryLow));
        Assert.IsTrue(rig.Store.IsPhoneBatteryLow);
        Assert.AreEqual(1, raised);

        await rig.Store.HandleEventAsync(Alert(AlertType.MobileBatteryRestored));
        Assert.IsFalse(rig.Store.IsPhoneBatteryLow);
        Assert.AreEqual(2, raised);
    }

    /// <summary>Alerts other than the two battery ones change nothing and raise nothing.</summary>
    [TestMethod]
    public async Task BatteryLow_OtherAlerts_Ignored()
    {
        using var rig = new Rig();
        int raised = 0;
        rig.Store.PhoneBatteryChanged += (_, _) => raised++;

        await rig.Store.HandleEventAsync(Alert(AlertType.BrowserInactive));
        await rig.Store.HandleEventAsync(Alert(AlertType.Unknown));

        Assert.IsFalse(rig.Store.IsPhoneBatteryLow);
        Assert.AreEqual(0, raised);
    }

    /// <summary>Reset forgets a low battery and tells the views.</summary>
    [TestMethod]
    public async Task BatteryLow_ClearedOnReset()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(Alert(AlertType.MobileBatteryLow));
        int raised = 0;
        rig.Store.PhoneBatteryChanged += (_, _) => raised++;

        rig.Store.Reset();

        Assert.IsFalse(rig.Store.IsPhoneBatteryLow);
        Assert.AreEqual(1, raised);
    }

    /// <summary>Every low alert raises the alerted event, a repeat too; restored does not.</summary>
    [TestMethod]
    public async Task BatteryLow_AlertedRaisesOnEveryLow()
    {
        using var rig = new Rig();
        int alerted = 0;
        rig.Store.PhoneBatteryLowAlerted += (_, _) => alerted++;

        await rig.Store.HandleEventAsync(Alert(AlertType.MobileBatteryLow));
        await rig.Store.HandleEventAsync(Alert(AlertType.MobileBatteryLow));
        await rig.Store.HandleEventAsync(Alert(AlertType.MobileBatteryRestored));

        Assert.AreEqual(2, alerted);
    }
}
