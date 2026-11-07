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
/// The phone mirrors a real phone in pair_google.go of mautrix-gmessages, seen from the other end.
/// The phone keeps the commitment of the client init. Its answer is a server init with a fresh P-256 key of the phone,
/// 32 random bytes, and verification and key-derivation versions 1 and 1.
/// The phone checks the client finished message against the commitment.
/// The phone computes the raw ECDH X coordinate from its own key and the public key of the client.
/// The shared secret is the SHA-256 of this coordinate.
/// HKDF over the secret, with the client init and server init messages as info, gives the auth key.
/// The first 4 bytes of the auth key pick <see cref="ShownEmoji"/>. The next key gives the request keys.
/// The finish answer waits for <see cref="Tap"/>.
/// <para>
/// Answers go onto the open long poll as data events. The session ID of an event is the request ID,
/// and the client matches answers with this ID.
/// After pairing, the phone answers <c>NOTIFY_DITTO_ACTIVITY</c> pings with an encrypted answer, as a real phone does,
/// so the app never reports the phone as not responding.
/// The HTTP reply only acks an unknown action, and the phone sends nothing back.
/// </para>
/// <para>
/// After pairing, the phone also keeps <see cref="Chats"/> and answers the requests of the app for them.
/// These are conversations by folder, messages, single conversations, contacts, top contacts, get-or-create,
/// read marks, and thumbnails, which are always none.
/// A send gets an answer, then an echo as a live message with the temporary ID of the request, unless
/// <see cref="FailSends"/> is set.
/// A reaction gets a success answer, then the phone sends the message again with the updated reactions.
/// Typing updates go to <see cref="Typing"/> and never get an answer.
/// A <c>SettingsEvent</c> with one SIM follows every <c>GET_UPDATES</c>.
/// </para>
/// <para>
/// <see cref="PushIncoming"/> sends a live message.
/// With <c>old</c>, it sends the message in the startup catch-up batch.
/// <see cref="PushStatusUpdate"/> and <see cref="PushStatus"/> send a message again with a new status.
/// A real phone does this several times per message.
/// <see cref="PushTyping"/> sends a typing event. <see cref="PushIncomingMedia"/> sends an attachment.
/// A full-size image request sends a thumbnail-only photo again with its full media ID.
/// </para>
/// <para>
/// The phone keeps conversation updates (archive, mute, block, delete) in <see cref="ConversationUpdates"/>,
/// answers them, applies them and sends the conversation again, unless <see cref="FailConversationUpdates"/> is set.
/// <see cref="PushConversationStatus"/> moves a conversation on the side of the phone.
/// <see cref="PushAlert"/> sends a user alert.
/// </para>
/// </remarks>
public sealed class FakePhone
{
    // Copied verbatim from encryptionKeyInfo in pair_google.go.
    private static readonly byte[] EncryptionKeyInfo = [130, 170, 85, 160, 211, 151, 248, 131, 70, 202, 28, 238, 141, 57, 9, 185, 95, 19, 250, 125, 235, 29, 74, 179, 131, 118, 184, 37, 109, 168, 85, 16];

    private readonly Lock _lock = new();
    private readonly Action<IncomingRPCMessage> _push;
    private readonly Action<IncomingRPCMessage> _pushCatchUp;
    private readonly List<SendMessageRequest> _sent  = [];
    private readonly List<MessageReadRequest> _reads = [];
    private readonly List<string> _deletes           = [];
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
    /// <param name="pushCatchUp">
    /// Puts a message in the batch that the next long poll starts with. The startup ack counts it.
    /// </param>
    internal FakePhone(Action<IncomingRPCMessage> push, Action<IncomingRPCMessage> pushCatchUp)
    {
        _push        = push;
        _pushCatchUp = pushCatchUp;
    }

    /// <summary>
    /// Gets the conversations, messages and contacts that the phone answers with.
    /// </summary>
    public FakeChats Chats { get; } = new();

