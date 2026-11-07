using System.Collections.Frozen;
using System.Security.Cryptography;
using GMessages.Protocol.Http;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.Rpc;

/// <summary>
/// What to send to the phone and how.
/// </summary>
/// <remarks>Ported from <c>SendMessageParams</c> in session_handler.go.</remarks>
/// <param name="Action">The action for the phone to run.</param>
/// <param name="Data">The request payload, or null for none.</param>
/// <param name="RequestId">A fixed request ID. Null or empty makes a new one.</param>
/// <param name="OmitTtl">True to leave out the TTL.</param>
/// <param name="CustomTtlMicros">A TTL in microseconds that replaces the Tachyon TTL. 0 means not set.</param>
/// <param name="DontEncrypt">True to send the payload unencrypted.</param>
/// <param name="MessageType">The message type. The request sends unknown as bugle message.</param>
/// <param name="UserInitiated">True when a person waits. This turns on the early "phone is slow" notice.</param>
public sealed record SendParams(
    ActionType Action,
    IMessage? Data = null,
    string? RequestId = null,
    bool OmitTtl = false,
    long CustomTtlMicros = 0,
    bool DontEncrypt = false,
    MessageType MessageType = MessageType.BugleMessage,
    bool UserInitiated = false);

/// <summary>
/// Sends encrypted requests to the phone and matches the answers that come back over the long poll.
/// </summary>
/// <remarks>
/// Ported from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/session_handler.go">session_handler.go</see>
/// and <c>decryptInternalMessage</c> / <c>responseType</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/event_handler.go">event_handler.go</see>.
/// A request goes out over HTTP, but its answer comes on the long poll. Thus each request registers a waiter by its
/// request ID before the send. All waits use the <see cref="TimeProvider"/>. The acks for received messages go in a
/// queue. <see cref="FlushAcksAsync"/> sends them in a batch. The caller decides when. Go does it every 5 seconds.
/// The session never logs request contents, answer contents, tokens or keys. It logs only request IDs and actions.
/// </remarks>
/// <param name="http">The HTTP client.</param>
/// <param name="auth">The account whose keys, devices and token are used.</param>
/// <param name="endpoints">The server addresses.</param>
/// <param name="time">The clock for every timeout.</param>
/// <param name="log">The logger.</param>
public sealed partial class RpcSession(GmHttpClient http, AuthData auth, GmEndpoints endpoints, TimeProvider time, ILogger log)
{
    /// <summary>The longest wait for the phone. This is Go's <c>responseHardTimeout</c>.</summary>
    public static readonly TimeSpan ResponseHardTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The wait before the session tells listeners that the phone is slow. This is Go's <c>pingShortCircuitTimeout</c>.</summary>
    public static readonly TimeSpan PhoneSlowTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The maximum number of acks kept for a retry. This is Go's <c>maxQueuedAcks</c>.</summary>
    public const int MaxQueuedAcks = 1024;

    // Port of responseType.
    private static readonly FrozenDictionary<ActionType, MessageParser> ResponseTypesMap = new Dictionary<ActionType, MessageParser>
    {
        [ActionType.IsBugleDefault]           = IsBugleDefaultResponse.Parser,
        [ActionType.GetUpdates]               = UpdateEvents.Parser,
        [ActionType.ListConversations]        = ListConversationsResponse.Parser,
        [ActionType.NotifyDittoActivity]      = NotifyDittoActivityResponse.Parser,
        [ActionType.GetConversationType]      = GetConversationTypeResponse.Parser,
        [ActionType.GetConversation]          = GetConversationResponse.Parser,
        [ActionType.ListMessages]             = ListMessagesResponse.Parser,
        [ActionType.SendMessage]              = SendMessageResponse.Parser,
        [ActionType.SendReaction]             = SendReactionResponse.Parser,
        [ActionType.DeleteMessage]            = DeleteMessageResponse.Parser,
        [ActionType.GetParticipantsThumbnail] = GetThumbnailResponse.Parser,
        [ActionType.GetContactsThumbnail]     = GetThumbnailResponse.Parser,
        [ActionType.ListContacts]             = ListContactsResponse.Parser,
        [ActionType.ListTopContacts]          = ListTopContactsResponse.Parser,
        [ActionType.GetOrCreateConversation]  = GetOrCreateConversationResponse.Parser,
        [ActionType.UpdateConversation]       = UpdateConversationResponse.Parser,
        [ActionType.GetFullSizeImage]         = GetFullSizeImageResponse.Parser,
        [ActionType.PreFetchContacts]         = ListContactsResponse.Parser,
        [ActionType.FetchBlockedContacts]     = ListContactsResponse.Parser,
    }.ToFrozenDictionary();

