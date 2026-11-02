using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Main;

/// <summary>
/// Live updates for the open chat: arriving messages, typing, resyncs, conversation changes and Dispose.
/// </summary>
/// <remarks>
/// Part of <see cref="ChatViewModel"/>; the other members are in ChatViewModel.cs.
/// </remarks>
public sealed partial class ChatViewModel
{

    // =========================================================================
    // LIVE UPDATES
    // =========================================================================

    private void OnMessageArrived(object? sender, MessageArrivedEventArgs e)
    {
        ChatMessage m = e.Message;
        if (m.ConversationId != ConversationId)
        {
            return;
        }

        // A System Line Shows No Bubble, But Can Switch Encryption On Or Off (The Header's Lock And The Status Row's)
        if (m.Kind == MessageKind.Tombstone)
        {
            IsEncrypted = _store.IsEncrypted(ConversationId);
            RefreshStatus(IsEncrypted, Messages.Count - 1);
            return;
        }

        // Only A Message New To This PC Animates In. The Phone Also Sends A Chat's Recent History As Live Messages (On A
        // Catch-Up, And After Start Chat Opens One), Which Showed As The History Loading A Second Time, Bubble By Bubble;
        // Those Go In Quietly, In Time Order
        MessageBubbleViewModel? bubble = FindBubble(m);
        if (bubble is not null)
        {
            bubble.Update(m);
            Index(bubble);
        }
        else
        {
            // Older Than The Oldest Bubble After A Trim (A Reaction Or Status On A Trimmed Message): The Store Saved It, And
            // The Older Page Brings It Back In Place. Catch-Up History Before Any Trim Still Goes In Above
            if (_trimmed && _older is not null && Messages.Count > 0 && m.Time < Messages[0].SentAt)
            {
                return;
            }

            bubble = NewBubble(m);
            if (e.IsNew && !e.IsOld)
            {
                MarkNewIfShown(bubble);
            }

            int at = Messages.Count;
            while (at > 0 && Messages[at - 1].Id.Length > 0 && Messages[at - 1].SentAt > m.Time)
            {
                at--;
            }

            Messages.Insert(at, bubble);
        }

        RefreshAround(bubble);

        // Their Message Ends Their Typing
        if (!m.FromMe && m.ParticipantId == _typingParticipant)
        {
            StopTyping();
        }

        // Reading Along: Tell The Phone
        if (IsActive && !m.FromMe && m.Id.Length > 0)
        {
            _ = MarkReadAsync(m.Id);
        }
    }

    // Posted, So It Can Run After The Chat Closed: Then It Does Nothing
    private void OnResynced(object? sender, EventArgs e) => _post(() =>
    {
        if (!_disposed)
        {
            _ = LoadAsync();
        }
    });

    // This Conversation Changed (A New Message, Muted, Moved): Keep It Current, Raising Only A Mute Change, Since Every
    // Message Lands Here Too
    private void OnConversationChanged(object? sender, ConversationInfo c)
    {
        if (c.Id != ConversationId)
        {
            return;
        }

        Conversation = c;
        if (IsMuted != c.IsMuted)
        {
            IsMuted = c.IsMuted;
            Raise(nameof(IsMuted));
        }

        // Someone Joined, Left Or Was Renamed: Every Bubble's Sender, Avatar And Reactions Follow
        if (!SameForBubbles(_bubblesFor, c))
        {
            RefreshBubbles();
        }
    }

    // Typing In: A Start Shows The Dots For Up To 15 Seconds, A Stop Hides Them At Once
    private void OnTypingChanged(object? sender, TypingEventArgs e)
    {
        if (e.ConversationId != ConversationId)
        {
            return;
        }

        if (!e.IsTyping)
        {
            StopTyping();
            return;
        }

        _typingParticipant = e.ParticipantId;
        _typingUntil       = _time.GetUtcNow() + TypingTimeout;
        IsTyping           = true;
        _typingTimer.Change(TypingTimeout, Timeout.InfiniteTimeSpan);
    }

    // The Timer Fired; A Start That Came Since Moved The Deadline
    private void OnTypingTimeout()
    {
        // The Post Can Run After The Chat Closed: The Timer Is Disposed Then
        if (!_disposed && _time.GetUtcNow() >= _typingUntil)
        {
            StopTyping();
        }
    }

    private void StopTyping()
    {
        _typingParticipant = string.Empty;
        IsTyping           = false;
        _typingTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private async Task MarkReadAsync(string messageId)
    {
        try
        {
            await _store.MarkReadAsync(ConversationId, messageId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The Phone Learns It The Next Time The Chat Opens Unread
            _store.LogFailure("mark read", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _store.MessageArrived      -= OnMessageArrived;
        _store.Resynced            -= OnResynced;
        _store.TypingChanged       -= OnTypingChanged;
        _store.ConversationChanged -= OnConversationChanged;
        _typingTimer.Dispose();
        _outgoing.Clear();
        Voice.Cancel();
    }
}
