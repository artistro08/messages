using System.Security.Cryptography;
using GMessages.Protocol.Events;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.LongPoll;

/// <summary>
/// Decodes each incoming long-poll message. It gives answers to the waiting requests and turns the other messages into events.
/// </summary>
/// <remarks>
/// Ported from <c>HandleRPCMsg</c>, <c>handleUpdatesEvent</c>, <c>deduplicateHash</c> and the fake account-change
/// part of <c>decryptInternalMessage</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/event_handler.go">event_handler.go</see>.
/// The dispatcher acks each message. After a connect, the server tells how many queued messages it replays.
/// <see cref="SkipCount"/> holds that number. The dispatcher marks that many data events as old. The app uses this
/// mark to skip notifications for catch-up messages. A duplicate has the same ID and decrypted bytes as 1 of the
/// last 8 updates. The dispatcher drops duplicates. For the pairing routes, the dispatcher only sends the ack.
/// Pairing handles its own responses. The log gets only IDs and actions, never message contents. The type is not
/// thread-safe. The long-poll loop calls it from 1 reader.
/// </remarks>
/// <param name="rpc">The RPC session. It decodes messages and owns the ack queue and the response waiters.</param>
/// <param name="log">The logger.</param>
public sealed partial class UpdateDispatcher(RpcSession rpc, ILogger log)
{
    // Port of recentUpdates [8]updateDedupItem.
    private const int RecentUpdatesLength = 8;

    // Port of hackyLoggedOutBytes.
    private static readonly byte[] LoggedOutBytes = [0x72, 0x00];

    private readonly (string Id, byte[] Hash)[] _recentUpdates = new (string, byte[])[RecentUpdatesLength];
    private int _recentUpdatesPtr;

    /// <summary>
    /// Occurs for each event. The value is 1 of the records in <see cref="GMessages.Protocol.Events"/>.
    /// </summary>
    public event Action<object>? EventRaised;

    /// <summary>
    /// Gets or sets how many more data events are old catch-up messages.
    /// </summary>
    /// <remarks>The loop sets it from the startup ack count of the server. Each data event lowers it by 1 until it is 0.</remarks>
    public int SkipCount { get; set; }

    /// <summary>
    /// Gets or sets the pinger that gets the phone activity, or null when not logged in.
    /// </summary>
    internal DittoPinger? Pinger { get; set; }

    /// <summary>
    /// Gets or sets the stop token of the current long-poll run. The requests in the background use it.
    /// </summary>
    /// <remarks>The loop sets it at the start of each run. Thus no request goes out after a disconnect or a dispose.</remarks>
    internal CancellationToken StopToken { get; set; }

    /// <summary>
    /// Handles 1 incoming message.
    /// </summary>
    /// <remarks>
    /// As in Go, the dispatcher logs and acks a message that does not decode, then drops it. A message that answers a
    /// waiting request goes to that request and raises no event. Such a message also counts as phone activity, but a
    /// ping answer does not.
    /// </remarks>
    /// <param name="raw">The message from the long poll.</param>
    public void Handle(IncomingRPCMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

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

        // As in Go, the ack goes in the queue before the response delivery and before the old mark.
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

    // Port of handleUpdatesEvent.
    private void HandleUpdates(IncomingRpc msg)
    {
        RPCMessageData data = msg.Message!;
        if (data.Action != ActionType.GetUpdates)
        {
            LogUnexpectedResponse(log, data.SessionID, data.Action, msg.IsOld);
            return;
        }

        // The phone signed out of the Google account.
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

    // Port of the "hacky hack" in decryptInternalMessage. The second encrypted field carries an account notice at startup.
    private void RaiseFakeAccountChange(IncomingRpc msg)
    {
        if (msg.Message is not { EncryptedData.Length: 0, EncryptedData2.Length: > 0 } || msg.DecryptedData is null)
        {
            return;
        }

        // RpcSession.Decode checked already that these bytes parse.
        AccountChangeOrSomethingEvent? change = EncryptedData2Container.Parser.ParseFrom(msg.DecryptedData).AccountChange;
        if (change is not null && change.Account.Contains('@', StringComparison.Ordinal))
        {
            Raise(new AccountChanged(change, true));
        }
    }

    // Port of deduplicateUpdate and deduplicateHash.
    private bool IsDuplicate(string id, IncomingRpc msg)
    {
        if (msg.DecryptedData is null)
        {
            return false;
        }

        byte[] hash = SHA256.HashData(msg.DecryptedData);
        for (int i = _recentUpdatesPtr + RecentUpdatesLength - 1; i >= _recentUpdatesPtr; i--)
        {
            (string? seenId, byte[]? seenHash) = _recentUpdates[i % RecentUpdatesLength];
            if (seenId != id)
            {
                continue;
            }

            if (seenHash.AsSpan().SequenceEqual(hash))
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

    // Port of ackBrowserPresence.
    private async Task AckBrowserPresenceAsync()
    {
        CancellationToken ct = StopToken;
        try
        {
            await rpc.SendNoResponseAsync(new SendParams(ActionType.AckBrowserPresence), ct).ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // A stop is not a failure.
        }
        catch (Exception ex)
        {
            LogBrowserPresenceFailed(log, ex);
        }
    }

    // Port of the sendAckRequest call after a sign-out. The flush logs failures, and a stop is quiet.
    private async Task FlushAcksAsync()
    {
        try
        {
            await rpc.FlushAcksAsync(StopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The run stopped.
        }
    }

    private void Raise(object evt) => ProtocolLog.Raise(log, EventRaised, evt);

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
}