    private readonly Lock _waitersLock = new();
    private readonly Dictionary<string, TaskCompletionSource<IncomingRpc>> _waiters = [];
    private readonly Lock _acksLock = new();
    private readonly List<string> _acks = [];

    /// <summary>
    /// Occurs 1 time for each user-initiated request when the phone does not answer in 5 seconds.
    /// </summary>
    /// <remarks>Go pokes its pinger here. The pinger then sends a ping. When the ping also fails, it reports "phone not responding".</remarks>
    public event EventHandler? PhoneSlow;

    /// <summary>
    /// Gets the session ID that each request carries. As in Go, it is empty until a call to <see cref="ResetSessionId"/>.
    /// </summary>
    public string SessionId { get; internal set; } = "";

    /// <summary>
    /// Gets or sets a check that tells if a long poll runs, so an answer can come. Null skips the check.
    /// </summary>
    /// <remarks>
    /// C#-only. <see cref="GmClient"/> points it at its long-poll loop. When the check returns false, a request that
    /// waits for an answer fails at once with <see cref="ConnectionClosedException"/>. It does not wait 60 seconds
    /// for an answer that cannot come. The check returns true while the loop reconnects, so a short reconnect does
    /// not fail requests.
    /// </remarks>
    internal Func<bool>? IsListening { get; set; }

    /// <summary>
    /// Gets the response types by action.
    /// </summary>
    /// <remarks>Tests compare it with the Go table.</remarks>
    internal static IReadOnlyDictionary<ActionType, MessageParser> ResponseTypes => ResponseTypesMap;

    // =========================================================================
    // SENDING
    // =========================================================================

    /// <summary>
    /// Sends a request and waits for the answer of the phone.
    /// </summary>
    /// <remarks>
    /// After 60 seconds, the method stops with <see cref="PhoneNotRespondingException"/>. For a user-initiated
    /// request, <see cref="PhoneSlow"/> occurs 1 time after 5 seconds with no answer, and the wait continues. When no
    /// long poll runs, the method fails at once and does not send.
    /// </remarks>
    /// <param name="p">What to send.</param>
    /// <param name="ct">Cancels the wait and removes the waiter.</param>
    /// <returns>The phone's answer.</returns>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="GmRequestException">The server refused the request.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<IncomingRpc> SendAsync(SendParams p, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(p);

        (string requestId, TaskCompletionSource<IncomingRpc> waiter) = await PostWithWaiterAsync(p, ct).ConfigureAwait(false);

        using var hard   = new CancellationTokenSource(ResponseHardTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, hard.Token);
        using var slow   = new CancellationTokenSource();
        if (p.UserInitiated)
        {
            _ = RaisePhoneSlowAsync(slow.Token);
        }

