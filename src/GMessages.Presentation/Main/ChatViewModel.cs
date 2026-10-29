using System.Collections.ObjectModel;
using System.ComponentModel;
using GMessages.Presentation.Messaging;
using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// One open conversation: its bubbles, the draft, sending with retry, and live updates.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ChatStore"/> raises its events on the UI thread, so bubbles change there. The view model subscribes to
/// the store (which outlives the window), so call <see cref="Dispose"/> when the chat closes.
/// </para>
/// <para>
/// Opening a conversation shows the page <see cref="ChatStore"/> kept from last time at once, then the phone's page
/// is merged into it quietly. Only with nothing to show does <see cref="IsLoadingHistory"/> ask for the loading ring.
/// </para>
/// <para>
/// A send adds a bubble at once. If it fails for any reason (not connected, not answering, refused, a network error) the bubble is
/// marked failed and keeps its text so <see cref="RetryAsync"/> can resend it; nothing is thrown at the view.
/// </para>
/// </remarks>
public sealed class ChatViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ChatStore _store;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;

    // Where The Last Older Page Ended; Null Means No More Pages
    private Cursor? _older;

    // Whether A Too-Short List May Load The Next Older Page On Its Own: Once After The Newest Page, Then Only After An
    // Older Page That Added Bubbles. A Failed Load Stops It Until The Next Good Load (The Resync After Reconnecting)
    private bool _fillOlder;
    private bool _isLoading;
    private bool _isLoadingHistory;
    private bool _isLoadingOlder;

    // A Reload Asked For While A Load Was Running; Runs When That Load Ends
    private bool _reloadPending;

    // The Last Reason An Older Load Was Skipped (Wave 7 Diagnostics)
    private string? _lastSkip;
    private string _draft = string.Empty;

    /// <summary>
    /// Creates an empty chat and starts listening to the store.
    /// </summary>
    /// <param name="store">Loads and sends messages and raises live updates.</param>
    /// <param name="conversationId">The conversation to show.</param>
    /// <param name="time">Gives the current time.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ChatViewModel(ChatStore store, string conversationId, TimeProvider time, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(post);

        _store         = store;
        ConversationId = conversationId;
        _time          = time;
        _post          = post;

        _store.MessageArrived += OnMessageArrived;
        _store.Resynced       += OnResynced;
        ApplyConversation(_store.Find(conversationId));
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the conversation ID.</summary>
    public string ConversationId { get; }

    /// <summary>Gets the conversation name.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Gets the avatar letter: the conversation's, or a group's first other person's.</summary>
    public string Initial { get; private set; } = string.Empty;

    /// <summary>Gets the avatar color as opaque ARGB.</summary>
    public uint AvatarColor { get; private set; } = AvatarParts.FallbackColor;

    /// <summary>Gets the participant whose photo covers the avatar (the first other person), or empty.</summary>
    public string PhotoId { get; private set; } = string.Empty;

    /// <summary>Gets the back circle's letter: the second other person of a group, else empty.</summary>
    public string SecondInitial { get; private set; } = string.Empty;

    /// <summary>Gets the back circle's color as opaque ARGB; 0 (no back circle) unless a group with two others.</summary>
    public uint SecondColor { get; private set; }

    /// <summary>Gets the participant whose photo covers the back circle, or empty.</summary>
    public string SecondPhotoId { get; private set; } = string.Empty;

    /// <summary>Gets the bubbles, oldest first.</summary>
    public ObservableCollection<MessageBubbleViewModel> Messages { get; } = [];

    /// <summary>Gets or sets the text being typed.</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            _draft = value ?? string.Empty;
            Raise(nameof(Draft));
            Raise(nameof(CanSend));
        }
    }

    /// <summary>Gets whether the draft has something to send (not empty or only spaces).</summary>
    public bool CanSend => !string.IsNullOrWhiteSpace(_draft);

    /// <summary>Gets or sets whether the window is focused with this chat open, so incoming messages count as read (set by <see cref="MainViewModel"/>).</summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Gets whether a list too short to scroll should load the next older page on its own.
    /// </summary>
    /// <remarks>
    /// True once after the newest page loads, then only after an older page that added bubbles, while older pages are
    /// left. A failed load (phone not connected or not answering, a network error) stops it until the next successful newest page, so
    /// the view never spins on a phone that can't answer.
    /// </remarks>
    public bool CanFillOlder => _fillOlder && _older is not null;

    /// <summary>Gets whether a page is loading.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value, nameof(IsLoading));
    }

    /// <summary>Gets whether the newest page is loading with no bubbles to show yet (no cached copy): the large ring.</summary>
    public bool IsLoadingHistory
    {
        get => _isLoadingHistory;
        private set => Set(ref _isLoadingHistory, value, nameof(IsLoadingHistory));
    }

    /// <summary>Gets whether an older page is loading above the bubbles: the small ring at the top.</summary>
    public bool IsLoadingOlder
    {
        get => _isLoadingOlder;
        private set => Set(ref _isLoadingOlder, value, nameof(IsLoadingOlder));
    }

    // =========================================================================
    // LOADING
    // =========================================================================

    /// <summary>
    /// Loads the newest page, merging it into the bubbles (unsent ones stay), and marks the conversation read when it was unread.
    /// </summary>
    /// <remarks>Call on the UI thread. With no bubbles yet, the cached page shows first, before anything is awaited. While a page is loading the call is remembered and runs once that load ends. When the load fails (phone not connected or not answering, a network error) the bubbles stay as they were.</remarks>
    /// <returns>A task that completes when the page is shown.</returns>
    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            _reloadPending = true;
            return;
        }

        // Opened Before: The Copy Kept From Then Shows At Once, And The Phone's Page Is Merged In Below
        if (Messages.Count == 0 && _store.CachedMessages(ConversationId) is { } cached)
        {
            ShowPage(cached);
        }

        IsLoading        = true;
        IsLoadingHistory = Messages.Count == 0;
        Trace($"load started ({Messages.Count} shown)");
        try
        {
            // The Conversation May Not Be Cached Yet (Opened Right After Start Chat)
            ConversationInfo? conversation = _store.Find(ConversationId) ?? await _store.GetAsync(ConversationId, CancellationToken.None);
            ApplyConversation(conversation);

            (IReadOnlyList<ChatMessage> page, Cursor? older) = await _store.LoadMessagesAsync(ConversationId, null, CancellationToken.None);

            _older = older;
            ShowPage(page);
            _fillOlder = true;

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
            Trace($"load ended ({Messages.Count} shown, older {(_older is null ? "none" : "set")})");
        }

        await RunPendingReloadAsync();
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
            // TODO: Wave 7 Diagnostics; Remove Once Older Paging Works On The Real Phone. Logged When The Reason Changes,
            // Not On Every Scroll
            string skipped = IsLoading ? "older skipped: loading" : "older skipped: no older pages";
            if (skipped != _lastSkip)
            {
                _lastSkip = skipped;
                Trace(skipped);
            }

            return;
        }

        _lastSkip      = null;
        IsLoading      = true;
        IsLoadingOlder = true;
        Trace($"older started ({Messages.Count} shown)");
        try
        {
            (IReadOnlyList<ChatMessage> page, Cursor? older) = await _store.LoadMessagesAsync(ConversationId, _older, CancellationToken.None);

            _older = older;

            // The Page Is Oldest First, So Each Bubble Goes In Above The Ones Already Placed
            int at = 0;
            foreach (ChatMessage m in page.Where(m => m.Kind != MessageKind.Tombstone))
            {
                Messages.Insert(at++, new MessageBubbleViewModel(m));
            }

            RefreshSenders();
            _fillOlder = at > 0;
            Trace($"older added {at}");
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

    // A Resync Arrived Mid-Load: The Page Above May Be Stale, So Load Again
    private async Task RunPendingReloadAsync()
    {
        if (_reloadPending)
        {
            _reloadPending = false;
            await LoadAsync();
        }
    }

    // =========================================================================
    // SENDING
    // =========================================================================

    /// <summary>
    /// Sends the draft: a bubble appears at once and the draft clears.
    /// </summary>
    /// <remarks>A send that fails for any reason marks the bubble failed instead of throwing. Does nothing when <see cref="CanSend"/> is false.</remarks>
    /// <returns>A task that completes when the phone answered or the bubble failed.</returns>
    public async Task SendAsync()
    {
        if (!CanSend)
        {
            return;
        }

        string text   = Draft;
        string tmp_id = GmClient.NewTmpId();

        Draft = string.Empty;

        // Optimistic Bubble
        var pending = new ChatMessage(string.Empty, tmp_id, ConversationId, string.Empty, _time.GetUtcNow(), true, MessageKind.Text, text, MessageStatusType.OutgoingSending);
        var bubble  = new MessageBubbleViewModel(pending);
        Messages.Add(bubble);
        RefreshSenders();

        await SendBubbleAsync(bubble);
    }

    /// <summary>
    /// Sends a failed bubble's text again with a new temporary ID.
    /// </summary>
    /// <param name="failed">The bubble that failed.</param>
    /// <returns>A task that completes when the phone answered or the bubble failed again.</returns>
    public async Task RetryAsync(MessageBubbleViewModel failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        if (!failed.IsFailed || !Messages.Contains(failed))
        {
            return;
        }

        failed.MarkSending(GmClient.NewTmpId());
        await SendBubbleAsync(failed);
    }

    // Sends A Bubble's Text; Any Failure (Phone, Refusal, Network, Timeout, Decode) Fails The Bubble Instead Of Escaping
    private async Task SendBubbleAsync(MessageBubbleViewModel bubble)
    {
        try
        {
            await _store.SendTextAsync(ConversationId, bubble.Text, bubble.TmpId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _store.LogFailure("send", ex);
            bubble.MarkFailed();
        }
    }

    // =========================================================================
    // LIVE UPDATES
    // =========================================================================

    private void OnMessageArrived(object? sender, MessageArrivedEventArgs e)
    {
        ChatMessage m = e.Message;
        if (m.ConversationId != ConversationId || m.Kind == MessageKind.Tombstone)
        {
            return;
        }

        if (FindBubble(m) is { } bubble)
        {
            bubble.Update(m);
        }
        else
        {
            Messages.Add(new MessageBubbleViewModel(m));
            RefreshSenders();
        }

        // Reading Along: Tell The Phone
        if (IsActive && !m.FromMe && m.Id.Length > 0)
        {
            _ = MarkReadAsync(m.Id);
        }
    }

    private void OnResynced(object? sender, EventArgs e) => _post(() =>
    {
        Trace("resync reload");
        _ = LoadAsync();
    });

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
        _store.MessageArrived -= OnMessageArrived;
        _store.Resynced       -= OnResynced;
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>
    /// Logs a step of this chat's loading or scrolling: IDs and numbers only, never message content.
    /// </summary>
    /// <remarks>TODO: Wave 7 diagnostics; remove once older paging works on the real phone.</remarks>
    /// <param name="step">What happened, such as "scroller hooked".</param>
    public void Trace(string step) => _store.LogChatStep(ConversationId, step);

    // Match By ID, Then By The Temporary ID Of A Bubble Sent From Here
    private MessageBubbleViewModel? FindBubble(ChatMessage m) =>
        Messages.FirstOrDefault(b => m.Id.Length > 0 && b.Id == m.Id)
        ?? Messages.FirstOrDefault(b => m.TmpId.Length > 0 && b.TmpId == m.TmpId);

    // Shows The Newest Page In Place Of The Bubbles, Reusing (And Updating) The Ones Already Shown So The List Doesn't
    // Redraw Them. Unsent Bubbles, And Ones That Arrived While The Page Was On Its Way (Newer Than Anything In It),
    // Stay At The Bottom Unless The Page Holds Them; Older Pages Above Are Dropped
    private void ShowPage(IReadOnlyList<ChatMessage> page)
    {
        List<MessageBubbleViewModel> wanted = [];
        foreach (ChatMessage m in page.Where(m => m.Kind != MessageKind.Tombstone))
        {
            MessageBubbleViewModel? bubble = FindBubble(m);
            if (bubble is null || wanted.Contains(bubble))
            {
                bubble = new MessageBubbleViewModel(m);
            }
            else
            {
                bubble.Update(m);
            }

            wanted.Add(bubble);
        }

        DateTimeOffset? newest = page.Count > 0 ? page.Max(m => m.Time) : null;
        wanted.AddRange([.. Messages
            .Where(b => !wanted.Contains(b) && (b.Id.Length == 0 || b.SentAt > newest))
            .OrderBy(b => b.SentAt)]);

        // Drop What The Page No Longer Holds, Then Put Each Bubble In Its Place
        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Messages[i]))
            {
                Messages.RemoveAt(i);
            }
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            int at = Messages.IndexOf(wanted[i]);
            if (at < 0)
            {
                Messages.Insert(i, wanted[i]);
            }
            else if (at != i)
            {
                Messages.Move(at, i);
            }
        }

        RefreshSenders();
    }

    private void ApplyConversation(ConversationInfo? c)
    {
        if (c is null)
        {
            return;
        }

        // Avatar: Stacked People For A Group (R12), The Same As The List Row
        AvatarParts avatar = AvatarParts.Of(c);
        Title              = c.Name;
        Initial            = avatar.Initial;
        AvatarColor        = avatar.Color;
        PhotoId            = avatar.PhotoId;
        SecondInitial      = avatar.SecondInitial;
        SecondColor        = avatar.SecondColor;
        SecondPhotoId      = avatar.SecondPhotoId;
        Raise(string.Empty);
    }

    // Names The Sender On The First Received Bubble Of A Run, Groups Only
    private void RefreshSenders()
    {
        ConversationInfo? c = _store.Find(ConversationId);
        MessageBubbleViewModel? previous = null;

        foreach (MessageBubbleViewModel b in Messages)
        {
            bool first_of_run       = previous is null || previous.FromMe || previous.ParticipantId != b.ParticipantId;
            ParticipantInfo? sender = c?.FindParticipant(b.ParticipantId);
            string name             = c is { IsGroup: true } && !b.FromMe && first_of_run ? ChatFormat.SenderName(sender) : string.Empty;

            // The Name Takes The Color Of The Sender's Letter Circle
            b.SetSender(name.Length > 0 ? name : null, AvatarParts.ColorOf(sender));
            previous = b;
        }
    }

    private void Set(ref bool field, bool value, string property)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        Raise(property);
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
