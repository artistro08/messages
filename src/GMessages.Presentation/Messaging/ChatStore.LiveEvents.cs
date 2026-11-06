using GMessages.Protocol.Events;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// The phone's live events: a message, a typing line, a conversation change or the battery turned into chat events.
/// </summary>
/// <remarks>
/// Part of <see cref="ChatStore"/>; the other members are in ChatStore.cs.
/// </remarks>
public sealed partial class ChatStore
{

    // =========================================================================
    // LIVE EVENTS
    // =========================================================================

    // Fire And Forget; HandleEventAsync Logs Its Own Failures
    private void OnClientEvent(object? sender, object e) => _ = HandleEventAsync(e);

    /// <summary>
    /// Maps one protocol event to the store's state and events (the constructor subscribes the real source; tests call this directly).
    /// </summary>
    /// <param name="e">A record from <c>GMessages.Protocol.Events</c>.</param>
    /// <returns>A task that completes when the event was handled.</returns>
    internal async Task HandleEventAsync(object e)
    {
        switch (e)
        {
            case ConversationUpdated updated:
                ConversationInfo info = WithSnippetMedia(ConversationInfo.From(updated.Conversation));

                // The Phone Says It's Read: The Live Unread Count Starts Over
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

            // Qualified: The Store's Own Event Has The Same Name
            case GMessages.Protocol.Events.TypingChanged typing:
                RaiseTyping(typing.Data);
                break;
        }
    }

    // Sets The Battery State And Raises Only On A Change
    private void SetBatteryLow(bool low)
    {
        if (Interlocked.Exchange(ref _batteryLow, low) != low)
        {
            PhoneBatteryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Maps The Typing Person's Number To A Participant Of The Cached Conversation, As getPhoneNumberParticipantID Does
    // In The Go Connector. No Cached Conversation Or No Match Means No Event
    // ponytail: first match wins; the connector prefers a visible participant, add IsVisible if duplicate numbers show up
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

    // Raises MessageArrived With The Conversation From The Cache, Fetching It On A Miss
    private async Task HandleMessageAsync(MessageReceived received)
    {
        ChatMessage message = ChatMessage.From(received.Message);
        int generation      = Generation;

        // A Live Stand-In The Phone Shows Nowhere: Not Saved, Shown Or Notified, And Its ID Isn't Marked Notified, So The
        // Real Message (Same ID, Often Moved Into A Group) Still Notifies. A Deletion Notice Drops The Kept Copy
        if (message.IsPlaceholder)
        {
            if (message.Status == MessageStatusType.MessageDeleted && message.Id.Length > 0)
            {
                ForgetMessage(message.ConversationId, message.Id, generation);
            }

            return;
        }

        // No ConfigureAwait(false) Below: Events Must Be Raised On The Thread That Delivered The Client Event
        // Decide IsNew Before Any Await, So Two Quick Events Of One Message Can't Both Be New. A Message Older Than A Day
        // Is Never New Either: A Reaction Or Status Update To It Must Not Notify (mautrix-gmessages, GetReactions). Nor
        // Is A Message Already In Its Cached Page, Or One Carrying My Own Reaction: Both Are The Phone Echoing An Update
        // (A Reaction Made Here Or On The Phone), Which The Notified Ring Misses After A Restart. Others' Reactions Don't Count
        bool isNew = MarkNotified(message.Id)
                     && !HasMyReaction(message)
                     && !IsCached(message)
                     && message.Time >= _time.GetUtcNow() - NewMessageAge;

        // A New Received Message Adds One To Its Conversation's Unread Count, Only While The Phone Says It's Unread (Its
        // Conversation Update Comes First): One Read Meanwhile Mustn't Carry Into The Next Count. Since That Update Was
        // Already Shown, A Pinned Tile Re-Reads The Count Below Even When Nothing Else Changed. A System Line (Someone
        // Joined, A Chat Created) Isn't From Me, But It's Nothing To Read Either
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

        // A Live Message Newer Than The Cached Conversation's Last Moves It To The Top At Once (The List Sorts By This
        // Time), Sent Or Received, Without Waiting For Google's Conversation Update, Which Doesn't Always Come. A Received
        // One's Update Comes First With Its Snippet; For One Of Mine It Often Never Came (The Row Kept The Old Snippet), So
        // Mine Brings Its Own (A System Line Keeps It). A Received One Makes The Chat Unread: When It Beats The Update, The
        // Cached Copy Is Still Read, And Raising That Would Take This Message's Toast With It. Only A New One Does: The
        // Phone Re-Sends A Message It Read (A Status Update), And An MMS's Whole-Second Time Can Sit A Millisecond Past
        // The Conversation's, Which Turned A Chat Read On The Phone Unread Again. The Update, When It Comes, Has The Last
        // Word. A Conversation Fetched Below Is Already Current. The Message's Status Becomes The Newest One's, So A Chat
        // That Held Only Its Created Line (Hidden, ConversationInfo.IsEmpty) Shows As Soon As A Message Lands In It
        if (!received.IsOld && conversation is not null && message.Time > conversation.LastMessageTime)
        {
            conversation = message.FromMe && message.Kind is not (MessageKind.Tombstone or MessageKind.Other)
                ? conversation with
                {
                    LastMessageTime     = message.Time,
                    Snippet             = message.Text,
                    SnippetFromMe       = true,
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

        // Unpaired Or Signed Out Meanwhile: Nothing Of The Old Account Is Raised
        if (generation != Generation)
        {
            return;
        }

        // The Conversation (Its Update, Or The One Fetched Above) Has No Snippet Text For This Media: Name It From The
        // Message Itself
        if (conversation is { Snippet.Length: 0, SnippetMedia.Length: 0 } && conversation.LatestMessageId == message.Id && message.Media is { } media)
        {
            conversation = conversation with { SnippetMedia = ChatFormat.MediaLabel(media) };
            Upsert(conversation);
            ConversationChanged?.Invoke(this, conversation);
            shown = true;
        }

        // Counted But Not Shown Yet: The Lists Re-Read The Conversation (And Its Count). Only A Pinned One Shows A Count
        if (counted && !shown && conversation is { Pinned: true })
        {
            ConversationChanged?.Invoke(this, conversation);
        }

        MessageArrived?.Invoke(this, new MessageArrivedEventArgs(message, conversation, received.IsOld, isNew));
    }
}
