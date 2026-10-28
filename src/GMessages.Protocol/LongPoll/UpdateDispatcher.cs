using System.Security.Cryptography;
using GMessages.Protocol.Events;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.LongPoll;

/// <summary>
/// Decodes each incoming long-poll message, answers waiting requests, and turns the rest into events.
/// </summary>
/// <remarks>
/// Ported from <c>HandleRPCMsg</c>, <c>handleUpdatesEvent</c>, <c>deduplicateHash</c> and the fake account-change
/// part of <c>decryptInternalMessage</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/event_handler.go">event_handler.go</see>.
/// Every message is acked. After connecting, the server says how many queued messages it is replaying
/// (<see cref="SkipCount"/>); that many data events are marked old, which is what the app uses to skip
/// notifications for catch-up messages. The same update arriving twice (same ID, same decrypted bytes, within the
/// last 8 updates) is dropped. Pairing routes are only acked here; pairing handles its own responses. Message
/// contents are never logged, only IDs and actions. Not thread-safe: the long-poll loop calls it from one reader.
/// </remarks>
/// <param name="rpc">The RPC session that decodes messages and owns the ack queue and response waiters.</param>
/// <param name="log">The logger.</param>
public sealed partial class UpdateDispatcher(RpcSession rpc, ILogger log)
{
    // Go: recentUpdates [8]updateDedupItem
    private const int RecentUpdatesLength = 8;

    // Go: hackyLoggedOutBytes
    private static readonly byte[] LoggedOutBytes = [0x72, 0x00];

    private readonly (string Id, byte[] Hash)[] _recentUpdates = new (string, byte[])[RecentUpdatesLength];
    private int _recentUpdatesPtr;

    /// <summary>
    /// Raised for every event; the value is one of the records in <see cref="GMessages.Protocol.Events"/>.
    /// </summary>
    public event Action<object>? EventRaised;

    /// <summary>
    /// Gets or sets how many more data events count as old (catch-up) messages.
    /// </summary>
    /// <remarks>Set from the server's startup ack count; each data event lowers it by one until it reaches zero.</remarks>
    public int SkipCount { get; set; }

    /// <summary>
    /// Gets or sets the pinger told about phone activity, or null when not logged in.
    /// </summary>
    internal DittoPinger? Pinger { get; set; }

    /// <summary>
    /// Gets or sets the stop token of the current long-poll run, used by the requests sent in the background.
    /// </summary>
    /// <remarks>Set by the loop at the start of each run, so nothing is posted after a disconnect or dispose.</remarks>
    internal CancellationToken StopToken { get; set; }

    /// <summary>
    /// Handles one incoming message.
    /// </summary>
    /// <remarks>
    /// Messages that fail to decode are logged and acked, then dropped, as in Go. A message that answers a waiting
    /// request goes to that request and raises nothing; it also counts as phone activity unless it is a ping answer.
    /// </remarks>
    /// <param name="raw">The message from the long poll.</param>
    public void Handle(IncomingRPCMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        // Decode
        IncomingRpc msg;
        try
        {
            msg = rpc.Decode(raw);
        }
        catch (InvalidDataException ex)
        {
            LogDecodeFailed(log, ex, raw.ResponseID);
            rpc.QueueAck(raw.ResponseID);
            return;
        }

        RaiseFakeAccountChange(msg);

        // Ack, Then Hand To A Waiting Request
        rpc.QueueAck(raw.ResponseID);
        if (rpc.TryDeliverResponse(msg))
        {
            if (msg.Message!.Action != ActionType.NotifyDittoActivity)
            {
                Pinger?.OnPhoneActivity("request response");
            }

            return;
        }

        LogReceived(log, raw.ResponseID, raw.BugleRoute, msg.Message?.Action);
        if (raw.BugleRoute != BugleRoute.DataEvent)
        {
            return;
        }

        // Mark Catch-Up Messages As Old
        if (SkipCount > 0)
        {
            SkipCount--;
            msg.IsOld = true;
        }

        if (!msg.IsOld)
        {
            Pinger?.OnPhoneActivity("data event");
        }

        HandleUpdates(msg);
    }

