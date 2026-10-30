using System.Buffers.Binary;
using System.Security.Cryptography;
using GMessages.Protocol.Crypto;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.Proto;
using Google.Protobuf;

namespace GMessages.App.UITests.FakeGoogle;

/// <summary>
/// A fake phone behind <see cref="FakeGoogleServer"/>: the server side of the UKEY2 pairing, then encrypted answers.
/// </summary>
/// <remarks>
/// Mirrors what a real phone does in mautrix-gmessages' pair_google.go, seen from the other end. The client init's
/// commitment is kept; the answer is a server init with the phone's own fresh P-256 key, 32 random bytes and
/// verification and key-derivation versions 1/1. The client finished message is checked against the commitment, and
/// the shared secret is SHA-256 of the raw ECDH X coordinate of the phone's key with the client's public key. HKDF over
/// it (with the client and server init messages as info) gives the auth key, whose first four bytes pick
/// <see cref="ShownEmoji"/>, and the next key, which gives the request keys. The finish answer waits for
/// <see cref="Tap"/>. Answers go onto the open long poll as data events whose session ID is the request ID, which is
/// how the client matches them. Once paired, <c>NOTIFY_DITTO_ACTIVITY</c> pings are answered (encrypted), as a real
/// phone does, so the app never reports the phone as not responding. Unknown actions are only acked by the HTTP
/// reply; the phone sends nothing back.
/// <para>
/// Once paired it also keeps <see cref="Chats"/> and answers the requests the app sends for them: conversations
/// by folder, messages, sends (answered, then echoed as a live message with the request's temporary ID, unless
/// <see cref="FailSends"/>), reactions (answered with success, then the message is sent again with its reactions
/// updated), typing updates (kept in <see cref="Typing"/>, never answered), read marks, single conversations,
/// contacts, top contacts, get-or-create and thumbnails (always none). A <c>SettingsEvent</c> with one SIM goes out
/// after every <c>GET_UPDATES</c>. <see cref="PushIncoming"/> sends a live message, or, with <c>old</c>, one in the
/// startup catch-up batch; <see cref="PushStatusUpdate"/> and <see cref="PushStatus"/> send a message again with a new
/// status, as a real phone does several times per message; <see cref="PushTyping"/> sends a typing event.
/// Conversation updates (archive, mute, block, delete) are kept in <see cref="ConversationUpdates"/>, answered, applied
/// and the conversation sent again, unless <see cref="FailConversationUpdates"/>. <see cref="PushConversationStatus"/>
/// moves a conversation on the phone's side and <see cref="PushAlert"/> sends a user alert.
/// </para>
/// </remarks>
public sealed class FakePhone
{
    // Copied Verbatim From encryptionKeyInfo In pair_google.go
    private static readonly byte[] EncryptionKeyInfo = [130, 170, 85, 160, 211, 151, 248, 131, 70, 202, 28, 238, 141, 57, 9, 185, 95, 19, 250, 125, 235, 29, 74, 179, 131, 118, 184, 37, 109, 168, 85, 16];

    // Ann's Photo: A 1x1 Blue PNG
    private const string AnnPhotoPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGOQKn4BAAIgAXZmNuK1AAAAAElFTkSuQmCC";

    private readonly Lock _lock = new();
    private readonly Action<IncomingRPCMessage> _push;
    private readonly Action<IncomingRPCMessage> _pushCatchUp;
    private readonly List<SendMessageRequest> _sent  = [];
    private readonly List<MessageReadRequest> _reads = [];
    private readonly List<SendReactionRequest> _reactions = [];
    private readonly List<TypingUpdateRequest> _typing    = [];
    private readonly List<UpdateConversationRequest> _conversationUpdates = [];
    private AesCtrHelper? _derivedCrypto;
    private TaskCompletionSource _finishReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ECParameters _key;
    private byte[] _clientInit = [];
    private byte[] _serverInit = [];
    private byte[] _commitment = [];
    private byte[] _nextKey    = [];
    private string? _heldFinish;