    /// <summary>
    /// Gets the contact photos that the phone answers with, by participant ID. Ann and the owner of the phone have a
    /// 1x1 PNG (<see cref="FakeChats.BluePng"/>) unless a test changes it. Change it before the app asks, which is
    /// before pairing.
    /// </summary>
    public Dictionary<string, byte[]> ParticipantPhotos { get; } = new()
    {
        ["p-ann"]                  = Convert.FromBase64String(FakeChats.BluePng),
        [FakeChats.MyParticipantId] = Convert.FromBase64String(FakeChats.BluePng),
    };

    /// <summary>
    /// Gets or sets whether sends get a <c>FAILURE_2</c> answer and no echo.
    /// This tests failures without the 60 second timeout.
    /// </summary>
    public bool FailSends { get; set; }

    /// <summary>
    /// Gets or sets whether conversation updates get a <c>Success = false</c> answer and change nothing.
    /// </summary>
    public bool FailConversationUpdates { get; set; }

    /// <summary>
    /// Gets or sets how long every <c>ListMessages</c> answer waits before it sends. The default is zero.
    /// </summary>
    public TimeSpan MessagesDelay { get; set; }

    /// <summary>
    /// Gets or sets how much longer a <c>ListMessages</c> answer for an older page waits, in addition to
    /// <see cref="MessagesDelay"/>. A request for an older page carries a cursor. The default is zero.
    /// </summary>
    public TimeSpan OlderPagesDelay { get; set; }