        try
        {
            return await waiter.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && hard.IsCancellationRequested)
        {
            CancelWaiter(requestId);
            throw new PhoneNotRespondingException($"Phone did not respond to request in {ResponseHardTimeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException)
        {
            CancelWaiter(requestId);
            throw;
        }
        finally
        {
            // The slow notice timer stops when the request ends. This is Go's defer shortCircuitTimer.Stop().
            await slow.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a request and returns when the server accepts it. The returned task completes with the answer of the phone.
    /// </summary>
    /// <remarks>
    /// As in Go, the inner task has no timeout. It ends with the answer, with <see cref="FailAllWaiters"/>, or when
    /// <paramref name="ct"/> is canceled. When no long poll runs, the method fails at once and does not send.
    /// </remarks>
    /// <param name="p">What to send.</param>
    /// <param name="ct">Cancels the send and then the wait.</param>
    /// <returns>A task that completes when the server accepts the request. Its result is the task for the answer.</returns>
    /// <exception cref="ConnectionClosedException">Not connected.</exception>
    /// <exception cref="GmRequestException">The server refused the request.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<Task<IncomingRpc>> SendNoWaitAsync(SendParams p, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(p);

        (string requestId, TaskCompletionSource<IncomingRpc> waiter) = await PostWithWaiterAsync(p, ct).ConfigureAwait(false);
        return WaitCancellableAsync(requestId, waiter, ct);
    }

    /// <summary>
    /// Sends a request and does not wait for an answer.
    /// </summary>
    /// <remarks>Use it for requests that the phone never answers, such as the first pairing step.</remarks>
    /// <param name="p">What to send.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>A task that completes when the server has accepted the request.</returns>
    /// <exception cref="GmRequestException">The server refused the request.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task SendNoResponseAsync(SendParams p, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(p);

        OutgoingRPCMessage payload = Build(p, out string requestId);
        LogSending(log, p.Action, requestId, false);
        await http.PostTypedAsync<OutgoingRPCResponse>(SendUrl, payload, GmContentType.PbLite, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the request the phone receives.
    /// </summary>
    /// <remarks>
    /// Ported from <c>buildMessage</c>. The keys of the account encrypt the payload, unless
    /// <see cref="SendParams.DontEncrypt"/> is set. The TTL is the custom TTL when set. Otherwise it is the Tachyon
    /// TTL, unless <see cref="SendParams.OmitTtl"/> is set.
    /// </remarks>
    /// <param name="p">What to send.</param>
    /// <param name="requestId">The request ID. The method makes a new one when <paramref name="p"/> has none.</param>
    /// <returns>The message to POST.</returns>
    internal OutgoingRPCMessage Build(SendParams p, out string requestId)
    {
        requestId = string.IsNullOrEmpty(p.RequestId) ? Guid.NewGuid().ToString() : p.RequestId;
        MessageType messageType  = p.MessageType == MessageType.UnknownMessageType ? MessageType.BugleMessage : p.MessageType;
        TachyonToken tachyon     = auth.Tachyon;

        var message = new OutgoingRPCMessage
        {
            Mobile = auth.Mobile,
            Data   = new OutgoingRPCMessage.Types.Data
            {
                RequestID       = requestId,
                BugleRoute      = BugleRoute.DataEvent,
                MessageTypeData = new OutgoingRPCMessage.Types.Data.Types.Type { EmptyArr = new EmptyArr(), MessageType = messageType },
            },
            Auth = new OutgoingRPCMessage.Types.Auth
            {
                RequestID        = requestId,
                TachyonAuthToken = ByteString.CopyFrom(tachyon.Token ?? []),
                ConfigVersion    = GmConstants.ConfigVersion,
            },
        };
        if (auth.IsGoogleAccount)
        {
            message.DestRegistrationIDs.Add(auth.DestRegId.ToString());
        }

        if (p.CustomTtlMicros != 0)
        {
            message.TTL = p.CustomTtlMicros;
        }
        else if (!p.OmitTtl)
        {
            message.TTL = tachyon.TtlMicros;
        }

        ByteString unencrypted = ByteString.Empty;
        ByteString encrypted   = ByteString.Empty;
        if (p.Data is not null)
        {
            byte[] serialized = p.Data.ToByteArray();
            if (p.DontEncrypt)
            {
                unencrypted = ByteString.CopyFrom(serialized);
            }
            else
            {
                encrypted = ByteString.CopyFrom(auth.RequestCrypto.Encrypt(serialized));
            }
        }

        message.Data.MessageData = new OutgoingRPCData
        {
            RequestID            = requestId,
            Action               = p.Action,
            UnencryptedProtoData = unencrypted,
            EncryptedProtoData   = encrypted,
            SessionID            = SessionId,
        }.ToByteString();

        return message;
    }

    /// <summary>
    /// Starts a new session ID.
    /// </summary>
    /// <remarks>Ported from <c>ResetSessionID</c>. Call it when a new connection starts.</remarks>
    internal void ResetSessionId() => SessionId = Guid.NewGuid().ToString();

    // =========================================================================
    // RECEIVING
    // =========================================================================

    /// <summary>
    /// Gives an incoming message to the request that waits for it, if there is one.
    /// </summary>
    /// <remarks>
    /// Ported from <c>receiveResponse</c>. When the account has cookies, the method ignores messages with unencrypted
    /// data and no encrypted data. These odd messages come before the real answers. Go calls this check "very hacky".
    /// The method does not ignore the 2 Google pairing actions.
    /// </remarks>
    /// <param name="msg">The decoded incoming message.</param>
    /// <returns>True when a waiting request took the message.</returns>
    internal bool TryDeliverResponse(IncomingRpc msg)
    {
        ArgumentNullException.ThrowIfNull(msg);

        if (msg.Message is not { } data)
        {
            return false;
        }

        // Length > 0 replaces the Go check != nil on bytes fields. Go sees a present field with 0 length as not nil.
        // Here that field looks absent. Real payloads are never empty.
        bool isPairingAction = data.Action is ActionType.CreateGaiaPairingClientInit or ActionType.CreateGaiaPairingClientFinished;
        if (auth.HasCookies && !isPairingAction && data.UnencryptedData.Length > 0 && data.EncryptedData.Length == 0)
        {
            return false;
        }

        // As in Go, the SessionID field of the answer holds the request ID.
        TaskCompletionSource<IncomingRpc>? waiter;
        lock (_waitersLock)
        {
            if (!_waiters.Remove(data.SessionID, out waiter))
            {
                return false;
            }
        }

        LogReceived(log, data.SessionID, msg.Raw.ResponseID, data.Action);
        return waiter.TrySetResult(msg);
    }

    /// <summary>
    /// Fails every waiting request with <see cref="ConnectionClosedException"/>.
    /// </summary>
    /// <remarks>Ported from <c>cancelAllResponseWaiters</c>. Call it when the client disconnects.</remarks>
    internal void FailAllWaiters()
    {
        TaskCompletionSource<IncomingRpc>[] pending;
        lock (_waitersLock)
        {
            pending = [.. _waiters.Values];
            _waiters.Clear();
        }

        foreach (TaskCompletionSource<IncomingRpc> waiter in pending)
        {
            waiter.TrySetException(new ConnectionClosedException());
        }
    }

    /// <summary>
    /// Decodes an incoming message. It decodes the inner payload by route, then decrypts data events.
    /// </summary>
    /// <remarks>
    /// Ported from <c>decryptInternalMessage</c>. For data events, the action gives the response type. The method
    /// parses the decrypted bytes as that type, when there is one. Some messages have a second encrypted field. The
    /// method decrypts it and checks that it parses, but raises nothing. Go fires a fake account-change event there.
    /// </remarks>
    /// <param name="raw">The message from the long poll.</param>
    /// <returns>The decoded message.</returns>
    /// <exception cref="InvalidDataException">The route is unknown or a payload could not be decoded or decrypted.</exception>
    internal IncomingRpc Decode(IncomingRPCMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        switch (raw.BugleRoute)
        {
            case BugleRoute.PairEvent:
                _ = Parse(RPCPairData.Parser, raw.MessageData.ToByteArray(), "pair event");
                return new IncomingRpc { Raw = raw };
            case BugleRoute.GaiaEvent:
                _ = Parse(RPCGaiaData.Parser, raw.MessageData.ToByteArray(), "gaia event");
                return new IncomingRpc { Raw = raw };
            case BugleRoute.DataEvent:
                return DecodeDataEvent(raw);
            default:
                throw new InvalidDataException($"Unknown bugle route {(int)raw.BugleRoute}.");
        }
    }

    // =========================================================================
    // ACKS
    // =========================================================================

    /// <summary>
    /// Puts an ack for a received message in the queue, unless the queue already has it.
    /// </summary>
    /// <remarks>Ported from <c>queueMessageAck</c>.</remarks>
    /// <param name="responseId">The response ID of the message to ack.</param>
    internal void QueueAck(string responseId)
    {
        lock (_acksLock)
        {
            if (!_acks.Contains(responseId))
            {
                _acks.Add(responseId);
            }
        }
    }

    /// <summary>
    /// Sends all queued acks in one request.
    /// </summary>
    /// <remarks>
    /// Ported from <c>sendAckRequest</c>. On a failure, the batch goes back in front of newer acks. Thus messages with
    /// no ack do not stop the delivery. When the queue then has more than 1024 acks, the method drops the batch. The
    /// method logs failures and does not throw them. It throws only when <paramref name="ct"/> is canceled.
    /// </remarks>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>A task that completes when the acks are sent or back in the queue.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task FlushAcksAsync(CancellationToken ct)
    {
        string[] batch;
        lock (_acksLock)
        {
            if (_acks.Count == 0)
            {
                return;
            }

            batch = [.. _acks];
            _acks.Clear();
        }

        var payload = new AckMessageRequest
        {
            AuthData = new AuthMessage
            {
                RequestID        = Guid.NewGuid().ToString(),
                TachyonAuthToken = ByteString.CopyFrom(auth.TachyonAuthToken ?? []),
                Network          = auth.AuthNetwork,
                ConfigVersion    = GmConstants.ConfigVersion,
            },
            EmptyArr = new EmptyArr(),
        };
        payload.Acks.AddRange(batch.Select(id => new AckMessageRequest.Types.Message { RequestID = id, Device = auth.Browser }));

        Uri url = auth.HasCookies ? endpoints.AckMessagesGoogle : endpoints.AckMessages;
        try
        {
            await http.PostTypedAsync<OutgoingRPCResponse>(url, payload, GmContentType.PbLite, ct).ConfigureAwait(false);
            LogAcksSent(log, batch.Length);
        }
        catch (Exception ex)
        {
            bool requeued;
            lock (_acksLock)
            {
                requeued = batch.Length + _acks.Count <= MaxQueuedAcks;
                if (requeued)
                {
                    _acks.InsertRange(0, batch);
                }
            }

            // On a cancel, throw the documented OperationCanceledException, not the error of the aborted socket.
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("The ack flush was canceled.", ex, ct);
            }

            LogAckFailed(log, ex, batch.Length, requeued);
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private Uri SendUrl => auth.HasCookies ? endpoints.SendMessageGoogle : endpoints.SendMessage;

    // Port of sendAsyncMessageWithID. The waiter must exist before the send.
    private async Task<(string RequestId, TaskCompletionSource<IncomingRpc> Waiter)> PostWithWaiterAsync(SendParams p, CancellationToken ct)
    {
        if (IsListening?.Invoke() == false)
        {
            throw new ConnectionClosedException("Not connected: no long poll is running to receive the answer.");
        }

        OutgoingRPCMessage payload = Build(p, out string requestId);

        var waiter = new TaskCompletionSource<IncomingRpc>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waitersLock)
        {
            _waiters[requestId] = waiter;
        }

        LogSending(log, p.Action, requestId, true);
        try
        {
            await http.PostTypedAsync<OutgoingRPCResponse>(SendUrl, payload, GmContentType.PbLite, ct).ConfigureAwait(false);
        }
        catch
        {
            CancelWaiter(requestId);
            throw;
        }

        return (requestId, waiter);
    }

    // Port of cancelResponse. It cancels the waiter only when no answer took it.
    private void CancelWaiter(string requestId)
    {
        TaskCompletionSource<IncomingRpc>? waiter;
        lock (_waitersLock)
        {
            _waiters.Remove(requestId, out waiter);
        }

        waiter?.TrySetCanceled();
    }

    private async Task<IncomingRpc> WaitCancellableAsync(string requestId, TaskCompletionSource<IncomingRpc> waiter, CancellationToken ct)
    {
        try
        {
            return await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CancelWaiter(requestId);
            throw;
        }
    }

    private async Task RaisePhoneSlowAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(PhoneSlowTimeout, time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            PhoneSlow?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogPhoneSlowHandlerFailed(log, ex);
        }
    }

    private IncomingRpc DecodeDataEvent(IncomingRPCMessage raw)
    {
        RPCMessageData message = Parse(RPCMessageData.Parser, raw.MessageData.ToByteArray(), "data event");

        MessageParser? parser       = ResponseTypesMap.GetValueOrDefault(message.Action);
        IMessage? decryptedMessage  = parser?.ParseFrom([]);
        byte[]? decryptedData       = null;

        if (message.EncryptedData.Length > 0)
        {
            decryptedData = Decrypt(message.EncryptedData, "data event");
            if (parser is not null)
            {
                decryptedMessage = Parse(parser, decryptedData, "decrypted data event");
            }
        }
        else if (message.EncryptedData2.Length > 0)
        {
            decryptedData = Decrypt(message.EncryptedData2, "field 2 in data event");
            Parse(EncryptedData2Container.Parser, decryptedData, "decrypted field 2 data event");
        }

        return new IncomingRpc { Raw = raw, Message = message, DecryptedData = decryptedData, DecryptedMessage = decryptedMessage };
    }

    private byte[] Decrypt(ByteString data, string what)
    {
        try
        {
            return auth.RequestCrypto.Decrypt(data.Span);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException($"Failed to decrypt {what}.", ex);
        }
    }

    private static T Parse<T>(MessageParser<T> parser, byte[] data, string what) where T : IMessage<T> =>
        (T)Parse((MessageParser)parser, data, what);

    private static IMessage Parse(MessageParser parser, byte[] data, string what)
    {
        try
        {
            return parser.ParseFrom(data);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidDataException($"Failed to decode {what}.", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending request to phone. Action {Action}, id {RequestId}, expecting response {ExpectResponse}")]
    private static partial void LogSending(ILogger logger, ActionType action, string requestId, bool expectResponse);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Received response. Request id {RequestId}, response id {ResponseId}, action {Action}")]
    private static partial void LogReceived(ILogger logger, string requestId, string responseId, ActionType action);

    [LoggerMessage(Level = LogLevel.Error, Message = "A PhoneSlow subscriber threw")]
    private static partial void LogPhoneSlowHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Sent {Count} acks")]
    private static partial void LogAcksSent(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send {Count} acks, re-queued: {Requeued}")]
    private static partial void LogAckFailed(ILogger logger, Exception exception, int count, bool requeued);
}