    /// <summary>
    /// Creates a phone that sends its answers through <paramref name="push"/>.
    /// </summary>
    /// <param name="push">Puts a message on the open long poll.</param>
    /// <param name="pushCatchUp">Puts a message in the batch the next long poll starts with, which its startup ack counts.</param>
    internal FakePhone(Action<IncomingRPCMessage> push, Action<IncomingRPCMessage> pushCatchUp)
    {
        _push        = push;
        _pushCatchUp = pushCatchUp;
    }

    /// <summary>
    /// Gets the conversations, messages and contacts the phone answers with.
    /// </summary>
    public FakeChats Chats { get; } = new();

    /// <summary>
    /// Gets or sets whether sends are answered with <c>FAILURE_2</c> and no echo, to test failures without the 60 second timeout.
    /// </summary>
    public bool FailSends { get; set; }

    /// <summary>
    /// Gets or sets whether conversation updates are answered with <c>Success = false</c> and change nothing.
    /// </summary>
    public bool FailConversationUpdates { get; set; }

    /// <summary>
    /// Gets or sets how long every <c>ListMessages</c> answer waits before it goes out (zero by default).
    /// </summary>
    public TimeSpan MessagesDelay { get; set; }

    /// <summary>
    /// Gets or sets how much longer a <c>ListMessages</c> answer for an older page (the request carries a cursor)
    /// waits, on top of <see cref="MessagesDelay"/> (zero by default).
    /// </summary>
    public TimeSpan OlderPagesDelay { get; set; }

