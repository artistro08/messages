using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// Loads the open chat: the first page, the older pages, the jump to a quoted message and the trim to the newest page.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>
    /// Loads the newest page and merges it into the bubbles, where the unsent bubbles stay. An unread conversation then
    /// turns read.
    /// </summary>
    /// <remarks>
    /// Call on the UI thread. With no bubbles yet, the saved page shows during the phone fetch. A page in memory shows
    /// at once. A page from the database shows unless the phone answered first, and the read runs off the UI thread.
    /// A call during a load waits, and runs when that load ends. A failed load keeps the bubbles as they were. The
    /// failure can be no connection, no answer or a network error.
    /// </remarks>
    /// <returns>A task that completes when the page shows.</returns>
    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            _reloadPending = true;
            return;
        }

        IsLoading        = true;
        IsLoadingHistory = Messages.Count == 0;

        // The chat opened before, also before a restart, so the saved page shows during the phone fetch.
        if (Messages.Count == 0)
        {
            _ = ShowSavedPageAsync(_store.CachedMessagesAsync(ConversationId), _phonePages);
        }

        try
        {
            // The store can lack the conversation when Start chat opened it just now.
            ConversationInfo? conversation = _store.Find(ConversationId) ?? await _store.GetAsync(ConversationId, CancellationToken.None);
            ApplyConversation(conversation);

            (IReadOnlyList<ChatMessage> page, Cursor? older) = await _store.LoadMessagesAsync(ConversationId, null, CancellationToken.None);

            _older = older;
            _phonePages++;
            ShowPage(page);
            _fillOlder = true;
            LogOpenStep("phone page shown");

            // The open of an unread chat reads it on the phone.
            if (conversation is { Unread: true } && conversation.LatestMessageId.Length > 0)
            {
                await _store.MarkReadAsync(ConversationId, conversation.LatestMessageId, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The cause can be no connection, no answer or a network error. The resync after the reconnect loads again.
            _store.LogFailure("load messages", ex);
            _fillOlder = false;
        }
        finally
        {
            IsLoading        = false;
            IsLoadingHistory = false;
        }

        await RunPendingReloadAsync();
    }

    // Shows the saved page unless a phone page showed after the request. The big ring then goes, with no ring in its
    // place. A page in memory completes at once, so it shows before LoadAsync awaits anything.
    private async Task ShowSavedPageAsync(Task<IReadOnlyList<ChatMessage>?> saved, int phonePages)
    {
        if (await saved is not { } page || _phonePages != phonePages)
        {
            return;
        }

        ShowPage(page);
        IsLoadingHistory = false;
        LogOpenStep("saved page shown");
    }

    /// <summary>
    /// Loads the page of older messages above the bubbles.
    /// </summary>
    /// <remarks>Call on the UI thread. The call does nothing during a load or when no older message is left.</remarks>
    /// <returns>A task that completes when the page shows.</returns>
    public async Task LoadOlderAsync()
    {
        if (IsLoading || _older is null)
        {
            return;
        }

        IsLoading      = true;
        IsLoadingOlder = true;
        try
        {
            (IReadOnlyList<ChatMessage> page, Cursor? older) = await _store.LoadMessagesAsync(ConversationId, _older, CancellationToken.None);

            _older = older;

            // The page is oldest first, so its bubbles go in at the top in page order. The loop skips a message that
            // shows already.
            int at = 0;
            foreach (ChatMessage m in page.Where(m => !ChatFormat.IsHiddenLine(m) && ById(m.Id) is null))
            {
                Messages.Insert(at++, NewBubble(m));
            }

            RefreshBubbles();
            _fillOlder = at > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The cause can be no connection, no answer or a network error. A new scroll tries again. The fill waits
            // for the next good load.
            _store.LogFailure("load older messages", ex);
            _fillOlder = false;
        }
        finally
        {
            IsLoading      = false;
            IsLoadingOlder = false;
        }

        await RunPendingReloadAsync();
    }

    /// <summary>
    /// Finds the bubble that a reply quotes, and loads older pages until it shows. A click on the quote jumps to it.
    /// </summary>
    /// <remarks>
    /// Call on the UI thread. The search stops after <see cref="ReplyJumpPages"/> older pages or at the oldest message.
    /// It also stops during another load, or when a page adds nothing, as a failed load does. It never throws.
    /// </remarks>
    /// <param name="messageId">The ID of the quoted message.</param>
    /// <returns>The bubble, or null when the search did not find it.</returns>
    public async Task<MessageBubbleViewModel?> FindForJumpAsync(string messageId)
    {
        if (string.IsNullOrEmpty(messageId))
        {
            return null;
        }

        for (int page = 0; ; page++)
        {
            if (ById(messageId) is { } found)
            {
                return found;
            }

            if (page >= ReplyJumpPages || _older is null || IsLoading)
            {
                return null;
            }

            int before = Messages.Count;
            await LoadOlderAsync();
            if (Messages.Count == before)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Drops the older pages above the newest 200 bubbles, when the list is at the newest message again.
    /// </summary>
    /// <remarks>
    /// Call on the UI thread. The call does nothing during a load or with 200 bubbles or fewer. The next older page
    /// starts from the new oldest bubble, as the fallback cursor of <see cref="ChatStore"/> does. That cursor holds the
    /// time in milliseconds. Thus a scroll up brings the dropped messages back.
    /// </remarks>
    public void TrimToNewest()
    {
        if (IsLoading || Messages.Count <= MaxLoaded)
        {
            return;
        }

        // The list is oldest first. A dropped bubble also drops the upload that it kept.
        while (Messages.Count > MaxLoaded)
        {
            _outgoing.Remove(Messages[0]);
            Messages.RemoveAt(0);
        }

        MessageBubbleViewModel oldest = Messages[0];
        _older     = new Cursor { LastItemID = oldest.Id, LastItemTimestamp = oldest.SentAt.ToUnixTimeMilliseconds() };
        _fillOlder = true;
        _trimmed   = true;
        RefreshBubbles();
    }

    // A resync came during the load. The page can be stale, so the chat loads again.
    private async Task RunPendingReloadAsync()
    {
        if (_reloadPending)
        {
            _reloadPending = false;
            await LoadAsync();
        }
    }
}
