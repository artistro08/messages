using GMessages.Protocol.Events;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Turns the live events of the phone into chat events: a message, a typing line, a conversation change or the battery.
/// </summary>
/// <remarks>
/// This part of <see cref="ChatStore"/> holds the live events. ChatStore.cs holds the other members.
/// </remarks>
public sealed partial class ChatStore
{
    // HandleEventAsync logs its own failures, so nothing waits for the task.
    private void OnClientEvent(object? sender, object e) => _ = HandleEventAsync(e);

    /// <summary>
    /// Maps 1 protocol event to the state and the events of the store. The constructor subscribes the real source, and
    /// tests call this method directly.
    /// </summary>
    /// <param name="e">A record from <c>GMessages.Protocol.Events</c>.</param>
    /// <returns>A task that completes when the store handled the event.</returns>
    internal async Task HandleEventAsync(object e)
    {
        switch (e)
        {
            case ConversationUpdated updated:
                ConversationInfo info = WithSnippetMedia(ConversationInfo.From(updated.Conversation));

                // The phone says that the conversation is read, so the live unread texts start again.
                if (!info.Unread)
                {
                    _unreadTexts.TryRemove(info.Id, out _);
                }

                Upsert(info);
                SaveConversation(updated.Conversation, Generation);
                ConversationChanged?.Invoke(this, info);
                break;

            case MessageReceived received:
                await HandleMessageAsync(received).ConfigureAwait(false);
                break;

            case SettingsChanged settings:
                _sims = [.. settings.Settings.SIMCards];
                break;

            case UserAlert alert when alert.Alert.AlertType is AlertType.MobileBatteryLow or AlertType.MobileBatteryRestored:
                bool low = alert.Alert.AlertType == AlertType.MobileBatteryLow;
                SetBatteryLow(low);
                if (low)
                {
                    PhoneBatteryLowAlerted?.Invoke(this, EventArgs.Empty);
                }

                break;

            case ListenRecovered:
                Resynced?.Invoke(this, EventArgs.Empty);
                break;

            // The full name is necessary, because the store has an event with the same name.
            case GMessages.Protocol.Events.TypingChanged typing:
                RaiseTyping(typing.Data);
                break;
        }
    }

