using System.Net;
using System.Text;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.LongPoll;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using GMessages.Protocol.Tests.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GMessages.Protocol.Tests.Rpc;

/// <summary>
/// Checks that <see cref="RpcSession"/> fills the plain ID fields that the phone leaves out.
/// </summary>
/// <remarks>
/// These tests have no Go golden file. Since November 2026 the phone sends a conversation with no field 1, and a
/// message with no field 1 and no field 7. The IDs come only in the nested ID fields: 31 on a conversation, 23 and
/// 24 on a message. Each test builds that shape, encrypts it with the keys of the account and decodes it as the long
/// poll does. The old shape, with the plain fields, must decode with no change.
/// </remarks>
[TestClass]
public class RpcSessionMissingIdsTests
{
    private const string ConversationId  = "626";
    private const string MessageId       = "195765";
    private const string LatestMessageId = "196880";

    // A conversation as the phone sends it now: no field 1, the ID only in field 31.
    private static Conversation NewShapeConversation() => new()
    {
        Name                   = "Golden",
        LatestMessageID        = LatestMessageId,
        SomeKindOfGroupID      = new SomeKindOfGroupID { ConversationID = ConversationId },
        LatestMessageAnotherID = new AnotherMessageID { MessageID = LatestMessageId, AnotherID = "golden-another-id" },
    };

    // A message as the phone sends it now: no field 1 and no field 7, the IDs only in fields 23 and 24.
    private static Message NewShapeMessage() => new()
    {
        ParticipantID     = "1",
        Timestamp         = 1,
        SomeKindOfGroupID = new SomeKindOfGroupID { ConversationID = ConversationId },
        AnotherMessageID  = new AnotherMessageID { MessageID = MessageId, AnotherID = "golden-another-id" },
    };

