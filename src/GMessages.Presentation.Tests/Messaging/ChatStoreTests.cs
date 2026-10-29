using GMessages.Presentation.Connection;
using GMessages.Presentation.Messaging;
using GMessages.Protocol;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.Proto;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class ChatStoreTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // =========================================================================
    // RIG
    // =========================================================================

    private sealed class Rig : IDisposable
    {
        public Rig(bool connected = true)
        {
            Controller = new ConnectionController(
                new AppPaths(Path.Combine(Path.GetTempPath(), "chat-test-" + Guid.NewGuid().ToString("N"))),
                GmEndpoints.Production,
                TimeProvider.System,
                NullLoggerFactory.Instance,
                a => a());
            Store = new ChatStore(Controller, () => connected ? Client : null, NullLogger.Instance);
        }

        public ConnectionController Controller { get; }

        public FakeMessagingClient Client { get; } = new();

        public ChatStore Store { get; }

        public void Dispose() => Store.Dispose();
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

        ChatMessage sent = await rig.Store.SendTextAsync("c1", "hello", "tmp-1", Ct);

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

        await Assert.ThrowsExactlyAsync<MessageSendException>(() => rig.Store.SendTextAsync("c1", "x", "t", Ct));
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
    public async Task SendText_UnknownConversation_NotFound_ThrowsBeforeSending()
    {
        using var rig = new Rig();

        await Assert.ThrowsExactlyAsync<MessageSendException>(() => rig.Store.SendTextAsync("nope", "x", "t", Ct));

        Assert.AreEqual(0, rig.Client.Sent.Count);
    }

    [TestMethod]
    public async Task SendText_UncachedConversation_IsFetchedCachedAndRaised()
    {
        using var rig = new Rig();
        rig.Client.Conversations["c5"] = Conv("c5", "Bo", outgoing: "me1");
        var raised = new List<ConversationInfo>();
        rig.Store.ConversationChanged += (_, c) => raised.Add(c);

        await rig.Store.SendTextAsync("c5", "x", "t", Ct);

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
}
