using Google.Protobuf;
using GMessages.Presentation.Connection;
using GMessages.Presentation.Messaging;
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
    // MUTE PROBE
    // =========================================================================

    [TestMethod]
    public void DescribeUnknownFields_ListsNumbersAndInts_NeverStrings()
    {
        // Known Field Plus Unknown 40 (Varint 1), 41 (String), 42 (Nested {1: 1})
        byte[] extra = [0xC0, 0x02, 0x01, 0xCA, 0x02, 0x0B, .. "secret-name"u8.ToArray(), 0xD2, 0x02, 0x02, 0x08, 0x01];
        byte[] known = new Conversation { ConversationID = "c1" }.ToByteArray();
        Conversation conversation = Conversation.Parser.ParseFrom([.. known, .. extra]);

        string text = ChatStore.DescribeUnknownFields(conversation);

        Assert.AreEqual("40=1 41=len11 42=len2{1=1}", text);
        Assert.IsFalse(text.Contains("secret"));
    }

    [TestMethod]
    public void DescribeUnknownFields_TextLookingNested_NeverLogsDigits()
    {
        // "x5" And "h1x2" Parse As Varint Pairs; Their Values Must Not Print
        byte[] extra = [0xCA, 0x02, 0x02, .. "x5"u8.ToArray(), 0xD2, 0x02, 0x04, .. "h1x2"u8.ToArray()];
        Conversation conversation = Conversation.Parser.ParseFrom(extra);

        string text = ChatStore.DescribeUnknownFields(conversation);

        Assert.AreEqual("41=len2{15=n} 42=len4{13=n 15=n}", text);
    }

    [TestMethod]
    public void DescribeUnknownFields_None_Empty()
    {
        Assert.AreEqual("", ChatStore.DescribeUnknownFields(new Conversation { ConversationID = "c1" }));
    }

    // =========================================================================
    // RIG
    // =========================================================================

    // The Default Message Time Below (Msg), So Default Messages Are Recent
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

            Store = new ChatStore(Controller, () => connected ? Client : null, NullLogger.Instance, Database, Time);
        }

        public ConnectionController Controller { get; }

        public FakeMessagingClient Client { get; } = new();

        public FakeTimeProvider Time { get; } = new(Now);

        public MessageDatabase? Database { get; }

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
    /// After an unpair (Reset) a newly paired phone whose conversation IDs collide with the old phone's sees none of
    /// the old conversations, owner, SIMs or notified IDs.
    /// </summary>
    [TestMethod]
    public async Task Reset_NewPhoneSeesNothingOfTheOldOne()
    {
        using var rig = new Rig();

        // The Old Phone
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1", "Old Ann", outgoing: "me1")));
        var settings = new GMessages.Protocol.Proto.Settings();
        settings.SIMCards.Add(Sim("me1", "old"));
        await rig.Store.HandleEventAsync(new SettingsChanged(settings));
        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m1", "c1"), false));
        Assert.AreEqual("me1", rig.Store.Me?.Id);

        rig.Store.Reset();

        Assert.IsNull(rig.Store.Find("c1"));
        Assert.IsNull(rig.Store.Me);

        // The New Phone Reuses ID c1 (And A Message ID The Old One Notified)
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

        // A Missing Conversation (Null Answer) Is The Same As A Failed Fetch
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

        // The Phone Answers Newest First
        page.Messages.AddRange(oldestFirst.Reverse());
        return page;
    }

    private static string Ids(IReadOnlyList<ChatMessage>? messages) => messages is null ? "(none)" : string.Join(",", messages.Select(m => m.Id.Length > 0 ? m.Id : m.TmpId));

    /// <summary>
    /// The newest page of a conversation is kept in memory; an older page is not.
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
    /// A message that arrives updates the cached page: a new one goes in by time, a known one is replaced, and the
    /// echo of a sent message replaces its pending copy.
    /// </summary>
    [TestMethod]
    public async Task Arrival_UpdatesCachedPage()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1", ts: 1_000), Msg("m2", "c1", ts: 2_000));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);

        // New
        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m3", "c1", "three", ts: 3_000), false));
        Assert.AreEqual("m1,m2,m3", Ids(rig.Store.CachedMessages("c1")));

        // Known: Replaced
        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m2", "c1", "edited", ts: 2_000), false));
        Assert.AreEqual("edited", rig.Store.CachedMessages("c1")![1].Text);

        // Sent, Then Its Echo
        await rig.Store.SendTextAsync("c1", "mine", "tmp-9", null, Ct);
        Assert.AreEqual("m1,m2,m3,tmp-9", Ids(rig.Store.CachedMessages("c1")));
        Message echo = Msg("m4", "c1", "mine", ts: 4_000);
        echo.TmpID = "tmp-9";
        await rig.Store.HandleEventAsync(new MessageReceived(echo, false));
        Assert.AreEqual("m1,m2,m3,m4", Ids(rig.Store.CachedMessages("c1")));

        // Another Conversation's Message Isn't Cached (It Was Never Opened)
        await rig.Store.HandleEventAsync(new MessageReceived(Msg("x1", "c2"), false));
        Assert.IsNull(rig.Store.CachedMessages("c2"));
    }

    /// <summary>
    /// The cache keeps the last 20 opened conversations: loading a 21st drops the one used longest ago.
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

        // Opening c0 Again Makes c1 The Oldest
        Assert.IsNotNull(rig.Store.CachedMessages("c0"));
        await rig.Store.LoadMessagesAsync("c-new", null, Ct);

        Assert.IsNotNull(rig.Store.CachedMessages("c0"));
        Assert.IsNull(rig.Store.CachedMessages("c1"));
        Assert.IsNotNull(rig.Store.CachedMessages("c-new"));
    }

    /// <summary>
    /// The phone's echo can arrive before the send call returns; the pending copy cached after it must not replace
    /// the delivered one.
    /// </summary>
    [TestMethod]
    public async Task EchoBeforeSendReturns_KeepsDeliveredCopy()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1", ts: 1_000));
        await rig.Store.LoadMessagesAsync("c1", null, Ct);

        // The Echo Lands While The Send Is Still Waiting For Its Answer
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
    /// A message that arrives while the newest page is being fetched stays cached when the (older) page lands.
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

    // =========================================================================
    // DATABASE
    // =========================================================================

    /// <summary>
    /// Saved conversations fill the cache (so the list shows at once after a restart) without raising anything.
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
    /// An unpair or sign-out while the saved conversations wait their turn on the queue: the old account's rows never
    /// reach memory.
    /// </summary>
    [TestMethod]
    public async Task Reset_WhileLoadSavedQueued_LoadsNothing()
    {
        using var rig = new Rig(withDatabase: true);
        rig.Database!.SaveConversation(Conv("c1", "Old Ann"));

        // Another Process Holds The File, So A Save Blocks The Queue And The Load Waits Behind It
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
    /// A conversation with no page in memory shows the database's newest messages, which then sit in memory.
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

        // A Chat Opened While Unpairing: Its Read Must Not Recreate The File
        Task<IReadOnlyList<ChatMessage>?> read = rig.Store.CachedMessagesAsync("c1");
        rig.Store.Reset();
        Assert.IsNull(await read);
        await rig.Store.FlushAsync();

        Assert.IsFalse(File.Exists(rig.DatabasePath));
    }

    /// <summary>
    /// A fetch that started before an unpair or sign-out drops its result: nothing of the old account reaches memory
    /// or the database, and nothing is raised.
    /// </summary>
    [TestMethod]
    public async Task Reset_DuringFetch_WritesNothing()
    {
        using var rig = new Rig(withDatabase: true);
        int raised = 0;
        rig.Store.ConversationChanged += (_, _) => raised++;
        rig.Store.MessageArrived      += (_, _) => raised++;

        // Messages
        var fetch_gate          = new TaskCompletionSource();
        rig.Client.MessagesPage = PageOf(Msg("m1", "c1"));
        rig.Client.FetchGate    = fetch_gate;
        Task<(IReadOnlyList<ChatMessage> Messages, Cursor? Older)> messages = rig.Store.LoadMessagesAsync("c1", null, Ct);

        // Conversations
        var list_gate                = new TaskCompletionSource();
        rig.Client.ConversationsPage = new ListConversationsResponse();
        rig.Client.ConversationsPage.Conversations.Add(Conv("c2", "Ann"));
        rig.Client.ListGate = list_gate;
        Task<(IReadOnlyList<ConversationInfo> Conversations, Cursor? Next)> folder = rig.Store.LoadFolderAsync(ConversationFolder.Inbox, null, Ct);

        // A Live Message Whose Conversation Is Still Being Fetched
        var conversation_gate          = new TaskCompletionSource();
        rig.Client.Conversations["c3"] = Conv("c3", "Bo");
        rig.Client.GetConversationGate = conversation_gate;
        Task live = rig.Store.HandleEventAsync(new MessageReceived(Msg("m3", "c3"), false));

        rig.Store.Reset();
        fetch_gate.SetResult();
        list_gate.SetResult();
        conversation_gate.SetResult();
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
    /// A status or reaction update for a message older than a day, never seen before (or out of the 256-ID ring),
    /// is not new, so it never toasts (the mautrix-gmessages rule for reactions to old messages).
    /// </summary>
    [TestMethod]
    public async Task ReactionOnDayOldMessage_IsNotNew()
    {
        using var rig = new Rig();
        await rig.Store.HandleEventAsync(new ConversationUpdated(Conv("c1")));
        MessageArrivedEventArgs? got = null;
        rig.Store.MessageArrived += (_, e) => got = e;

        long two_days_ago = Now.AddDays(-2).ToUnixTimeMilliseconds() * 1000;
        await rig.Store.HandleEventAsync(new MessageReceived(Msg("m-old", "c1", ts: two_days_ago), false));

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
        // Google Usually Sends No Cursor; Like mautrix-gmessages, The Oldest Message (Its Time In Milliseconds) Is One
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
        // The Phone Can Answer With The Cursor's Own Message, Or Newer Ones, Already Shown Below
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

        // The Row Stops Being Bold Without Waiting For The Phone To Send The Conversation Back
        Assert.IsFalse(rig.Store.Find("c1")?.Unread);
        Assert.IsFalse(raised.Single().Unread);

        // Already Read: Nothing Changes, Nothing Raised
        await rig.Store.MarkReadAsync("c1", "m1", Ct);
        Assert.AreEqual(1, raised.Count);
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

    // A Single-Threaded Context That Runs Posted Work On One Dedicated Thread
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

        // A Newer Switch Arrives Live, Then An Older One Replays: The Newer Stays
        await rig.Store.HandleEventAsync(new MessageReceived(Tomb("t3", "c1", MessageStatusType.TombstoneProtocolSwitchToEncryptedRcs, 3_000_000), false));
        await rig.Store.HandleEventAsync(new MessageReceived(Tomb("t1", "c1", MessageStatusType.TombstoneProtocolSwitchToText, 1_000_000), true));
        Assert.IsTrue(rig.Store.IsEncrypted("c1"));
        await rig.Store.FlushAsync();

        // After A Restart The Lock Comes Back From The Database
        using var restarted = new ChatStore(rig.Controller, () => null, NullLogger.Instance, rig.Database);
        await restarted.LoadSavedAsync();
        Assert.IsTrue(restarted.IsEncrypted("c1"));
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
    /// An empty ID finds nothing, not a still-sending message (which has no ID yet).
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
    /// A page that lands after a reset leaves no lock state of the old account.
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
}
