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
/// <param name="Action">The action the phone should run.</param>
/// <param name="Data">The request payload, or null for none.</param>
/// <param name="RequestId">A fixed request ID; null or empty makes a new one.</param>
/// <param name="OmitTtl">True to leave the TTL out.</param>
/// <param name="CustomTtlMicros">A TTL in microseconds that replaces the Tachyon one; 0 means not set.</param>
/// <param name="DontEncrypt">True to send the payload unencrypted.</param>
/// <param name="MessageType">The message type; unknown is treated as bugle message.</param>
/// <param name="UserInitiated">True when a person is waiting, which turns on the early "phone is slow" notice.</param>
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
/// A request goes out over HTTP, but its answer arrives on the long poll, so each request registers a waiter keyed by
/// its request ID before it is sent. All waiting uses the <see cref="TimeProvider"/>. Acks for received messages are
/// queued and sent in a batch by <see cref="FlushAcksAsync"/>; the caller decides when (Go does it every 5 s).
/// Request and response contents, tokens and keys are never logged; only request IDs and actions are.
/// </remarks>
/// <param name="http">The HTTP client.</param>
/// <param name="auth">The account whose keys, devices and token are used.</param>
/// <param name="endpoints">The server addresses.</param>
/// <param name="time">The clock for every timeout.</param>
/// <param name="log">The logger.</param>
public sealed partial class RpcSession(GmHttpClient http, AuthData auth, GmEndpoints endpoints, TimeProvider time, ILogger log)
{
    /// <summary>How long to wait for the phone before giving up (Go: <c>responseHardTimeout</c>).</summary>
    public static readonly TimeSpan ResponseHardTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait before telling listeners the phone is slow (Go: <c>pingShortCircuitTimeout</c>).</summary>
    public static readonly TimeSpan PhoneSlowTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The most acks kept for retry (Go: <c>maxQueuedAcks</c>).</summary>
    public const int MaxQueuedAcks = 1024;

    // Response Types By Action (Port Of responseType)
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
    /// Raised once per user-initiated request when the phone has not answered within five seconds.
    /// </summary>
    /// <remarks>Go pokes its pinger here; the pinger then sends a ping and reports "phone not responding" if that fails too.</remarks>
    public event EventHandler? PhoneSlow;

    /// <summary>
    /// Gets the session ID put in every request; empty until <see cref="ResetSessionId"/> is called (as in Go).
    /// </summary>
    public string SessionId { get; internal set; } = "";

    /// <summary>
    /// Gets or sets whether a long poll is running, so an answer can arrive; null skips the check.
    /// </summary>
    /// <remarks>
    /// C#-only. <see cref="GmClient"/> points it at its long-poll loop. A request that waits for an answer fails at once
    /// with <see cref="ConnectionClosedException"/> when it returns false, instead of waiting 60 seconds for an answer
    /// that can never come. True while the loop reconnects, so a brief reconnect does not fail requests.
    /// </remarks>
    internal Func<bool>? IsListening { get; set; }

    /// <summary>
    /// Gets the response types by action.
    /// </summary>
    /// <remarks>Used by tests to compare with Go's table.</remarks>
    internal static IReadOnlyDictionary<ActionType, MessageParser> ResponseTypes => ResponseTypesMap;

    // =========================================================================
    // SENDING
    // =========================================================================

    /// <summary>
    /// Sends a request and waits for the phone's answer.
    /// </summary>
    /// <remarks>
    /// Gives up after 60 seconds with <see cref="PhoneNotRespondingException"/>. For user-initiated requests,
    /// <see cref="PhoneSlow"/> is raised once after 5 seconds without an answer, and waiting continues. Fails at once,
    /// without sending, when no long poll is running (not connected).
    /// </remarks>
    /// <param name="p">What to send.</param>
    /// <param name="ct">Cancels the wait; the waiter is removed.</param>
    /// <returns>The phone's answer.</returns>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="GmRequestException">The server refused the request.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<IncomingRpc> SendAsync(SendParams p, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(p);

        (string request_id, TaskCompletionSource<IncomingRpc> waiter) = await PostWithWaiterAsync(p, ct).ConfigureAwait(false);

