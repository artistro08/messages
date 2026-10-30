using System.Net;
using System.Text;
using GMessages.Protocol.Http;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using GMessages.Protocol.Tests.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Protocol.Tests;

/// <summary>
/// Checks the <c>methods.go</c> requests on <see cref="GmClient"/>: what each one sends and what it hands back.
/// </summary>
/// <remarks>
/// C#-only. The expected values (actions, the 1/350/50 contact request, the top contacts count of 8, the annotation on
/// the first conversation list) were read from methods.go. The client is not connected; the test marks the RPC session
/// as listening, captures the posted request, decrypts it with the account's keys, and then delivers an encrypted
/// answer the way the long poll would. See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go"/>.
/// </remarks>
[TestClass]
public class GmClientMessagingTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private static readonly string[] ExpectedNumbers      = ["+15550100", "+15550101"];
    private static readonly string[] ExpectedParticipants = ["p-1", "p-2"];
    private static readonly string[] ExpectedContacts     = ["k-1"];

    private sealed class Rig : IAsyncDisposable
    {
        public required GmClient Client { get; init; }

        public required AuthData Auth { get; init; }

        public required FakeHttpHandler Handler { get; init; }

        public required SignalingTimeProvider Time { get; init; }

        public required string SessionPath { get; init; }

        public int RequestCount { get { lock (Handler.Requests) { return Handler.Requests.Count(r => r.Url.EndsWith("/SendMessage", StringComparison.Ordinal)); } } }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            File.Delete(SessionPath);
        }
    }

    // A Request As The Phone Would See It
    private sealed record Sent(OutgoingRPCMessage Message, OutgoingRPCData Data, byte[] Plain)
    {
        public string RequestId => Data.RequestID;

        public T Payload<T>(MessageParser<T> parser) where T : IMessage<T> => parser.ParseFrom(Plain);
    }

    private static Rig CreateRig()
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        var time      = new SignalingTimeProvider();
        var handler   = new FakeHttpHandler { Time = time };
        string path   = Path.Combine(Path.GetTempPath(), "gmclient-msg-test-" + Guid.NewGuid().ToString("N") + ".bin");
        handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", "[]"u8.ToArray());
        var client = GmClient.Create(auth, new SessionStore(path), GmEndpoints.Production, time, NullLoggerFactory.Instance, handler);

        // No Long Poll Here: Answers Are Delivered By Hand
        client.Rpc.IsListening = () => true;
        return new Rig { Client = client, Auth = auth, Handler = handler, Time = time, SessionPath = path };
    }

    // Waits For The Nth (Zero-Based) Request, Then Decodes And Decrypts It
    private static Sent Captured(Rig rig, int index)
    {
        Assert.IsTrue(SpinWait.SpinUntil(() => rig.RequestCount > index, HangGuard), $"request {index} was not sent");

        RecordedRequest req;
        lock (rig.Handler.Requests)
        {
            req = rig.Handler.Requests.Where(r => r.Url.EndsWith("/SendMessage", StringComparison.Ordinal)).ElementAt(index);
        }

        OutgoingRPCMessage message = PbLiteSerializer.Deserialize<OutgoingRPCMessage>(req.Body);
        OutgoingRPCData data       = OutgoingRPCData.Parser.ParseFrom(message.Data.MessageData);
        return new Sent(message, data, rig.Auth.RequestCrypto.Decrypt(data.EncryptedProtoData.Span));
    }

    // Delivers An Encrypted Answer For A Request, As The Long Poll Would
    private static void Answer(Rig rig, Sent sent, IMessage response, ActionType? action = null)
    {
        var data = new RPCMessageData
        {
            SessionID     = sent.RequestId,
            Action        = action ?? sent.Data.Action,
            EncryptedData = ByteString.CopyFrom(rig.Auth.RequestCrypto.Encrypt(response.ToByteArray())),
        };
        var raw = new IncomingRPCMessage { ResponseID = "response-1", BugleRoute = BugleRoute.DataEvent, MessageData = data.ToByteString() };

        Assert.IsTrue(rig.Client.Rpc.TryDeliverResponse(rig.Client.Rpc.Decode(raw)), "a request was waiting for the answer");
    }

    /// <summary>
    /// The first conversation list is sent as an annotation, later ones as normal messages; folder and count are sent.
    /// </summary>
    [TestMethod]
    public async Task ListConversations_FirstIsAnnotation_ThenMessage()
    {
        await using Rig rig = CreateRig();

        // First Call
        Task<ListConversationsResponse> first = rig.Client.ListConversationsAsync(ListConversationsRequest.Types.Folder.Inbox, 25, null, CancellationToken.None);
        Sent sent_first = Captured(rig, 0);
        Answer(rig, sent_first, new ListConversationsResponse());
        await first.WaitAsync(HangGuard);

        // Second Call
        Task<ListConversationsResponse> second = rig.Client.ListConversationsAsync(ListConversationsRequest.Types.Folder.Inbox, 25, null, CancellationToken.None);
        Sent sent_second = Captured(rig, 1);
        Answer(rig, sent_second, new ListConversationsResponse());
        await second.WaitAsync(HangGuard);

        Assert.AreEqual(ActionType.ListConversations, sent_first.Data.Action);
        Assert.AreEqual(MessageType.BugleAnnotation, sent_first.Message.Data.MessageTypeData.MessageType);
        Assert.AreEqual(MessageType.BugleMessage, sent_second.Message.Data.MessageTypeData.MessageType);
        foreach (Sent sent in new[] { sent_first, sent_second })
        {
            ListConversationsRequest request = sent.Payload(ListConversationsRequest.Parser);
            Assert.AreEqual(ListConversationsRequest.Types.Folder.Inbox, request.Folder);
            Assert.AreEqual(25L, request.Count);
        }
    }

    /// <summary>
    /// FetchMessages sends the conversation ID, count and cursor, and returns the answer.
    /// </summary>
    [TestMethod]
    public async Task FetchMessages_SendsConversationCountCursor()
    {
        await using Rig rig = CreateRig();
        var cursor = new Cursor { LastItemID = "m-9", LastItemTimestamp = 1234 };

        Task<ListMessagesResponse> call = rig.Client.FetchMessagesAsync("c-1", 30, cursor, CancellationToken.None);
        Sent sent = Captured(rig, 0);
        Answer(rig, sent, new ListMessagesResponse { TotalMessages = 7 });
        ListMessagesResponse result = await call.WaitAsync(HangGuard);

        Assert.AreEqual(ActionType.ListMessages, sent.Data.Action);
        Assert.AreEqual(MessageType.BugleMessage, sent.Message.Data.MessageTypeData.MessageType);
        ListMessagesRequest request = sent.Payload(ListMessagesRequest.Parser);
        Assert.AreEqual("c-1", request.ConversationID);
        Assert.AreEqual(30L, request.Count);
        Assert.AreEqual("m-9", request.Cursor.LastItemID);
        Assert.AreEqual(1234L, request.Cursor.LastItemTimestamp);
        Assert.AreEqual(7L, result.TotalMessages);
    }

    /// <summary>
    /// SendMessage is user-initiated: five seconds without an answer raises PhoneSlow once and keeps waiting; the status is returned.
    /// </summary>
    [TestMethod]
    public async Task SendMessage_IsUserInitiated_ReturnsStatus()
    {
        await using Rig rig = CreateRig();
        int slow_count = 0;
        rig.Client.Rpc.PhoneSlow += (_, _) => Interlocked.Increment(ref slow_count);

        Task<SendMessageResponse> call = rig.Client.SendMessageAsync(new SendMessageRequest { ConversationID = "c-1", TmpID = "tmp-1" }, CancellationToken.None);
        Sent sent = Captured(rig, 0);

        // Five Seconds With No Answer
        Assert.IsTrue(SpinWait.SpinUntil(() => rig.Time.CountTimers(RpcSession.PhoneSlowTimeout) > 0, HangGuard), "slow timer");
        rig.Time.Advance(RpcSession.PhoneSlowTimeout);
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref slow_count) > 0, HangGuard), "PhoneSlow raised");
        Assert.IsFalse(call.IsCompleted);

        Answer(rig, sent, new SendMessageResponse { Status = SendMessageResponse.Types.Status.Success });
        SendMessageResponse result = await call.WaitAsync(HangGuard);

        Assert.AreEqual(ActionType.SendMessage, sent.Data.Action);
        SendMessageRequest request = sent.Payload(SendMessageRequest.Parser);
        Assert.AreEqual("c-1", request.ConversationID);
        Assert.AreEqual("tmp-1", request.TmpID);
        Assert.AreEqual(SendMessageResponse.Types.Status.Success, result.Status);
        Assert.AreEqual(1, Volatile.Read(ref slow_count), "raised once");
    }

    /// <summary>
    /// SendReaction is user-initiated: five seconds without an answer raises PhoneSlow once; the request goes out as
    /// given and the typed answer is returned.
    /// </summary>
    [TestMethod]
    public async Task SendReaction_IsUserInitiated_SendsRequest()
    {
        await using Rig rig = CreateRig();
        int slow_count = 0;
        rig.Client.Rpc.PhoneSlow += (_, _) => Interlocked.Increment(ref slow_count);
        var request = new SendReactionRequest
        {
            MessageID    = "m-5",
            ReactionData = EmojiTypes.MakeReactionData("👍"),
            Action       = SendReactionRequest.Types.Action.Add,
            SIMPayload   = new SIMPayload { SIMNumber = 1 },
        };

        Task<SendReactionResponse> call = rig.Client.SendReactionAsync(request, CancellationToken.None);
        Sent sent = Captured(rig, 0);

        // Five Seconds With No Answer
        Assert.IsTrue(SpinWait.SpinUntil(() => rig.Time.CountTimers(RpcSession.PhoneSlowTimeout) > 0, HangGuard), "slow timer");
        rig.Time.Advance(RpcSession.PhoneSlowTimeout);
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref slow_count) > 0, HangGuard), "PhoneSlow raised");

        Answer(rig, sent, new SendReactionResponse { Success = true });
        SendReactionResponse result = await call.WaitAsync(HangGuard);

        Assert.AreEqual(ActionType.SendReaction, sent.Data.Action);
        SendReactionRequest sent_request = sent.Payload(SendReactionRequest.Parser);
        Assert.AreEqual(request, sent_request);
        Assert.AreEqual(EmojiType.Like, sent_request.ReactionData.Type);
        Assert.IsTrue(result.Success);
    }

    /// <summary>
    /// GetFullSizeImage is user-initiated (five seconds without an answer raises PhoneSlow) and sends action 36 with
    /// both IDs, as in Go's methods.go.
    /// </summary>
    [TestMethod]
    public async Task GetFullSizeImage_IsUserInitiated()
    {
        await using Rig rig = CreateRig();
        int slow_count = 0;
        rig.Client.Rpc.PhoneSlow += (_, _) => Interlocked.Increment(ref slow_count);

        Task call = rig.Client.GetFullSizeImageAsync("m-7", "a-7", CancellationToken.None);
        Sent sent = Captured(rig, 0);

        // Five Seconds With No Answer
        Assert.IsTrue(SpinWait.SpinUntil(() => rig.Time.CountTimers(RpcSession.PhoneSlowTimeout) > 0, HangGuard), "slow timer");
        rig.Time.Advance(RpcSession.PhoneSlowTimeout);
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref slow_count) > 0, HangGuard), "PhoneSlow raised");

        Answer(rig, sent, new GetFullSizeImageResponse());
        await call.WaitAsync(HangGuard);

        Assert.AreEqual(ActionType.GetFullSizeImage, sent.Data.Action);
        Assert.AreEqual(36, (int)sent.Data.Action);
        Assert.AreEqual(new GetFullSizeImageRequest { MessageID = "m-7", ActionMessageID = "a-7" }, sent.Payload(GetFullSizeImageRequest.Parser));
    }

    /// <summary>
    /// UpdateConversation is user-initiated: five seconds without an answer raises PhoneSlow once; the request goes out
    /// as given and the typed answer is returned.
    /// </summary>
    [TestMethod]
    public async Task UpdateConversation_IsUserInitiated_SendsRequest()
    {
        await using Rig rig = CreateRig();
        int slow_count = 0;
        rig.Client.Rpc.PhoneSlow += (_, _) => Interlocked.Increment(ref slow_count);
        var request = new UpdateConversationRequest
        {
            ConversationID = "c-1",
            UpdateData     = new UpdateConversationData { ConversationID = "c-1", Status = ConversationStatus.Archived },
        };

        Task<UpdateConversationResponse> call = rig.Client.UpdateConversationAsync(request, CancellationToken.None);
        Sent sent = Captured(rig, 0);

        // Five Seconds With No Answer
        Assert.IsTrue(SpinWait.SpinUntil(() => rig.Time.CountTimers(RpcSession.PhoneSlowTimeout) > 0, HangGuard), "slow timer");
        rig.Time.Advance(RpcSession.PhoneSlowTimeout);
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref slow_count) > 0, HangGuard), "PhoneSlow raised");

        Answer(rig, sent, new UpdateConversationResponse { Success = true });
        UpdateConversationResponse result = await call.WaitAsync(HangGuard);

        Assert.AreEqual(ActionType.UpdateConversation, sent.Data.Action);
        UpdateConversationRequest sent_request = sent.Payload(UpdateConversationRequest.Parser);
        Assert.AreEqual(request, sent_request);
        Assert.AreEqual(ConversationStatus.Archived, sent_request.UpdateData.Status);
        Assert.IsTrue(result.Success);
    }

    /// <summary>
    /// SetTyping sends a typing update with the conversation, typing true and the SIM, and completes without an answer.
    /// </summary>
    [TestMethod]
    public async Task SetTyping_SendsTypingUpdatesWithoutWaiting()
    {
        await using Rig rig = CreateRig();

        await rig.Client.SetTypingAsync("c-1", new SIMPayload { SIMNumber = 2 }, CancellationToken.None).WaitAsync(HangGuard);
        Sent sent = Captured(rig, 0);

        Assert.AreEqual(ActionType.TypingUpdates, sent.Data.Action);
        TypingUpdateRequest request = sent.Payload(TypingUpdateRequest.Parser);
        Assert.AreEqual("c-1", request.Data.ConversationID);
        Assert.IsTrue(request.Data.Typing);
        Assert.AreEqual(2, request.SIMPayload.SIMNumber);
    }

    /// <summary>
    /// MarkRead sends both IDs and completes once the phone answers.
    /// </summary>
    [TestMethod]
    public async Task MarkRead_SendsIds()
    {
        await using Rig rig = CreateRig();

        Task call = rig.Client.MarkReadAsync("c-1", "m-5", CancellationToken.None);
        Sent sent = Captured(rig, 0);
        Assert.IsFalse(call.IsCompleted, "waits for the answer");
        Answer(rig, sent, new UpdateConversationResponse(), ActionType.MessageRead);
        await call.WaitAsync(HangGuard);

        Assert.AreEqual(ActionType.MessageRead, sent.Data.Action);
        MessageReadRequest request = sent.Payload(MessageReadRequest.Parser);
        Assert.AreEqual("c-1", request.ConversationID);
        Assert.AreEqual("m-5", request.MessageID);
    }

    /// <summary>
    /// GetConversation sends the ID and returns the conversation inside the answer.
    /// </summary>
    [TestMethod]
    public async Task GetConversation_SendsIdAndUnwraps()
    {
        await using Rig rig = CreateRig();

        Task<Conversation?> call = rig.Client.GetConversationAsync("c-1", CancellationToken.None);
        Sent sent = Captured(rig, 0);
        Answer(rig, sent, new GetConversationResponse { Conversation = new Conversation { ConversationID = "c-1" } });
        Conversation? result = await call.WaitAsync(HangGuard);

        Assert.AreEqual(ActionType.GetConversation, sent.Data.Action);
        Assert.AreEqual("c-1", sent.Payload(GetConversationRequest.Parser).ConversationID);
        Assert.AreEqual("c-1", result!.ConversationID);
    }

    /// <summary>
    /// ListContacts always sends 1, 350 and 50.
    /// </summary>
    [TestMethod]
    public async Task ListContacts_Sends1_350_50()
    {
        await using Rig rig = CreateRig();

        Task<ListContactsResponse> call = rig.Client.ListContactsAsync(CancellationToken.None);
        Sent sent = Captured(rig, 0);
        Answer(rig, sent, new ListContactsResponse());
        await call.WaitAsync(HangGuard);

        Assert.AreEqual(ActionType.ListContacts, sent.Data.Action);
        ListContactsRequest request = sent.Payload(ListContactsRequest.Parser);
        Assert.AreEqual(1, request.I1);
        Assert.AreEqual(350, request.I2);
        Assert.AreEqual(50, request.I3);
    }

    /// <summary>
    /// ListTopContacts always asks for 8.
    /// </summary>
    [TestMethod]
    public async Task ListTopContacts_Sends8()
    {
        await using Rig rig = CreateRig();

        Task<ListTopContactsResponse> call = rig.Client.ListTopContactsAsync(CancellationToken.None);
        Sent sent = Captured(rig, 0);
        Answer(rig, sent, new ListTopContactsResponse());
        await call.WaitAsync(HangGuard);

        Assert.AreEqual(ActionType.ListTopContacts, sent.Data.Action);
        Assert.AreEqual(8, sent.Payload(ListTopContactsRequest.Parser).Count);
    }

    /// <summary>
    /// GetOrCreateConversation passes the request through unchanged: numbers and the RCS group name and flag.
    /// </summary>
    [TestMethod]
    public async Task GetOrCreateConversation_PassesNumbersAndGroupFlags()
    {
        await using Rig rig = CreateRig();
        var request = new GetOrCreateConversationRequest { RCSGroupName = "Friends", CreateRCSGroup = true };
        request.Numbers.Add(new ContactNumber { MysteriousInt = 2, Number = "+15550100" });
        request.Numbers.Add(new ContactNumber { MysteriousInt = 2, Number = "+15550101" });

        Task<GetOrCreateConversationResponse> call = rig.Client.GetOrCreateConversationAsync(request, CancellationToken.None);
        Sent sent = Captured(rig, 0);
        Answer(rig, sent, new GetOrCreateConversationResponse { Status = GetOrCreateConversationResponse.Types.Status.Success });
        GetOrCreateConversationResponse result = await call.WaitAsync(HangGuard);

        Assert.AreEqual(ActionType.GetOrCreateConversation, sent.Data.Action);
        GetOrCreateConversationRequest sent_request = sent.Payload(GetOrCreateConversationRequest.Parser);
        Assert.AreEqual("Friends", sent_request.RCSGroupName);
        Assert.IsTrue(sent_request.CreateRCSGroup);
        CollectionAssert.AreEqual(ExpectedNumbers, sent_request.Numbers.Select(n => n.Number).ToArray());
        Assert.AreEqual(GetOrCreateConversationResponse.Types.Status.Success, result.Status);
    }

    /// <summary>
    /// The participant thumbnail request carries the identifiers under the participants action.
    /// </summary>
    [TestMethod]
    public async Task GetParticipantThumbnail_SendsIdentifiers()
    {
        await using Rig rig = CreateRig();

        Task<GetThumbnailResponse> call = rig.Client.GetParticipantThumbnailAsync(["p-1", "p-2"], CancellationToken.None);
        Sent sent = Captured(rig, 0);
        Answer(rig, sent, new GetThumbnailResponse());
        await call.WaitAsync(HangGuard);

        Assert.AreEqual(ActionType.GetParticipantsThumbnail, sent.Data.Action);
        CollectionAssert.AreEqual(ExpectedParticipants, sent.Payload(GetThumbnailRequest.Parser).Identifiers.ToArray());
    }

    /// <summary>
    /// The contact thumbnail request carries the identifiers under the contacts action.
    /// </summary>
    [TestMethod]
    public async Task GetContactThumbnail_SendsIdentifiers()
    {
        await using Rig rig = CreateRig();

        Task<GetThumbnailResponse> call = rig.Client.GetContactThumbnailAsync(["k-1"], CancellationToken.None);
        Sent sent = Captured(rig, 0);
        Answer(rig, sent, new GetThumbnailResponse());
        await call.WaitAsync(HangGuard);

        Assert.AreEqual(ActionType.GetContactsThumbnail, sent.Data.Action);
        CollectionAssert.AreEqual(ExpectedContacts, sent.Payload(GetThumbnailRequest.Parser).Identifiers.ToArray());
    }

    /// <summary>
    /// An answer of another type (here a contacts list for a messages request) throws InvalidDataException.
    /// </summary>
    [TestMethod]
    public async Task WrongResponseType_ThrowsInvalidData()
    {
        await using Rig rig = CreateRig();

        Task<ListMessagesResponse> call = rig.Client.FetchMessagesAsync("c-1", 10, null, CancellationToken.None);
        Sent sent = Captured(rig, 0);
        Answer(rig, sent, new ListContactsResponse(), ActionType.ListContacts);

        InvalidDataException ex = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => call.WaitAsync(HangGuard));
        StringAssert.StartsWith(ex.Message, "unexpected response type ListContactsResponse");
    }

    /// <summary>
    /// Temporary IDs are GUIDs and differ per call.
    /// </summary>
    [TestMethod]
    public void NewTmpId_IsGuid()
    {
        string first  = GmClient.NewTmpId();
        string second = GmClient.NewTmpId();

        Assert.IsTrue(Guid.TryParseExact(first, "D", out _));
        Assert.AreNotEqual(first, second);
    }
}