    private static (RpcSession Session, AuthData Auth) CreateSession()
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        var time      = new FakeTimeProvider();
        var handler   = new FakeHttpHandler { Time = time };
        handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", Encoding.UTF8.GetBytes("[]"));
        var http = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
        return (new RpcSession(http, auth, GmEndpoints.Production, time, NullLogger.Instance), auth);
    }

    // An encrypted data event, as the long poll delivers it.
    private static IncomingRPCMessage Incoming(AuthData auth, ActionType action, IMessage payload)
    {
        var data = new RPCMessageData
        {
            SessionID     = "request-1",
            Action        = action,
            EncryptedData = ByteString.CopyFrom(auth.RequestCrypto.Encrypt(payload.ToByteArray())),
        };
        return new IncomingRPCMessage { ResponseID = "response-1", BugleRoute = BugleRoute.DataEvent, MessageData = data.ToByteString() };
    }

    private static T Decode<T>(ActionType action, T payload) where T : IMessage<T>
    {
        (RpcSession session, AuthData auth) = CreateSession();
        return (T)session.Decode(Incoming(auth, action, payload)).DecryptedMessage!;
    }

    // Feeds 1 live update through the dispatcher and returns the events that it raised.
    private static List<object> Dispatch(UpdateEvents update)
    {
        (RpcSession session, AuthData auth) = CreateSession();
        var dispatcher = new UpdateDispatcher(session, NullLogger.Instance);
        var events     = new List<object>();
        dispatcher.EventRaised += events.Add;

        dispatcher.Handle(Incoming(auth, ActionType.GetUpdates, update));
        return events;
    }

    /// <summary>
    /// The new shape has no plain ID on the wire. This guards the other tests against a shape that still carries one.
    /// </summary>
    [TestMethod]
    public void NewShape_HasNoPlainIdFieldsOnTheWire()
    {
        Assert.AreEqual(string.Empty, Conversation.Parser.ParseFrom(NewShapeConversation().ToByteArray()).ConversationID);

        Message message = Message.Parser.ParseFrom(NewShapeMessage().ToByteArray());
        Assert.AreEqual(string.Empty, message.MessageID);
        Assert.AreEqual(string.Empty, message.ConversationID);
    }

    /// <summary>
    /// Each conversation of a list answer gets its ID from field 31.
    /// </summary>
    [TestMethod]
    public void Decode_ListConversations_NewShape_FillsConversationIds()
    {
        Conversation second = NewShapeConversation();
        second.SomeKindOfGroupID.ConversationID = "627";
        var response = new ListConversationsResponse { Conversations = { NewShapeConversation(), second } };

        ListConversationsResponse decoded = Decode(ActionType.ListConversations, response);

        CollectionAssert.AreEqual(new[] { ConversationId, "627" }, decoded.Conversations.Select(c => c.ConversationID).ToArray());
    }

    /// <summary>
    /// The single conversation of a get answer and of a get-or-create answer gets its ID from field 31.
    /// </summary>
    [TestMethod]
    public void Decode_SingleConversationAnswers_NewShape_FillConversationId()
    {
        GetConversationResponse got             = Decode(ActionType.GetConversation, new GetConversationResponse { Conversation = NewShapeConversation() });
        GetOrCreateConversationResponse created = Decode(ActionType.GetOrCreateConversation, new GetOrCreateConversationResponse { Conversation = NewShapeConversation() });

        Assert.AreEqual(ConversationId, got.Conversation.ConversationID);
        Assert.AreEqual(ConversationId, created.Conversation.ConversationID);
    }

    /// <summary>
    /// A conversation with no latest message ID in field 17 gets it from field 35.
    /// </summary>
    [TestMethod]
    public void Decode_Conversation_NoLatestMessageId_FillsItFromNestedField()
    {
        Conversation conversation = NewShapeConversation();
        conversation.LatestMessageID = string.Empty;

        GetConversationResponse decoded = Decode(ActionType.GetConversation, new GetConversationResponse { Conversation = conversation });

        Assert.AreEqual(LatestMessageId, decoded.Conversation.LatestMessageID);
    }

    /// <summary>
    /// Each message of a list answer gets its message ID from field 24 and its conversation ID from field 23.
    /// </summary>
    [TestMethod]
    public void Decode_ListMessages_NewShape_FillsMessageAndConversationIds()
    {
        Message second = NewShapeMessage();
        second.AnotherMessageID.MessageID = "195766";
        var response = new ListMessagesResponse { Messages = { NewShapeMessage(), second } };

        ListMessagesResponse decoded = Decode(ActionType.ListMessages, response);

        CollectionAssert.AreEqual(new[] { MessageId, "195766" }, decoded.Messages.Select(m => m.MessageID).ToArray());
        CollectionAssert.AreEqual(new[] { ConversationId, ConversationId }, decoded.Messages.Select(m => m.ConversationID).ToArray());
    }

    /// <summary>
    /// A live conversation update, a live message and a typing notice reach the subscribers with their IDs filled.
    /// </summary>
    [TestMethod]
    public void Dispatch_LiveUpdates_NewShape_RaiseEventsWithIds()
    {
        var typing = new TypingData { User = new User { Number = "+15550100" }, SomeKindOfGroupID = new SomeKindOfGroupID { ConversationID = ConversationId } };

        var conversation = (ConversationUpdated)Dispatch(new UpdateEvents { ConversationEvent = new ConversationEvent { Data = { NewShapeConversation() } } }).Single();
        var message      = (MessageReceived)Dispatch(new UpdateEvents { MessageEvent = new MessageEvent { Data = { NewShapeMessage() } } }).Single();
        var typed        = (TypingChanged)Dispatch(new UpdateEvents { TypingEvent = new TypingEvent { Data = typing } }).Single();

        Assert.AreEqual(ConversationId, conversation.Conversation.ConversationID);
        Assert.AreEqual(MessageId, message.Message.MessageID);
        Assert.AreEqual(ConversationId, message.Message.ConversationID);
        Assert.AreEqual(ConversationId, typed.Data.ConversationID);
    }

    /// <summary>
    /// The old shape decodes with no change. A plain ID wins over a nested ID that differs from it.
    /// </summary>
    [TestMethod]
    public void Decode_OldShape_KeepsPlainIds()
    {
        Conversation conversation = NewShapeConversation();
        conversation.ConversationID  = "old-conversation";
        conversation.LatestMessageID = "old-latest";
        Message message = NewShapeMessage();
        message.MessageID      = "old-message";
        message.ConversationID = "old-conversation";
        var conversations = new ListConversationsResponse { Conversations = { conversation, new Conversation { ConversationID = "plain-only" } } };
        var messages      = new ListMessagesResponse { Messages = { message, new Message { MessageID = "plain-only", ConversationID = "plain-only" } } };
        var typing        = new UpdateEvents { TypingEvent = new TypingEvent { Data = new TypingData { ConversationID = "old-conversation", SomeKindOfGroupID = new SomeKindOfGroupID { ConversationID = ConversationId } } } };

        Assert.AreEqual(conversations, Decode(ActionType.ListConversations, conversations.Clone()));
        Assert.AreEqual(messages, Decode(ActionType.ListMessages, messages.Clone()));
        Assert.AreEqual(typing, Decode(ActionType.GetUpdates, typing.Clone()));
    }

    /// <summary>
    /// A conversation and a message with no ID in any field decode with empty IDs and do not throw.
    /// </summary>
    [TestMethod]
    public void Decode_NoIdAnywhere_LeavesIdsEmpty()
    {
        var conversations = new ListConversationsResponse { Conversations = { new Conversation { Name = "Golden" } } };
        var messages      = new ListMessagesResponse { Messages = { new Message { ParticipantID = "1" } } };

        Assert.AreEqual(conversations, Decode(ActionType.ListConversations, conversations.Clone()));
        Assert.AreEqual(messages, Decode(ActionType.ListMessages, messages.Clone()));
    }
}