    /// <summary>
    /// Gets or sets how long the phone waits after a full-size image request before it sends the photo again with its
    /// full media ID. The default is zero.
    /// </summary>
    public TimeSpan FullSizeDelay { get; set; }

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
    /// Gets every message ID that a <c>DeleteMessage</c> request named, in arrival order.
    /// </summary>
    public IReadOnlyList<string> Deletes
    {
        get
        {
            lock (_lock)
            {
                return [.. _deletes];
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
    /// Gets the emoji that the phone asks the user to tap. The value is null before the client finished message of
    /// the current attempt.
    /// </summary>
    public string? ShownEmoji { get; private set; }

    /// <summary>
    /// Gets a task that completes when the client finished message of the current attempt arrives and
    /// <see cref="ShownEmoji"/> is set.
    /// </summary>
    /// <remarks>
    /// Each client init starts a new attempt with a new task, so a test can also await a retried pairing.
    /// </remarks>
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
    /// Gets the request keys after a correct <see cref="Tap"/>. The value is null before the tap.
    /// </summary>
    /// <remarks>
    /// These are the same AES and HMAC keys that the client derives. The phone encrypts later answers with them.
    /// </remarks>
    public AesCtrHelper? RequestCrypto { get; private set; }

    /// <summary>
    /// Answers the held client finished message as if the user tapped an emoji on the phone.
    /// </summary>
    /// <remarks>
    /// A correct tap derives the request keys and answers with success. A wrong tap answers with
    /// <c>WRONG_VERIFICATION_CODE_SELECTED</c>.
    /// </remarks>
    /// <param name="correct">True when the user taps the emoji that the computer shows.</param>
    /// <exception cref="InvalidOperationException">No client finished message is waiting.</exception>
    public void Tap(bool correct)
    {
        string requestId;
        lock (_lock)
        {
            requestId   = _heldFinish ?? throw new InvalidOperationException("No pairing is waiting for a tap.");
            _heldFinish = null;
            if (correct)
            {
                RequestCrypto = _derivedCrypto;
            }
        }

        // Pairing answers are never encrypted.
        GaiaPairingResponseContainer answer = correct
            ? new GaiaPairingResponseContainer()
            : new GaiaPairingResponseContainer { FinishErrorType = 1, FinishErrorCode = GaiaPairingErrorCode.WrongVerificationCodeSelected };
        Reply(requestId, ActionType.CreateGaiaPairingClientFinished, answer, encrypt: false);
    }

    /// <summary>
    /// Handles one request that the client sent through <c>SendMessage</c>.
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

            // The ping answer keeps the client from reporting that the phone does not respond.
            case ActionType.NotifyDittoActivity when RequestCrypto is not null:
                Reply(data.RequestID, data.Action, new NotifyDittoActivityResponse(), encrypt: true);
                break;

            // Every update request, one per connect, gets the settings event right after it, as from a real phone.
            case ActionType.GetUpdates when RequestCrypto is not null:
                PushUpdate(new UpdateEvents { SettingsEvent = new Settings { SIMCards = { new SIMCard { SIMParticipant = new SIMParticipant { ID = FakeChats.MyParticipantId } } } } }, _push);
                break;

            case ActionType.ListConversations when RequestCrypto is not null:
                var list = Open(data, ListConversationsRequest.Parser);
                Reply(data.RequestID, data.Action, new ListConversationsResponse { Conversations = { Chats.List(list.Folder, list.Count, list.Cursor) } }, encrypt: true);
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

                // The delay runs off the request thread, so the phone answers other requests in the meantime.
                string requestId = data.RequestID;
                _ = Task.Delay(delay).ContinueWith(_ => Reply(requestId, ActionType.ListMessages, answer, encrypt: true), TaskScheduler.Default);
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

            // The client sends typing without waiting for an answer, so the phone never answers it.
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

            case ActionType.DeleteMessage when RequestCrypto is not null:
                var delete = Open(data, DeleteMessageRequest.Parser);
                lock (_lock)
                {
                    _deletes.Add(delete.MessageID);
                }

                Reply(data.RequestID, data.Action, new DeleteMessageResponse { Success = true }, encrypt: true);
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

            // Photos come from ParticipantPhotos, which holds Ann and the owner of the phone unless a test changes it.
            // Every other participant has none.
            case ActionType.GetParticipantsThumbnail when RequestCrypto is not null:
                var thumbnails = Open(data, GetThumbnailRequest.Parser);
                var photos     = new GetThumbnailResponse();
                foreach ((string withPhoto, byte[] image) in ParticipantPhotos)
                {
                    if (thumbnails.Identifiers.Contains(withPhoto))
                    {
                        photos.Thumbnail.Add(new GetThumbnailResponse.Types.Thumbnail { Identifier = withPhoto, Data = new ThumbnailData { ImageBuffer = ByteString.CopyFrom(image) } });
                    }
                }

                Reply(data.RequestID, data.Action, photos, encrypt: true);
                break;

            // The phone answers, then sends the thumbnail-only photo again with its full media ID.
            case ActionType.GetFullSizeImage when RequestCrypto is not null:
                var fullSize  = Open(data, GetFullSizeImageRequest.Parser);
                Message? full = Chats.ReleaseFullSize(fullSize.MessageID);
                Reply(data.RequestID, data.Action, new GetFullSizeImageResponse(), encrypt: true);
                if (full is not null)
                {
                    TimeSpan fullDelay = FullSizeDelay;
                    _ = Task.Delay(fullDelay).ContinueWith(_ => PushUpdate(new UpdateEvents { MessageEvent = new MessageEvent { Data = { full } } }, _push), TaskScheduler.Default);
                }

                break;

            // The phone has no contact photos, so the thumbnail list is empty.
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
    /// The snippet of the conversation is updated and the conversation becomes unread.
    /// A <c>ConversationEvent</c> with the updated conversation goes first, then the <c>MessageEvent</c>.
    /// <para>
    /// With <paramref name="old"/>, both events go into the startup catch-up batch instead.
    /// The startup ack of the next long poll of a paired client counts them, so the client marks them old.
    /// The phone delivers the batch only at the start of a long poll. A client that is already connected holds it
    /// until it reconnects. Call <see cref="FakeGoogleServer.KillLongPoll"/> with 0 to end the current long poll.
    /// The client then opens a new one and receives the batch.
    /// Before the first connect, push after the client finished message arrives and before <see cref="Tap"/>.
    /// </para>
    /// </remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="text">The message text.</param>
    /// <param name="old">True to send the message in the catch-up batch.</param>
    /// <param name="replyToId">The ID of the message that this one replies to, or null for none.</param>
    /// <param name="type">The message type: 4 for RCS (the default), 2 for a downloaded MMS, or 1 for SMS.</param>
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
    /// Pushes an attachment from the other side of a conversation, as a live update.
    /// </summary>
    /// <remarks>
    /// The phone encrypts the file with <see cref="MediaCrypto"/> and a fresh key and stores it, so the client can
    /// download it from <c>/upload</c>. With <paramref name="withThumbnail"/>, the message carries only a thumbnail.
    /// The thumbnail has the same bytes, under its own ID and key.
    /// <c>GET_FULL_SIZE_IMAGE</c> then sends the message again with the full media ID, as a real phone does.
    /// A <c>ConversationEvent</c> goes first, then the <c>MessageEvent</c>.
    /// </remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="mime">The mime type of the file, such as <c>image/png</c>.</param>
    /// <param name="bytes">The bytes of the file.</param>
    /// <param name="fileName">The file name.</param>
    /// <param name="withThumbnail">
    /// True to send a thumbnail only and hold the full media ID until the client asks for it.
    /// </param>
    /// <param name="caption">A caption to send with the file, or null for none.</param>
    /// <param name="dimensions">
    /// The width and height of the media as the phone reports them.
    /// Null selects the own size of a PNG, and none for other formats.
    /// </param>
    /// <returns>The message as pushed.</returns>
    /// <exception cref="InvalidOperationException">Pairing has not reached the client finished message.</exception>
    public Message PushIncomingMedia(string conversationId, string mime, byte[] bytes, string fileName, bool withThumbnail = false, string? caption = null, Dimensions? dimensions = null)
    {
        (Conversation conversation, Message message) = Chats.AddIncomingMedia(conversationId, mime, bytes, fileName, withThumbnail, caption, dimensions);
        PushUpdate(new UpdateEvents { ConversationEvent = new ConversationEvent { Data = { conversation } } }, _push);
        PushUpdate(new UpdateEvents { MessageEvent = new MessageEvent { Data = { message } } }, _push);
        return message;
    }

    /// <summary>
    /// Pushes an already pushed message again with a new status, as a live update.
    /// The message ID stays the same, as the real phone sends it for status changes and reactions.
    /// </summary>
    /// <param name="message">The message that <see cref="PushIncoming"/> returned.</param>
    /// <param name="status">The new status.</param>
    /// <exception cref="KeyNotFoundException">The fake phone does not store the message.</exception>
    /// <exception cref="InvalidOperationException">Pairing has not reached the client finished message.</exception>
    public void PushStatusUpdate(Message message, MessageStatusType status)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The call goes through the store, so the new status stays for later fetches.
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
    /// Adds reactions to a stored message and pushes it again, as a live update. The phone reports reactions this way.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <param name="reactions">The type of each reaction and the person who reacted, in order.</param>
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
    /// <remarks>
    /// The event names the sender by phone number, as the real phone does. The app maps the number to a participant.
    /// </remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="number">The phone number of the typing person.</param>
    /// <param name="started">True when the person starts typing, false when the person stops.</param>
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
    /// Pushes a user alert, such as a low battery of the phone, as a live update.
    /// </summary>
    /// <param name="type">The alert type.</param>
    /// <exception cref="InvalidOperationException">Pairing has not reached the client finished message.</exception>
    public void PushAlert(AlertType type) =>
        PushUpdate(new UpdateEvents { UserAlertEvent = new UserAlertEvent { AlertType = type } }, _push);

    /// <summary>
    /// Moves a conversation to a new status on the side of the phone, such as archiving it there.
    /// Pushes the conversation as a live update.
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

    // Answers a conversation update, applies the status or mute change and sends the conversation again.
    // A failed update or an unknown conversation gets an answer without success and changes nothing.
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

        // Block, unblock and delete use the action. Archive and unarchive use the status in the update data.
        ConversationStatus? updated = request.UpdateData?.DataCase == UpdateConversationData.DataOneofCase.Status ? request.UpdateData.Status : null;
        ConversationStatus? status  = request.Action switch
        {
            ConversationActionStatus.Delete                                           => ConversationStatus.Deleted,
            ConversationActionStatus.Block or ConversationActionStatus.BlockAndReport => ConversationStatus.BlockedFolder,
            ConversationActionStatus.Unblock                                          => ConversationStatus.Active,
            _                                                                         => updated,
        };

        // Archive, unarchive and mute carry the conversation ID only in the update data.
        // See conversation_builder.go of LibGM.
        string id                     = request.ConversationID.Length > 0 ? request.ConversationID : request.UpdateData?.ConversationID ?? "";
        ConversationMuteStatus? muted = request.UpdateData?.DataCase == UpdateConversationData.DataOneofCase.Mute ? request.UpdateData.Mute : null;
        Conversation? conversation    = Chats.SetConversationStatus(id, status, muted);
        Reply(data.RequestID, data.Action, new UpdateConversationResponse { Success = conversation is not null }, encrypt: true);
        if (conversation is not null)
        {
            PushUpdate(new UpdateEvents { ConversationEvent = new ConversationEvent { Data = { conversation } } }, _push);
        }
    }

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

    // Answers a send, then echoes it as a live message with the temporary ID of the request.
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
        Message message = Chats.AddOutgoing(request.ConversationID, text, request.TmpID, request.Reply?.MessageID, request.MessagePayload?.MessageInfo);
        Reply(data.RequestID, data.Action, new SendMessageResponse { Status = SendMessageResponse.Types.Status.Success }, encrypt: true);
        PushUpdate(new UpdateEvents { MessageEvent = new MessageEvent { Data = { message } } }, _push);
    }

    /// <summary>
    /// Reads the text of a sent message from the content of the first <c>MessageInfo</c>. The app puts the text there,
    /// as <c>ConvertMatrixMessage</c> of the bridge does.
    /// </summary>
    /// <param name="request">The send request.</param>
    /// <returns>The text, or an empty string.</returns>
    public static string SentText(SendMessageRequest request) =>
        request?.MessagePayload?.MessageInfo.FirstOrDefault()?.MessageContent?.Content ?? "";

    // A live update is a GET_UPDATES data event that answers no request.
    private void PushUpdate(UpdateEvents update, Action<IncomingRPCMessage> push) =>
        Reply(Guid.NewGuid().ToString(), ActionType.GetUpdates, update, encrypt: true, push);

    private T Open<T>(OutgoingRPCData data, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom(RequestCrypto!.Decrypt(data.EncryptedProtoData.Span));

    // =========================================================================
    // UKEY2 SERVER SIDE
    // =========================================================================

    // Keeps the client init and its commitment. Answers with a server init that uses a fresh key.
    private GaiaPairingResponseContainer OnClientInit(OutgoingRPCData data)
    {
        byte[] clientInit      = GaiaPairingRequestContainer.Parser.ParseFrom(data.UnencryptedProtoData).Data.ToByteArray();
        Ukey2ClientInit parsed = Ukey2ClientInit.Parser.ParseFrom(Ukey2Message.Parser.ParseFrom(clientInit).MessageData);
        byte[] commitment      = parsed.CipherCommitments.FirstOrDefault(c => c.HandshakeCipher == Ukey2HandshakeCipher.P256Sha512)?.Commitment.ToByteArray()
            ?? throw new InvalidDataException("No P-256/SHA-512 commitment in the client init.");

        // The server init holds the own key of the phone. X and Y have a leading zero byte, like the Go code.
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ECParameters key = ecdh.ExportParameters(true);
        var serverInit   = new Ukey2ServerInit
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
        byte[] message = new Ukey2Message { MessageType = Ukey2Message.Types.Type.ServerInit, MessageData = serverInit.ToByteString() }.ToByteArray();

        // A new attempt forgets the emoji, the held answer and the signal of the last attempt.
        lock (_lock)
        {
            _key        = key;
            _clientInit = clientInit;
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

    // Checks the commitment, derives the auth key and the next key, picks the emoji and holds the answer for Tap.
    private void OnClientFinished(OutgoingRPCData data)
    {
        byte[] finish = GaiaPairingRequestContainer.Parser.ParseFrom(data.UnencryptedProtoData).Data.ToByteArray();
        lock (_lock)
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA512.HashData(finish), _commitment))
            {
                throw new InvalidDataException("The client finished message does not match the client init's commitment.");
            }

            EcP256PublicKey client = Ukey2ClientFinished.Parser.ParseFrom(Ukey2Message.Parser.ParseFrom(finish).MessageData).PublicKey.EcP256PublicKey;
            var clientPoint        = new ECPoint { X = StripPrefix(client.X), Y = StripPrefix(client.Y) };

            // The shared secret is the SHA-256 of the raw ECDH X coordinate, computed from the own key of the phone.
            byte[] sharedSecret;
            using (var server = ECDiffieHellman.Create(_key))
            using (var peer = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = clientPoint }))
            using (ECDiffieHellmanPublicKey peerPublic = peer.PublicKey)
            {
                sharedSecret = SHA256.HashData(server.DeriveRawSecretAgreement(peerPublic));
            }

            // The auth key picks the emoji. The next key waits for the tap.
            byte[] info     = [.. _clientInit, .. _serverInit];
            byte[] authKey  = Hkdf(sharedSecret, "UKEY2 v1 auth"u8.ToArray(), info);
            _nextKey        = Hkdf(sharedSecret, "UKEY2 v1 next"u8.ToArray(), info);
            ShownEmoji      = PairingEmojis.Pick(BinaryPrimitives.ReadUInt32BigEndian(authKey), 1);
            _heldFinish     = data.RequestID;

            // The phone derives the keys now, so it can encrypt a catch-up message before the tap.
            _derivedCrypto  = DeriveRequestKeys(_nextKey);

            // Continuations run asynchronously, so the signal inside the lock is safe.
            _finishReceived.TrySetResult();
        }
    }

    // Key derivation version 1 orders the client key and the server key by Java hash. The "Ditto" HKDFs follow.
    private static AesCtrHelper DeriveRequestKeys(byte[] nextKey)
    {
        byte[] clientKey    = Hkdf(nextKey, EncryptionKeyInfo, "client"u8.ToArray());
        byte[] serverKey    = Hkdf(nextKey, EncryptionKeyInfo, "server"u8.ToArray());
        bool clientFirst    = JavaHash.ByteHash(clientKey) < JavaHash.ByteHash(serverKey);
        byte[] concatenated = clientFirst ? [.. EncryptionKeyInfo, .. clientKey, .. serverKey] : [.. EncryptionKeyInfo, .. serverKey, .. clientKey];
        byte[] hash         = SHA256.HashData(concatenated);
        return new AesCtrHelper(Hkdf(hash, "Ditto salt 1"u8.ToArray(), "Ditto info 1"u8.ToArray()), Hkdf(hash, "Ditto salt 2"u8.ToArray(), "Ditto info 2"u8.ToArray()));
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // An answer routed to the request is a data event whose session ID is the request ID.
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

    // HKDF-SHA256 with 32 bytes of output (doHKDF in Go).
    private static byte[] Hkdf(byte[] key, byte[] salt, byte[] info) => HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, info);

    // Go sends each coordinate with a leading zero byte.
    private static byte[] StripPrefix(ByteString coordinate) => coordinate.Length == 33 ? coordinate.ToByteArray()[1..] : coordinate.ToByteArray();
}