    // Port Of handleUpdatesEvent
    private void HandleUpdates(IncomingRpc msg)
    {
        RPCMessageData data = msg.Message!;
        if (data.Action != ActionType.GetUpdates)
        {
            LogUnexpectedResponse(log, data.SessionID, data.Action, msg.IsOld);
            return;
        }

        // Signed Out On The Phone
        if (msg.DecryptedData is null && data.UnencryptedData.Span.SequenceEqual(LoggedOutBytes))
        {
            _ = FlushAcksAsync();
            Raise(new GaiaLoggedOut());
            return;
        }

        if (!msg.IsOld)
        {
            Pinger?.BumpDataReceiveCheck();
        }

        if (msg.DecryptedMessage is not UpdateEvents updates)
        {
            LogUnexpectedType(log, msg.IsOld);
            return;
        }

        switch (updates.EventCase)
        {
            case UpdateEvents.EventOneofCase.UserAlertEvent:
                if (!msg.IsOld)
                {
                    Raise(new UserAlert(updates.UserAlertEvent));
                }

                break;

            case UpdateEvents.EventOneofCase.SettingsEvent:
                Raise(new SettingsChanged(updates.SettingsEvent));
                break;

            case UpdateEvents.EventOneofCase.ConversationEvent:
                foreach (Conversation part in updates.ConversationEvent.Data)
                {
                    if (IsDuplicate(part.ConversationID, msg))
                    {
                        return;
                    }

                    if (!msg.IsOld)
                    {
                        Raise(new ConversationUpdated(part));
                    }
                }

                break;

            case UpdateEvents.EventOneofCase.MessageEvent:
                foreach (Message part in updates.MessageEvent.Data)
                {
                    if (IsDuplicate(part.MessageID, msg))
                    {
                        return;
                    }

                    Raise(new MessageReceived(part, msg.IsOld));
                }

                break;

            case UpdateEvents.EventOneofCase.TypingEvent:
                if (!msg.IsOld)
                {
                    Raise(new TypingChanged(updates.TypingEvent.Data));
                }

                break;

            case UpdateEvents.EventOneofCase.BrowserPresenceCheckEvent:
                _ = AckBrowserPresenceAsync();
                break;

            case UpdateEvents.EventOneofCase.AccountChange:
                Raise(new AccountChanged(updates.AccountChange, false));
                break;

            default:
                LogUnknownEvent(log, msg.Raw.ResponseID);
                break;
        }
    }

    // Port Of The "Hacky Hack" In decryptInternalMessage: Startup Account Notice From The Second Encrypted Field
    private void RaiseFakeAccountChange(IncomingRpc msg)
    {
        if (msg.Message is not { EncryptedData.Length: 0, EncryptedData2.Length: > 0 } || msg.DecryptedData is null)
        {
            return;
        }

        // RpcSession.Decode already checked these bytes parse
        AccountChangeOrSomethingEvent? change = EncryptedData2Container.Parser.ParseFrom(msg.DecryptedData).AccountChange;
        if (change is not null && change.Account.Contains('@', StringComparison.Ordinal))
        {
            Raise(new AccountChanged(change, true));
        }
    }

    // Port Of deduplicateUpdate / deduplicateHash: Same ID And Same Bytes Among The Last 8 Updates
    private bool IsDuplicate(string id, IncomingRpc msg)
    {
        if (msg.DecryptedData is null)
        {
            return false;
        }

        byte[] hash = SHA256.HashData(msg.DecryptedData);
        for (int i = _recentUpdatesPtr + RecentUpdatesLength - 1; i >= _recentUpdatesPtr; i--)
        {
            (string? seen_id, byte[]? seen_hash) = _recentUpdates[i % RecentUpdatesLength];
            if (seen_id != id)
            {
                continue;
            }

            if (seen_hash.AsSpan().SequenceEqual(hash))
            {
                LogDuplicate(log, id, msg.IsOld);
                return true;
            }

            break;
        }

        _recentUpdates[_recentUpdatesPtr] = (id, hash);
        _recentUpdatesPtr = (_recentUpdatesPtr + 1) % RecentUpdatesLength;
        return false;
    }

    // Port Of ackBrowserPresence
    private async Task AckBrowserPresenceAsync()
    {
        CancellationToken ct = StopToken;
        try
        {
            await rpc.SendNoResponseAsync(new SendParams(ActionType.AckBrowserPresence), ct).ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Stopped: Not A Failure
        }
        catch (Exception ex)
        {
            LogBrowserPresenceFailed(log, ex);
        }
    }

    // The Logged-Out Ack Flush (Go: sendAckRequest); Failures Are Logged By The Flush, Stopping Is Quiet
    private async Task FlushAcksAsync()
    {
        try
        {
            await rpc.FlushAcksAsync(StopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped
        }
    }

    private void Raise(object evt)
    {
        try
        {
            EventRaised?.Invoke(evt);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(log, ex, evt.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to decode incoming RPC message {ResponseId}")]
    private static partial void LogDecodeFailed(ILogger logger, Exception exception, string responseId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Received message {ResponseId}, route {Route}, action {Action}")]
    private static partial void LogReceived(ILogger logger, string responseId, BugleRoute route, ActionType? action);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Got unexpected response. Request id {RequestId}, action {Action}, old {IsOld}")]
    private static partial void LogUnexpectedResponse(ILogger logger, string requestId, ActionType action, bool isOld);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected data type in GET_UPDATES event, old {IsOld}")]
    private static partial void LogUnexpectedType(ILogger logger, bool isOld);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Got unknown event type in message {ResponseId}")]
    private static partial void LogUnknownEvent(ILogger logger, string responseId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Ignoring duplicate update {ThingId}, old {IsOld}")]
    private static partial void LogDuplicate(ILogger logger, string thingId, bool isOld);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to ack browser presence")]
    private static partial void LogBrowserPresenceFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "An event subscriber threw for {EventType}")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string eventType);
}
