using System.Globalization;
using System.Text;
using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Main;

/// <summary>
/// Keeps the open chat current with the live updates: new messages, typing, resyncs, changes to the conversation and
/// <see cref="Dispose"/>.
/// </summary>
public sealed partial class ChatViewModel
{
    private static readonly CompositeFormat TypingFormat = CompositeFormat.Parse(ChatCopy.TypingFormat);

    private void OnMessageArrived(object? sender, MessageArrivedEventArgs e)
    {
        ChatMessage m = e.Message;
        if (m.ConversationId != ConversationId)
        {
            return;
        }

        // A system line can start or stop the encryption, and thus the lock. Only the name change of a group shows as a
        // line.
        if (m.Kind == MessageKind.Tombstone)
        {
            IsEncrypted = _store.IsEncrypted(ConversationId);
            RefreshStatus(IsEncrypted, 0);
            if (ChatFormat.IsHiddenLine(m))
            {
                return;
            }
        }

        // Only a message new to this PC animates in. The phone also sends the recent history of a chat as live
        // messages. It does so on a catch-up and after Start chat opens a chat. That history animated in as a second
        // load, 1 bubble at a time. Now it goes in with no animation, in time order.
        MessageBubbleViewModel? bubble = FindBubble(m);
        if (bubble is not null)
        {
            bubble.Update(m);
            Index(bubble);
        }
        else
        {
            // After a trim, a message older than the oldest bubble is a reaction or a status on a trimmed message. The
            // store saved it, and the older page puts it back in its place. Catch-up history before a trim still goes
            // in above.
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

        if (!m.FromMe && m.ParticipantId == _typingParticipant)
        {
            StopTyping();
        }

        // The user sees the message in the focused chat, so the chat marks it read on the phone.
        if (IsActive && !m.FromMe && m.Id.Length > 0)
        {
            _ = MarkReadAsync(m.Id);
        }
    }

    // The post can run after the chat closed. Then it does nothing.
    private void OnResynced(object? sender, EventArgs e) => _post(() =>
    {
        if (!_disposed)
        {
            _ = LoadAsync();
        }
    });

    // Each new message, mute and move of the conversation comes here. Thus each property raises only when its value
    // changes.
    private void OnConversationChanged(object? sender, ConversationInfo c)
    {
        if (c.Id != ConversationId)
        {
            return;
        }

        bool couldMute    = CanMute;
        string wasHolder  = DraftPlaceholder;
        AvatarParts shown = new(Initial, AvatarColor, PhotoId, SecondInitial, SecondColor, SecondPhotoId);

        // A rename or a new avatar changes the header. Only then do all the properties raise.
        if (c.Name != Title || AvatarParts.Of(c) != shown)
        {
            ApplyConversation(c);
        }

        Conversation = c;

        if (wasHolder != DraftPlaceholder)
        {
            Raise(nameof(DraftPlaceholder));
        }

        // The conversation from the phone also carries the encryption state.
        bool encrypted = _store.IsEncrypted(ConversationId);
        if (encrypted != IsEncrypted)
        {
            IsEncrypted = encrypted;
            RefreshStatus(encrypted, 0);
        }

        if (IsMuted != c.IsMuted)
        {
            IsMuted = c.IsMuted;
            Raise(nameof(IsMuted));
        }

        if (couldMute != CanMute)
        {
            Raise(nameof(CanMute));
        }

        // A participant joined, left or got a new name. The sender, the avatar and the reactions of each bubble change.
        if (!SameForBubbles(_bubblesFor, c))
        {
            RefreshBubbles();
        }
    }

    // A start shows the dots for 15 seconds at most. A stop hides them at once.
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

        if (e.ParticipantId != _typingParticipant)
        {
            ShowTyper(e.ParticipantId);
        }

        _typingParticipant = e.ParticipantId;
        _typingUntil       = _time.GetUtcNow() + TypingTimeout;
        IsTyping           = true;
        _typingTimer.Change(TypingTimeout, Timeout.InfiniteTimeSpan);
    }

    // Shows who types, with an avatar beside the dots. A group shows the avatar of the person, as beside their bubbles.
    // A one-on-one chat shows the avatar of the conversation, as in the header. Screen readers hear the name.
    private void ShowTyper(string participantId)
    {
        ConversationInfo? c    = _store.Find(ConversationId);
        ParticipantInfo? typer = c?.FindParticipant(participantId);
        string name            = ChatFormat.SenderName(typer);
        TypingName             = name.Length > 0 ? string.Format(CultureInfo.CurrentCulture, TypingFormat, name) : ChatCopy.Typing;

        if (c is { IsGroup: false })
        {
            AvatarParts avatar = AvatarParts.Of(c);
            TypingInitial      = avatar.Initial;
            TypingColor        = avatar.Color;
            TypingPhotoId      = avatar.PhotoId;
        }
        else
        {
            TypingInitial = ChatFormat.Initial(name);
            TypingColor   = AvatarParts.ColorOf(typer);
            TypingPhotoId = participantId;
        }

        Raise(nameof(TypingInitial));
        Raise(nameof(TypingColor));
        Raise(nameof(TypingPhotoId));
        Raise(nameof(TypingName));
    }

    // A start after the timer began moves the deadline. The post can run after Dispose disposed the timer.
    private void OnTypingTimeout()
    {
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
            // The phone learns it the next time that the chat opens unread.
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
