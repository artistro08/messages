using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// Loading the open chat: the first page, older pages, jumping to a quoted message and trimming back to the newest.
/// </summary>
/// <remarks>
/// Part of <see cref="ChatViewModel"/>; the other members are in ChatViewModel.cs.
/// </remarks>
public sealed partial class ChatViewModel
{

    // =========================================================================
    // LOADING
    // =========================================================================

    /// <summary>
    /// Loads the newest page, merging it into the bubbles (unsent ones stay), and marks the conversation read when it was unread.
    /// </summary>
    /// <remarks>Call on the UI thread. With no bubbles yet, the cached page shows alongside the phone fetch: at once from memory, or from the database (read off the UI thread) unless the phone answered first. While a page is loading the call is remembered and runs once that load ends. When the load fails (phone not connected or not answering, a network error) the bubbles stay as they were.</remarks>
    /// <returns>A task that completes when the page is shown.</returns>
    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            _reloadPending = true;
            return;
        }

        IsLoading        = true;
        IsLoadingHistory = Messages.Count == 0;

        // Opened Before (Also Before A Restart): The Saved Copy Shows Alongside The Phone Fetch. A Copy In Memory Shows At
        // Once; The Database Is Read Off The UI Thread And Shows Only If The Phone's Page Hasn't Come First
        if (Messages.Count == 0)
        {
            _ = ShowSavedPageAsync(_store.CachedMessagesAsync(ConversationId), _phonePages);
        }

        try
        {
            // The Conversation May Not Be Cached Yet (Opened Right After Start Chat)
            ConversationInfo? conversation = _store.Find(ConversationId) ?? await _store.GetAsync(ConversationId, CancellationToken.None);
            ApplyConversation(conversation);

            (IReadOnlyList<ChatMessage> page, Cursor? older) = await _store.LoadMessagesAsync(ConversationId, null, CancellationToken.None);

            _older = older;
            _phonePages++;
            ShowPage(page);
            _fillOlder = true;
            LogOpenStep("phone page shown");

            // Opening An Unread Chat Reads It
            if (conversation is { Unread: true } && conversation.LatestMessageId.Length > 0)
            {
                await _store.MarkReadAsync(ConversationId, conversation.LatestMessageId, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not Connected, Not Answering Or A Network Error: The Resync After Reconnecting Reloads
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

    // Shows The Saved Copy Unless A Phone Page Was Shown Since It Was Asked For; Over It The Big Ring Goes Away With No
    // Ring In Its Place. A Copy In Memory Completes At Once, So It Shows Before LoadAsync Awaits Anything
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
    /// <remarks>Call on the UI thread. Does nothing while a page is loading or when there are no older messages.</remarks>
    /// <returns>A task that completes when the page is shown.</returns>
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

            // The Page Is Oldest First, So Each Bubble Goes In Above The Ones Already Placed
            int at = 0;
            foreach (ChatMessage m in page.Where(m => m.Kind != MessageKind.Tombstone))
            {
                Messages.Insert(at++, NewBubble(m));
            }

            RefreshBubbles();
            _fillOlder = at > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not Connected, Not Answering Or A Network Error: Scrolling Again Tries Again, Filling Waits For The Next Good Load
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
    /// Finds the bubble a reply quotes, loading older pages until it shows (a reply quote's click jumps to it).
    /// </summary>
    /// <remarks>
    /// Call on the UI thread. Stops at <see cref="ReplyJumpPages"/> older pages, at the oldest message, when another load
    /// is running, or when a page adds nothing (a failed load included). Never throws.
    /// </remarks>
    /// <param name="messageId">The quoted message's ID.</param>
    /// <returns>The bubble, or null when it wasn't found.</returns>
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
    /// Lets go of the older pages above the newest 200 bubbles (the list is back at the newest message).
    /// </summary>
    /// <remarks>
    /// Call on the UI thread. Does nothing while a page loads or with 200 bubbles or fewer. The next older page starts again
    /// from the new oldest bubble, as <see cref="ChatStore"/>'s own fallback cursor does (its time in milliseconds), so
    /// scrolling up brings the dropped messages back.
    /// </remarks>
    public void TrimToNewest()
    {
        if (IsLoading || Messages.Count <= MaxLoaded)
        {
            return;
        }

        // Oldest First: Drop From The Top, With Any Upload A Dropped Bubble Kept
        while (Messages.Count > MaxLoaded)
        {
            _outgoing.Remove(Messages[0]);
            Messages.RemoveAt(0);
        }

        MessageBubbleViewModel oldest = Messages[0];
        _older     = new Cursor { LastItemID = oldest.Id, LastItemTimestamp = oldest.SentAt.ToUnixTimeMilliseconds() };
        _fillOlder = true;
        RefreshBubbles();
    }

    // A Resync Arrived Mid-Load: The Page Above May Be Stale, So Load Again
    private async Task RunPendingReloadAsync()
    {
        if (_reloadPending)
        {
            _reloadPending = false;
            await LoadAsync();
        }
    }
}