    private void SetBatteryLow(bool low)
    {
        if (Interlocked.Exchange(ref _batteryLow, low) != low)
        {
            PhoneBatteryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Maps the number of the typing person to a participant of the cached conversation. getPhoneNumberParticipantID in
    // the Go connector does the same. With no cached conversation or no match, the store raises no event.
    // ponytail: the first match wins. The connector prefers a visible participant. Add IsVisible if duplicate numbers
    // occur.
    private void RaiseTyping(TypingData data)
    {
        string number = data.User?.Number ?? string.Empty;
        if (number.Length == 0 || Find(data.ConversationID) is not { } conversation)
        {
            return;
        }

        ParticipantInfo? who = conversation.Participants.FirstOrDefault(p => p.Number == number);
        if (who is null)
        {
            return;
        }

        TypingChanged?.Invoke(this, new TypingEventArgs(conversation.Id, who.Id, data.Type == TypingTypes.StartedTyping));
    }

    // Raises MessageArrived with the conversation from the cache. On a cache miss, it fetches the conversation first.
    private async Task HandleMessageAsync(MessageReceived received)
    {
        ChatMessage message = ChatMessage.From(received.Message);
        int generation      = Generation;

        // The phone shows a live stand-in nowhere. The store does not save, show or notify it, and does not mark its ID
        // notified. Thus the real message, with the same ID and often moved into a group, still notifies. A deletion
        // notice removes the kept copy.
        if (message.IsPlaceholder)
        {
            if (message.Status == MessageStatusType.MessageDeleted && message.Id.Length > 0)
            {
                ForgetMessage(message.ConversationId, message.Id, generation);
            }

            return;
        }

        // No ConfigureAwait(false) below: the store must raise events on the thread that delivered the client event.
        // The store decides isNew before any await, so 2 quick events of 1 message cannot both be new. A message older
        // than 1 day is never new. A reaction or status update to it must not notify, as GetReactions in
        // mautrix-gmessages decides. A message already in its cached page is not new. A message with my own reaction
        // is not new. Both are echoes of an update from the phone, such as a reaction made here or on the phone. The
        // notified ring misses these echoes after a restart. The reactions of other people do not count.
        bool isNew = MarkNotified(message.Id)
                     && !HasMyReaction(message)
                     && !IsCached(message)
                     && message.Time >= _time.GetUtcNow() - NewMessageAge;

        // A new received message adds 1 to the unread count of its conversation. It counts only while the phone says
        // that the conversation is unread, because the conversation update comes first. Thus a chat read meanwhile does
        // not carry into the next count. The store already showed that update, so a pinned tile reads the count again
        // below, also when nothing else changed. A system line, such as a join or a chat creation, is not from me. It
        // is also not something to read, so it never counts.
        bool receivedNew           = isNew && !message.FromMe && message.Kind != MessageKind.Tombstone;
        ConversationInfo? unreadIn = receivedNew && Find(message.ConversationId) is { Unread: true } found ? found : null;
        bool counted               = unreadIn is not null;
        bool shown                 = false;
        if (unreadIn is not null)
        {
            string text = ChatFormat.UnreadLine(unreadIn, message);
            _unreadTexts.AddOrUpdate(message.ConversationId, [text], (_, texts) => texts.Add(text));
        }

        CacheMessage(message);
        SaveMessages(message.ConversationId, [received.Message], generation);
        if (message is { Media: { } liveMedia, Id.Length: > 0 })
        {
            _liveMedia[message.ConversationId] = (message.Id, ChatFormat.MediaLabel(liveMedia));
        }

        NoteEncryption(message.ConversationId, [message], generation);

        ConversationInfo? conversation = Find(message.ConversationId);

        // A live message newer than the last message of the cached conversation moves the conversation to the top at
        // once. The list sorts by this time. This applies to sent and received messages, because Google does not always
        // send the conversation update.
        // For a received message, the update usually comes first with its snippet. For a message of mine, the update
        // often never came, and the row kept the old snippet. Thus my message brings its own snippet. A system line
        // keeps the old snippet.
        // A received message turns the chat unread. When it comes before the update, the cached copy is still read.
        // Raising that read copy dismisses the toast of this message. Only a new message turns the chat unread. The
        // phone sends a read message again as a status update. The whole-second time of an MMS can sit 1 ms past the
        // time of its conversation. Without this rule, a chat read on the phone turns unread again.
        // When the update comes, it replaces this copy. A conversation that the code below fetches is already current.
        // The status of the message becomes the newest status. Thus a chat with only its created line shows when a
        // message lands in it, see ConversationInfo.IsEmpty. That message also brings its snippet, mine or not. Thus
        // the row does not show the snippet of the created line until the update.
        if (!received.IsOld && conversation is not null && message.Time > conversation.LastMessageTime)
        {
            bool bringsSnippet = (message.FromMe || conversation.IsEmpty) && message.Kind is not (MessageKind.Tombstone or MessageKind.Other);
            conversation       = bringsSnippet
                ? conversation with
                {
                    LastMessageTime     = message.Time,
                    Unread              = conversation.Unread || receivedNew,
                    Snippet             = message.Text,
                    SnippetFromMe       = message.FromMe,
                    SnippetMedia        = message.Media is { } snippetMedia ? ChatFormat.MediaLabel(snippetMedia) : string.Empty,
                    LatestMessageId     = message.Id,
                    LatestMessageStatus = message.Status,
                }
                : conversation with
                {
                    LastMessageTime     = message.Time,
                    Unread              = conversation.Unread || receivedNew,
                    LatestMessageStatus = message.Status,
                };
            if (Upsert(conversation))
            {
                ConversationChanged?.Invoke(this, conversation);
                shown = true;
            }
        }

        if (conversation is null && _client() is { } client)
        {
            try
            {
                Conversation? fetched = await client.GetConversationAsync(message.ConversationId, CancellationToken.None);
                if (fetched is not null && generation == Generation)
                {
                    conversation = CacheConversation(fetched, generation);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogConversationFetchFailed(message.ConversationId, ex.GetType().Name);
            }
        }

        // After an unpair or a sign-out during the fetch, the store raises nothing of the old account.
        if (generation != Generation)
        {
            return;
        }

        // The conversation has no snippet text for this media, so the message itself names it.
        if (conversation is { Snippet.Length: 0, SnippetMedia.Length: 0 } && conversation.LatestMessageId == message.Id && message.Media is { } media)
        {
            conversation = conversation with { SnippetMedia = ChatFormat.MediaLabel(media) };
            Upsert(conversation);
            ConversationChanged?.Invoke(this, conversation);
            shown = true;
        }

        // The message counts, but the lists did not get a change yet. This event makes them read the conversation and
        // its count again. Only a pinned conversation shows a count.
        if (counted && !shown && conversation is { Pinned: true })
        {
            ConversationChanged?.Invoke(this, conversation);
        }

        MessageArrived?.Invoke(this, new MessageArrivedEventArgs(message, conversation, received.IsOld, isNew));
    }
}