        // Timers: Hard Timeout, Plus The Early Notice For User Requests
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
            CancelWaiter(request_id);
            throw new PhoneNotRespondingException($"Phone did not respond to request in {ResponseHardTimeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException)
        {
            CancelWaiter(request_id);
            throw;
        }
        finally
        {
            // Stop The Slow Notice Timer Whenever The Request Ends (Go: defer shortCircuitTimer.Stop())
            await slow.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a request and returns once it is accepted; the returned task completes with the phone's answer.
    /// </summary>
    /// <remarks>
    /// The inner task has no timeout of its own, as in Go; it ends with the answer, <see cref="FailAllWaiters"/>, or
    /// cancellation of <paramref name="ct"/>. Fails at once, without sending, when no long poll is running.
    /// </remarks>
    /// <param name="p">What to send.</param>
    /// <param name="ct">Cancels the send and, later, the wait.</param>
    /// <returns>A task that completes when the request is accepted, holding the task for the answer.</returns>
    /// <exception cref="ConnectionClosedException">Not connected.</exception>
    /// <exception cref="GmRequestException">The server refused the request.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<Task<IncomingRpc>> SendAsyncNoWait(SendParams p, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(p);

        (string request_id, TaskCompletionSource<IncomingRpc> waiter) = await PostWithWaiterAsync(p, ct).ConfigureAwait(false);
        return WaitCancellableAsync(request_id, waiter, ct);
    }

    /// <summary>
    /// Sends a request and does not wait for an answer.
    /// </summary>
    /// <remarks>Used for requests the phone never answers, such as the first pairing step.</remarks>
    /// <param name="p">What to send.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>A task that completes when the server has accepted the request.</returns>
    /// <exception cref="GmRequestException">The server refused the request.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task SendNoResponseAsync(SendParams p, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(p);

        OutgoingRPCMessage payload = Build(p, out string request_id);
        LogSending(log, p.Action, request_id, false);
        await http.PostTypedAsync<OutgoingRPCResponse>(SendUrl, payload, GmContentType.PbLite, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the request the phone receives.
    /// </summary>
    /// <remarks>
    /// Ported from <c>buildMessage</c>. The payload is encrypted with the account's keys unless
    /// <see cref="SendParams.DontEncrypt"/> is set. The TTL is the custom one if given, else the Tachyon one unless omitted.
    /// </remarks>
    /// <param name="p">What to send.</param>
    /// <param name="requestId">The request ID used (generated when <paramref name="p"/> has none).</param>
    /// <returns>The message to POST.</returns>
    internal OutgoingRPCMessage Build(SendParams p, out string requestId)
    {
        requestId = string.IsNullOrEmpty(p.RequestId) ? Guid.NewGuid().ToString() : p.RequestId;
        MessageType message_type = p.MessageType == MessageType.UnknownMessageType ? MessageType.BugleMessage : p.MessageType;
        TachyonToken tachyon     = auth.Tachyon;

        var message = new OutgoingRPCMessage
        {
            Mobile = auth.Mobile,
            Data   = new OutgoingRPCMessage.Types.Data
            {
                RequestID       = requestId,
                BugleRoute      = BugleRoute.DataEvent,
                MessageTypeData = new OutgoingRPCMessage.Types.Data.Types.Type { EmptyArr = new EmptyArr(), MessageType = message_type },
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

        // TTL
        if (p.CustomTtlMicros != 0)
        {
            message.TTL = p.CustomTtlMicros;
        }
        else if (!p.OmitTtl)
        {
            message.TTL = tachyon.TtlMicros;
        }

        // Payload
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
    /// <remarks>Ported from <c>ResetSessionID</c>; called when a new connection starts.</remarks>
    internal void ResetSessionId() => SessionId = Guid.NewGuid().ToString();

    // =========================================================================
    // RECEIVING
    // =========================================================================

    /// <summary>
    /// Hands an incoming message to the request waiting for it, if any.
    /// </summary>
    /// <remarks>
    /// Ported from <c>receiveResponse</c>. When the account has cookies, messages with unencrypted data and no
    /// encrypted data are ignored (Go calls this "very hacky": they are odd messages that arrive before real
    /// responses), except for the two Google pairing actions.
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

        // Ignore Odd Messages That Come Before Real Responses
        // Note: Length > 0 stands in for Go's != nil on bytes fields. A field that is present on the wire with zero
        // length is nil-vs-empty in Go (non-nil) but indistinguishable from absent here; real payloads are never empty.
        bool is_pairing_action = data.Action is ActionType.CreateGaiaPairingClientInit or ActionType.CreateGaiaPairingClientFinished;
        if (auth.HasCookies && !is_pairing_action && data.UnencryptedData.Length > 0 && data.EncryptedData.Length == 0)
        {
            return false;
        }

        // Claim The Waiter
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
    /// <remarks>Ported from <c>cancelAllResponseWaiters</c>; called when the client disconnects.</remarks>
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
    /// Decodes an incoming message: the inner payload by route, then decrypts data events.
    /// </summary>
    /// <remarks>
    /// Ported from <c>decryptInternalMessage</c>. For data events, the response type comes from the action; the
    /// decrypted bytes are parsed as that type when there is one. A second encrypted field some messages carry is
    /// decrypted and checked to parse, but nothing is raised for it (Go fires a fake account-change event there).
    /// </remarks>
    /// <param name="raw">The message from the long poll.</param>
    /// <returns>The decoded message.</returns>
    /// <exception cref="InvalidDataException">The route is unknown or a payload could not be decoded or decrypted.</exception>
    internal IncomingRpc Decode(IncomingRPCMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        return raw.BugleRoute switch
        {
            BugleRoute.PairEvent => new IncomingRpc { Raw = raw, Pair = Parse(RPCPairData.Parser, raw.MessageData.ToByteArray(), "pair event") },
            BugleRoute.GaiaEvent => new IncomingRpc { Raw = raw, Gaia = Parse(RPCGaiaData.Parser, raw.MessageData.ToByteArray(), "gaia event") },
            BugleRoute.DataEvent => DecodeDataEvent(raw),
            _                    => throw new InvalidDataException($"Unknown bugle route {(int)raw.BugleRoute}."),
        };
    }

    // =========================================================================
    // ACKS
    // =========================================================================

    /// <summary>
    /// Queues an ack for a received message, unless it is already queued.
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
    /// Ported from <c>sendAckRequest</c>. On failure the batch is put back in front of newer acks so unacked messages
    /// do not stall delivery; if that would make the queue longer than 1024, the batch is dropped instead. Failures
    /// are logged, not thrown; only cancellation of <paramref name="ct"/> is.
    /// </remarks>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>A task that completes when the acks were sent or re-queued.</returns>
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
            // Re-queue In Front Of Newer Acks, Unless The Queue Would Overflow
            bool requeued;
            lock (_acksLock)
            {
                requeued = batch.Length + _acks.Count <= MaxQueuedAcks;
                if (requeued)
                {
                    _acks.InsertRange(0, batch);
                }
            }

            // Canceled: Throw The Documented Cancel, Not The Aborted Socket's Error
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

    // Port Of sendAsyncMessageWithID: Register The Waiter Before Sending
    private async Task<(string RequestId, TaskCompletionSource<IncomingRpc> Waiter)> PostWithWaiterAsync(SendParams p, CancellationToken ct)
    {
        // No Long Poll, No Answer: Fail Now Instead Of After 60 s
        if (IsListening?.Invoke() == false)
        {
            throw new ConnectionClosedException("Not connected: no long poll is running to receive the answer.");
        }

        OutgoingRPCMessage payload = Build(p, out string request_id);

        var waiter = new TaskCompletionSource<IncomingRpc>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waitersLock)
        {
            _waiters[request_id] = waiter;
        }

        LogSending(log, p.Action, request_id, true);
        try
        {
            await http.PostTypedAsync<OutgoingRPCResponse>(SendUrl, payload, GmContentType.PbLite, ct).ConfigureAwait(false);
        }
        catch
        {
            CancelWaiter(request_id);
            throw;
        }

        return (request_id, waiter);
    }

    // Port Of cancelResponse: Only Cancel If Nobody Claimed It Yet
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

    // Fires Once After 5 s; Stops Quietly When The Request Ends First
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
        IMessage? decrypted_message = parser?.ParseFrom([]);
        byte[]? decrypted_data      = null;

        if (message.EncryptedData.Length > 0)
        {
            decrypted_data = Decrypt(message.EncryptedData, "data event");
            if (parser is not null)
            {
                decrypted_message = Parse(parser, decrypted_data, "decrypted data event");
            }
        }
        else if (message.EncryptedData2.Length > 0)
        {
            decrypted_data = Decrypt(message.EncryptedData2, "field 2 in data event");
            Parse(EncryptedData2Container.Parser, decrypted_data, "decrypted field 2 data event");
        }

        return new IncomingRpc { Raw = raw, Message = message, DecryptedData = decrypted_data, DecryptedMessage = decrypted_message };
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

    private static T Parse<T>(MessageParser<T> parser, byte[] data, string what) where T : IMessage<T>
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