    /// <summary>
    /// Gets every <c>SendMessage</c> request so far, in arrival order.
    /// </summary>
    public IReadOnlyList<SendMessageRequest> Sent
    {
        get
        {
            lock (_lock)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>
    /// Gets every <c>MessageRead</c> request so far, in arrival order.
    /// </summary>
    public IReadOnlyList<MessageReadRequest> Reads
    {
        get
        {
            lock (_lock)
            {
                return [.. _reads];
            }
        }
    }

    /// <summary>
    /// Gets every <c>SendReaction</c> request so far, in arrival order.
    /// </summary>
    public IReadOnlyList<SendReactionRequest> Reactions
    {
        get
        {
            lock (_lock)
            {
                return [.. _reactions];
            }
        }
    }

    /// <summary>
    /// Gets every <c>TypingUpdates</c> request so far, in arrival order.
    /// </summary>
    public IReadOnlyList<TypingUpdateRequest> Typing
    {
        get
        {
            lock (_lock)
            {
                return [.. _typing];
            }
        }
    }

    /// <summary>
    /// Gets every <c>UpdateConversation</c> request so far, in arrival order, including failed ones.
    /// </summary>
    public IReadOnlyList<UpdateConversationRequest> ConversationUpdates
    {
        get
        {
            lock (_lock)
            {
                return [.. _conversationUpdates];
            }
        }
    }

    /// <summary>
    /// Gets the emoji the phone asks the user to tap, or null before the current attempt's client finished message.
    /// </summary>
    public string? ShownEmoji { get; private set; }

    /// <summary>
    /// Gets a task that completes once the current attempt's client finished message arrived and
    /// <see cref="ShownEmoji"/> is set.
    /// </summary>
    /// <remarks>Each client init starts a new attempt with a new task, so a retried pairing can be awaited too.</remarks>
    public Task FinishReceived
    {
        get
        {
            lock (_lock)
            {
                return _finishReceived.Task;
            }
        }
    }

    /// <summary>
    /// Gets the request keys after a correct <see cref="Tap"/>, or null before.
    /// </summary>
    /// <remarks>The same AES and HMAC keys the client derives, so later answers can be encrypted for it.</remarks>
    public AesCtrHelper? RequestCrypto { get; private set; }

    /// <summary>
    /// Answers the held client finished message as if the user tapped an emoji on the phone.
    /// </summary>
    /// <remarks>
    /// A correct tap derives the request keys and answers with success. A wrong one answers with
    /// <c>WRONG_VERIFICATION_CODE_SELECTED</c>.
    /// </remarks>
    /// <param name="correct">True when the user tapped the emoji the computer shows.</param>
    /// <exception cref="InvalidOperationException">No client finished message is waiting.</exception>
    public void Tap(bool correct)
    {
        string request_id;
        lock (_lock)
        {
            request_id  = _heldFinish ?? throw new InvalidOperationException("No pairing is waiting for a tap.");
            _heldFinish = null;
            if (correct)
            {
                RequestCrypto = _derivedCrypto;
            }
        }

        // Pairing Answers Are Never Encrypted
        GaiaPairingResponseContainer answer = correct
            ? new GaiaPairingResponseContainer()
            : new GaiaPairingResponseContainer { FinishErrorType = 1, FinishErrorCode = GaiaPairingErrorCode.WrongVerificationCodeSelected };
        Reply(request_id, ActionType.CreateGaiaPairingClientFinished, answer, encrypt: false);
    }

    /// <summary>
    /// Handles one request the client sent through <c>SendMessage</c>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <exception cref="InvalidDataException">A pairing message is invalid.</exception>
    internal void Handle(OutgoingRPCMessage request)
    {
        OutgoingRPCData data = OutgoingRPCData.Parser.ParseFrom(request.Data.MessageData);
        switch (data.Action)
        {
            case ActionType.CreateGaiaPairingClientInit:
                Reply(data.RequestID, data.Action, OnClientInit(data), encrypt: false);
                break;

            case ActionType.CreateGaiaPairingClientFinished:
                OnClientFinished(data);
                break;

            // Ping Answers Keep The Client From Reporting The Phone As Not Responding
            case ActionType.NotifyDittoActivity when RequestCrypto is not null:
                Reply(data.RequestID, data.Action, new NotifyDittoActivityResponse(), encrypt: true);
                break;

            // Every Update Request (Each Connect) Gets The Settings Event Right After, As From A Real Phone
            case ActionType.GetUpdates when RequestCrypto is not null:
                PushUpdate(new UpdateEvents { SettingsEvent = new Settings { SIMCards = { new SIMCard { SIMParticipant = new SIMParticipant { ID = FakeChats.MyParticipantId } } } } }, _push);
                break;

            case ActionType.ListConversations when RequestCrypto is not null:
                var list = Open(data, ListConversationsRequest.Parser);
                Reply(data.RequestID, data.Action, new ListConversationsResponse { Conversations = { Chats.List(list.Folder) } }, encrypt: true);
                break;

            case ActionType.ListMessages when RequestCrypto is not null:
                var messages = Open(data, ListMessagesRequest.Parser);
                (List<Message> page, Cursor? next) = Chats.Page(messages.ConversationID, messages.Count, messages.Cursor);
                var answer                          = new ListMessagesResponse { Messages = { page }, TotalMessages = page.Count, Cursor = next };
                TimeSpan delay                      = MessagesDelay + (messages.Cursor is { LastItemID.Length: > 0 } ? OlderPagesDelay : TimeSpan.Zero);
                if (delay <= TimeSpan.Zero)
                {
                    Reply(data.RequestID, data.Action, answer, encrypt: true);
                    break;
                }

                // Held Off The Request's Thread, So Other Requests Are Answered Meanwhile
                string request_id = data.RequestID;
                _ = Task.Delay(delay).ContinueWith(_ => Reply(request_id, ActionType.ListMessages, answer, encrypt: true), TaskScheduler.Default);
                break;

            case ActionType.SendMessage when RequestCrypto is not null:
                OnSendMessage(data);
                break;

            case ActionType.SendReaction when RequestCrypto is not null:
                OnSendReaction(data);
                break;

            case ActionType.UpdateConversation when RequestCrypto is not null:
                OnUpdateConversation(data);
                break;

            // Typing Is Sent Without Waiting, So The Phone Never Answers It
            case ActionType.TypingUpdates when RequestCrypto is not null:
                var typing = Open(data, TypingUpdateRequest.Parser);
                lock (_lock)
                {
                    _typing.Add(typing);
                }

                break;

            case ActionType.MessageRead when RequestCrypto is not null:
                var read = Open(data, MessageReadRequest.Parser);
                lock (_lock)
                {
                    _reads.Add(read);
                }

                Chats.MarkRead(read.ConversationID);
                Reply(data.RequestID, data.Action, new EmptyArr(), encrypt: true);
                break;

            case ActionType.GetConversation when RequestCrypto is not null:
                var get = Open(data, GetConversationRequest.Parser);
                Reply(data.RequestID, data.Action, new GetConversationResponse { Conversation = Chats.Get(get.ConversationID) }, encrypt: true);
                break;

            case ActionType.ListContacts when RequestCrypto is not null:
                Reply(data.RequestID, data.Action, new ListContactsResponse { Contacts = { Chats.Contacts() } }, encrypt: true);
                break;

            case ActionType.ListTopContacts when RequestCrypto is not null:
                Reply(data.RequestID, data.Action, new ListTopContactsResponse { Contacts = { Chats.Contacts() } }, encrypt: true);
                break;

            case ActionType.GetOrCreateConversation when RequestCrypto is not null:
                var create = Open(data, GetOrCreateConversationRequest.Parser);
                Reply(data.RequestID, data.Action, new GetOrCreateConversationResponse { Conversation = Chats.FindOrCreate(create.Numbers), Status = GetOrCreateConversationResponse.Types.Status.Success }, encrypt: true);
                break;

            // Photos: Ann Has A 1x1 PNG, Everyone Else None
            case ActionType.GetParticipantsThumbnail when RequestCrypto is not null:
                var thumbnails = Open(data, GetThumbnailRequest.Parser);
                var photos     = new GetThumbnailResponse();
                if (thumbnails.Identifiers.Contains("p-ann"))
                {
                    photos.Thumbnail.Add(new GetThumbnailResponse.Types.Thumbnail { Identifier = "p-ann", Data = new ThumbnailData { ImageBuffer = ByteString.FromBase64(AnnPhotoPng) } });
                }

                Reply(data.RequestID, data.Action, photos, encrypt: true);
                break;

            // No Contact Photos: An Empty Thumbnail List
            case ActionType.GetContactsThumbnail when RequestCrypto is not null:
                Reply(data.RequestID, data.Action, new GetThumbnailResponse(), encrypt: true);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Pushes a message from the other side of a conversation, as a live update.
    /// </summary>
    /// <remarks>
    /// The conversation's snippet is updated and it becomes unread. A <c>ConversationEvent</c> with the updated
    /// conversation goes first, then the <c>MessageEvent</c>. With <paramref name="old"/>, both are put in the startup
    /// catch-up batch instead: the next long poll of a paired client counts them in its startup ack, so the client
    /// marks them old. The batch is only delivered at the start of a long poll, so a client that is already
    /// connected holds it until it reconnects: call <see cref="FakeGoogleServer.KillLongPoll"/> with 0 to end the
    /// current long poll and the client reopens one and receives the batch. Before the first connect, push after the
    /// client finished message arrived and before <see cref="Tap"/>.
    /// </remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="text">The message text.</param>
    /// <param name="old">True to send it in the catch-up batch.</param>
    /// <param name="replyToId">The ID of the message it replies to, or null for none.</param>
    /// <param name="type">The message type: 4 for RCS (the default), 2 for a downloaded MMS, 1 for SMS.</param>
    /// <returns>The message as pushed, for <see cref="PushStatusUpdate"/>.</returns>
    /// <exception cref="InvalidOperationException">Pairing has not reached the client finished message.</exception>
    public Message PushIncoming(string conversationId, string text, bool old = false, string? replyToId = null, long type = 4)
    {
        (Conversation conversation, Message message) = Chats.AddIncoming(conversationId, text, replyToId, type);
        Action<IncomingRPCMessage> push = old ? _pushCatchUp : _push;
        PushUpdate(new UpdateEvents { ConversationEvent = new ConversationEvent { Data = { conversation } } }, push);
        PushUpdate(new UpdateEvents { MessageEvent = new MessageEvent { Data = { message } } }, push);
        return message;
    }

    /// <summary>
    /// Pushes an already pushed message again with a new status, as a live update: the same message ID, as the real
    /// phone sends for status changes and reactions.
    /// </summary>
    /// <param name="message">The message, as <see cref="PushIncoming"/> returned it.</param>
    /// <param name="status">The new status.</param>
    /// <exception cref="KeyNotFoundException">The fake phone does not store the message.</exception>
    /// <exception cref="InvalidOperationException">Pairing has not reached the client finished message.</exception>
    public void PushStatusUpdate(Message message, MessageStatusType status)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Through The Store, So The New Status Sticks For Later Fetches
        PushStatus(message.ConversationID, message.MessageID, status);
    }

    /// <summary>
    /// Gives a stored message a new status and pushes it again, as a live update.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <param name="status">The new status.</param>
    /// <exception cref="KeyNotFoundException">The conversation has no such message.</exception>
    /// <exception cref="InvalidOperationException">Pairing has not reached the client finished message.</exception>
    public void PushStatus(string conversationId, string messageId, MessageStatusType status)
    {
        Message message = Chats.SetStatus(conversationId, messageId, status) ?? throw new KeyNotFoundException($"No message {messageId} in {conversationId}.");
        PushUpdate(new UpdateEvents { MessageEvent = new MessageEvent { Data = { message } } }, _push);
    }

    /// <summary>
    /// Adds reactions to a stored message and pushes it again, as a live update (the way the phone reports reactions).
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <param name="reactions">Each reaction's type and who reacted, in order.</param>
    /// <exception cref="KeyNotFoundException">The conversation has no such message.</exception>
    /// <exception cref="InvalidOperationException">Pairing has not reached the client finished message.</exception>
    public void PushReactions(string conversationId, string messageId, params (EmojiType Type, string ParticipantId)[] reactions)
    {
        Message message = Chats.AddReactions(conversationId, messageId, reactions) ?? throw new KeyNotFoundException($"No message {messageId} in {conversationId}.");
        PushUpdate(new UpdateEvents { MessageEvent = new MessageEvent { Data = { message } } }, _push);
    }

    /// <summary>
    /// Pushes a typing event from someone in a conversation, as a live update.
    /// </summary>
    /// <remarks>The sender is named by phone number, as the real phone does; the app maps it to a participant.</remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="number">The typing person's phone number.</param>
    /// <param name="started">True when they started typing, false when they stopped.</param>
    /// <exception cref="InvalidOperationException">Pairing has not reached the client finished message.</exception>
    public void PushTyping(string conversationId, string number, bool started)
    {
        var typing = new TypingData
        {
            ConversationID = conversationId,
            User           = new User { Number = number },
            Type           = started ? TypingTypes.StartedTyping : TypingTypes.StoppedTyping,
        };
        PushUpdate(new UpdateEvents { TypingEvent = new TypingEvent { Data = typing } }, _push);
    }

    /// <summary>
    /// Pushes a user alert, such as the phone's battery running low, as a live update.
    /// </summary>
    /// <param name="type">The alert type.</param>
    /// <exception cref="InvalidOperationException">Pairing has not reached the client finished message.</exception>
    public void PushAlert(AlertType type) =>
        PushUpdate(new UpdateEvents { UserAlertEvent = new UserAlertEvent { AlertType = type } }, _push);

    /// <summary>
    /// Moves a conversation to a new status on the phone's side, such as archiving it there, and pushes it as a live update.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="status">The new status.</param>
    /// <exception cref="KeyNotFoundException">The fake phone has no such conversation.</exception>
    /// <exception cref="InvalidOperationException">Pairing has not reached the client finished message.</exception>
    public void PushConversationStatus(string conversationId, ConversationStatus status)
    {
        Conversation conversation = Chats.SetConversationStatus(conversationId, status) ?? throw new KeyNotFoundException($"No conversation {conversationId}.");
        PushUpdate(new UpdateEvents { ConversationEvent = new ConversationEvent { Data = { conversation } } }, _push);
    }

    // Answers A Conversation Update, Applies It (Status Or Mute) And Sends The Conversation Again. Failed Or Unknown
    // Conversations Are Answered Without Success And Change Nothing
    private void OnUpdateConversation(OutgoingRPCData data)
    {
        var request = Open(data, UpdateConversationRequest.Parser);
        lock (_lock)
        {
            _conversationUpdates.Add(request);
        }

        if (FailConversationUpdates)
        {
            Reply(data.RequestID, data.Action, new UpdateConversationResponse { Success = false }, encrypt: true);
            return;
        }

        // Block, Unblock And Delete Ride On The Action; Archive And Unarchive On The Update Data's Status
        ConversationStatus? updated = request.UpdateData?.DataCase == UpdateConversationData.DataOneofCase.Status ? request.UpdateData.Status : null;
        ConversationStatus? status  = request.Action switch
        {
            ConversationActionStatus.Delete                                           => ConversationStatus.Deleted,
            ConversationActionStatus.Block or ConversationActionStatus.BlockAndReport => ConversationStatus.BlockedFolder,
            ConversationActionStatus.Unblock                                          => ConversationStatus.Active,
            _                                                                         => updated,
        };

        // Archive, Unarchive And Mute Carry The ID Only In The Update Data (LibGM's conversation_builder.go)
        string id                     = request.ConversationID.Length > 0 ? request.ConversationID : request.UpdateData?.ConversationID ?? "";
        ConversationMuteStatus? muted = request.UpdateData?.DataCase == UpdateConversationData.DataOneofCase.Mute ? request.UpdateData.Mute : null;
        Conversation? conversation    = Chats.SetConversationStatus(id, status, muted);
        Reply(data.RequestID, data.Action, new UpdateConversationResponse { Success = conversation is not null }, encrypt: true);
        if (conversation is not null)
        {
            PushUpdate(new UpdateEvents { ConversationEvent = new ConversationEvent { Data = { conversation } } }, _push);
        }
    }

    // Answers A Reaction, Then Sends The Message Again With Its Reactions Updated
    private void OnSendReaction(OutgoingRPCData data)
    {
        var request = Open(data, SendReactionRequest.Parser);
        lock (_lock)
        {
            _reactions.Add(request);
        }

        Message? message = Chats.React(request);
        Reply(data.RequestID, data.Action, new SendReactionResponse { Success = true }, encrypt: true);
        if (message is not null)
        {
            PushUpdate(new UpdateEvents { MessageEvent = new MessageEvent { Data = { message } } }, _push);
        }
    }

    // Answers A Send, Then Echoes It As A Live Message With The Request's Temporary ID
    private void OnSendMessage(OutgoingRPCData data)
    {
        var request = Open(data, SendMessageRequest.Parser);
        lock (_lock)
        {
            _sent.Add(request);
        }

        if (FailSends)
        {
            Reply(data.RequestID, data.Action, new SendMessageResponse { Status = SendMessageResponse.Types.Status.Failure2 }, encrypt: true);
            return;
        }

        string text     = SentText(request);
        Message message = Chats.AddOutgoing(request.ConversationID, text, request.TmpID, request.Reply?.MessageID);
        Reply(data.RequestID, data.Action, new SendMessageResponse { Status = SendMessageResponse.Types.Status.Success }, encrypt: true);
        PushUpdate(new UpdateEvents { MessageEvent = new MessageEvent { Data = { message } } }, _push);
    }

    /// <summary>
    /// Reads the text of a sent message: the first <c>MessageInfo</c>'s content, where the app (like the bridge's
    /// <c>ConvertMatrixMessage</c>) puts it.
    /// </summary>
    /// <param name="request">The send request.</param>
    /// <returns>The text, or empty.</returns>
    public static string SentText(SendMessageRequest request) =>
        request?.MessagePayload?.MessageInfo.FirstOrDefault()?.MessageContent?.Content ?? "";

    // A Live Update: A GET_UPDATES Data Event That Answers No Request
    private void PushUpdate(UpdateEvents update, Action<IncomingRPCMessage> push) =>
        Reply(Guid.NewGuid().ToString(), ActionType.GetUpdates, update, encrypt: true, push);

    // The Request's Payload, Decrypted
    private T Open<T>(OutgoingRPCData data, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom(RequestCrypto!.Decrypt(data.EncryptedProtoData.Span));

    // =========================================================================
    // UKEY2 SERVER SIDE
    // =========================================================================

    // Keeps The Client Init And Its Commitment, Answers With A Server Init Using A Fresh Key
    private GaiaPairingResponseContainer OnClientInit(OutgoingRPCData data)
    {
        byte[] client_init     = GaiaPairingRequestContainer.Parser.ParseFrom(data.UnencryptedProtoData).Data.ToByteArray();
        Ukey2ClientInit parsed = Ukey2ClientInit.Parser.ParseFrom(Ukey2Message.Parser.ParseFrom(client_init).MessageData);
        byte[] commitment      = parsed.CipherCommitments.FirstOrDefault(c => c.HandshakeCipher == Ukey2HandshakeCipher.P256Sha512)?.Commitment.ToByteArray()
            ?? throw new InvalidDataException("No P-256/SHA-512 commitment in the client init.");

        // Server Init: Own Key, X/Y With A Leading Zero Byte Like Go
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ECParameters key = ecdh.ExportParameters(true);
        var server_init  = new Ukey2ServerInit
        {
            Version         = 1,
            Random          = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            HandshakeCipher = Ukey2HandshakeCipher.P256Sha512,
            PublicKey       = new GenericPublicKey
            {
                Type            = PublicKeyType.EcP256,
                EcP256PublicKey = new EcP256PublicKey { X = ByteString.CopyFrom([0, .. key.Q.X!]), Y = ByteString.CopyFrom([0, .. key.Q.Y!]) },
            },
        };
        byte[] message = new Ukey2Message { MessageType = Ukey2Message.Types.Type.ServerInit, MessageData = server_init.ToByteString() }.ToByteArray();

        // A New Attempt Forgets The Last One's Emoji, Held Answer And Signal
        lock (_lock)
        {
            _key        = key;
            _clientInit = client_init;
            _serverInit = message;
            _commitment = commitment;
            _heldFinish = null;
            ShownEmoji  = null;
            if (_finishReceived.Task.IsCompleted)
            {
                _finishReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return new GaiaPairingResponseContainer
        {
            Data                             = ByteString.CopyFrom(message),
            ConfirmedVerificationCodeVersion = 1,
            ConfirmedKeyDerivationVersion    = 1,
        };
    }

    // Checks The Commitment, Derives The Auth And Next Keys, Picks The Emoji, Holds The Answer For Tap
    private void OnClientFinished(OutgoingRPCData data)
    {
        byte[] finish = GaiaPairingRequestContainer.Parser.ParseFrom(data.UnencryptedProtoData).Data.ToByteArray();
        lock (_lock)
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA512.HashData(finish), _commitment))
            {
                throw new InvalidDataException("The client finished message does not match the client init's commitment.");
            }

            // Client Public Key
            EcP256PublicKey client = Ukey2ClientFinished.Parser.ParseFrom(Ukey2Message.Parser.ParseFrom(finish).MessageData).PublicKey.EcP256PublicKey;
            var client_point       = new ECPoint { X = StripPrefix(client.X), Y = StripPrefix(client.Y) };

            // Shared Secret: SHA-256 Of The Raw ECDH X Coordinate, From The Phone's Own Key
            byte[] shared_secret;
            using (var server = ECDiffieHellman.Create(_key))
            using (var peer = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = client_point }))
            using (ECDiffieHellmanPublicKey peer_public = peer.PublicKey)
            {
                shared_secret = SHA256.HashData(server.DeriveRawSecretAgreement(peer_public));
            }

            // Auth Key Picks The Emoji, Next Key Waits For The Tap
            byte[] info     = [.. _clientInit, .. _serverInit];
            byte[] auth_key = Hkdf(shared_secret, "UKEY2 v1 auth"u8.ToArray(), info);
            _nextKey        = Hkdf(shared_secret, "UKEY2 v1 next"u8.ToArray(), info);
            ShownEmoji      = PairingEmojis.Pick(BinaryPrimitives.ReadUInt32BigEndian(auth_key), 1);
            _heldFinish     = data.RequestID;

            // Derived Now, So A Catch-Up Message Can Be Encrypted Before The Tap
            _derivedCrypto  = DeriveRequestKeys(_nextKey);

            // Continuations Run Asynchronously, So Signaling Inside The Lock Is Safe
            _finishReceived.TrySetResult();
        }
    }

    // Key Derivation Version 1: Order Client And Server Keys By Java Hash, Then The "Ditto" HKDFs
    private static AesCtrHelper DeriveRequestKeys(byte[] nextKey)
    {
        byte[] client_key   = Hkdf(nextKey, EncryptionKeyInfo, "client"u8.ToArray());
        byte[] server_key   = Hkdf(nextKey, EncryptionKeyInfo, "server"u8.ToArray());
        bool client_first   = JavaHash.ByteHash(client_key) < JavaHash.ByteHash(server_key);
        byte[] concatenated = client_first ? [.. EncryptionKeyInfo, .. client_key, .. server_key] : [.. EncryptionKeyInfo, .. server_key, .. client_key];
        byte[] hash         = SHA256.HashData(concatenated);
        return new AesCtrHelper(Hkdf(hash, "Ditto salt 1"u8.ToArray(), "Ditto info 1"u8.ToArray()), Hkdf(hash, "Ditto salt 2"u8.ToArray(), "Ditto info 2"u8.ToArray()));
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // An Answer Routed To The Request: Data Event, Session ID = Request ID
    private void Reply(string requestId, ActionType action, IMessage answer, bool encrypt, Action<IncomingRPCMessage>? push = null)
    {
        byte[] bytes = answer.ToByteArray();
        var data     = new RPCMessageData { SessionID = requestId, Action = action, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 };
        if (encrypt)
        {
            data.EncryptedData = ByteString.CopyFrom((RequestCrypto ?? _derivedCrypto ?? throw new InvalidOperationException("Pairing has not reached the client finished message.")).Encrypt(bytes));
        }
        else
        {
            data.UnencryptedData = ByteString.CopyFrom(bytes);
        }

        (push ?? _push)(new IncomingRPCMessage
        {
            ResponseID  = Guid.NewGuid().ToString(),
            BugleRoute  = BugleRoute.DataEvent,
            MessageType = MessageType.BugleMessage,
            MessageData = data.ToByteString(),
        });
    }

    // HKDF-SHA256, 32 Bytes Out (Go: doHKDF)
    private static byte[] Hkdf(byte[] key, byte[] salt, byte[] info) => HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, info);

    // Go Sends Each Coordinate With A Leading Zero Byte
    private static byte[] StripPrefix(ByteString coordinate) => coordinate.Length == 33 ? coordinate.ToByteArray()[1..] : coordinate.ToByteArray();
}
