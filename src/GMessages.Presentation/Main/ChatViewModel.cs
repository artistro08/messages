using System.Collections.ObjectModel;
using System.ComponentModel;
using GMessages.Presentation.Messaging;
using GMessages.Protocol;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;

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
/// A send adds a bubble at once. If the phone cannot take it (not connected, not answering, refused) the bubble is
/// marked failed and keeps its text so <see cref="RetryAsync"/> can resend it; nothing is thrown at the view.
/// </para>
/// </remarks>
public sealed class ChatViewModel : INotifyPropertyChanged, IDisposable
{
    // Gray Used When The Phone Gives No Avatar Color
    private const uint FallbackColor = 0xFF5F6368;

    private readonly ChatStore _store;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;

    // Where The Last Older Page Ended; Null Means No More Pages
    private Cursor? _older;
    private bool _isLoading;

    // A Reload Asked For While A Load Was Running; Runs When That Load Ends
    private bool _reloadPending;
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

    /// <summary>Gets the avatar letter.</summary>
    public string Initial { get; private set; } = string.Empty;

    /// <summary>Gets the avatar color as opaque ARGB.</summary>
    public uint AvatarColor { get; private set; } = FallbackColor;

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

    /// <summary>Gets whether a page is loading.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
            {
                return;
            }

            _isLoading = value;
            Raise(nameof(IsLoading));
        }
    }

    // =========================================================================
    // LOADING
    // =========================================================================

    /// <summary>
    /// Loads the newest page, replacing the bubbles (unsent ones stay), and marks the conversation read when it was unread.
    /// </summary>
    /// <remarks>Call on the UI thread. While a page is loading the call is remembered and runs once that load ends. When the phone is not connected or not answering the bubbles stay as they were.</remarks>
    /// <returns>A task that completes when the page is shown.</returns>
    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            _reloadPending = true;
            return;
        }

        IsLoading = true;
        try
        {
            // The Conversation May Not Be Cached Yet (Opened Right After Start Chat)
            ConversationInfo? conversation = _store.Find(ConversationId) ?? await _store.GetAsync(ConversationId, CancellationToken.None);
            ApplyConversation(conversation);

            (IReadOnlyList<ChatMessage> page, Cursor? older) = await _store.LoadMessagesAsync(ConversationId, null, CancellationToken.None);

            // Unsent Bubbles Stay Unless The Page Already Holds Their Echo
            List<MessageBubbleViewModel> unsent = [.. Messages.Where(b => b.Id.Length == 0 && !page.Any(m => m.TmpId.Length > 0 && m.TmpId == b.TmpId))];

            Messages.Clear();
            _older = older;
            foreach (ChatMessage m in page.Where(m => m.Kind != MessageKind.Tombstone))
            {
                Messages.Add(new MessageBubbleViewModel(m));
            }

            foreach (MessageBubbleViewModel b in unsent)
            {
                Messages.Add(b);
            }

            RefreshSenders();

            // Opening An Unread Chat Reads It
            if (conversation is { Unread: true } && conversation.LatestMessageId.Length > 0)
            {
                await _store.MarkReadAsync(ConversationId, conversation.LatestMessageId, CancellationToken.None);
            }
        }
        catch (Exception ex) when (IsPhoneProblem(ex))
        {
            // Not Connected Or Not Answering: The Resync After Reconnecting Reloads
        }
        finally
        {
            IsLoading = false;
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
            return;
        }

        IsLoading = true;
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
        }
        catch (Exception ex) when (IsPhoneProblem(ex))
        {
            // Not Connected Or Not Answering: Scrolling Again Tries Again
        }
        finally
        {
            IsLoading = false;
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
    /// <remarks>A send the phone can't take marks the bubble failed instead of throwing. Does nothing when <see cref="CanSend"/> is false.</remarks>
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

    // Sends A Bubble's Text; Any Phone Problem Fails The Bubble Instead Of Escaping
    private async Task SendBubbleAsync(MessageBubbleViewModel bubble)
    {
        try
        {
            await _store.SendTextAsync(ConversationId, bubble.Text, bubble.TmpId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is MessageSendException || IsPhoneProblem(ex))
        {
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

        // Match By ID, Then By The Temporary ID Of A Bubble Sent From Here
        MessageBubbleViewModel? bubble = Messages.FirstOrDefault(b => m.Id.Length > 0 && b.Id == m.Id)
            ?? Messages.FirstOrDefault(b => m.TmpId.Length > 0 && b.TmpId == m.TmpId);

        if (bubble is not null)
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

    private void OnResynced(object? sender, EventArgs e) => _post(() => _ = LoadAsync());

    private async Task MarkReadAsync(string messageId)
    {
        try
        {
            await _store.MarkReadAsync(ConversationId, messageId, CancellationToken.None);
        }
        catch (Exception ex) when (IsPhoneProblem(ex))
        {
            // The Phone Learns It The Next Time The Chat Opens Unread
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

    private static bool IsPhoneProblem(Exception ex) => ex is MessagingUnavailableException or PhoneNotRespondingException or ConnectionClosedException;

    private void ApplyConversation(ConversationInfo? c)
    {
        if (c is null)
        {
            return;
        }

        Title       = c.Name;
        Initial     = ChatFormat.Initial(c.Name);
        AvatarColor = ChatFormat.ColorFromHex(c.AvatarHexColor, FallbackColor);
        Raise(string.Empty);
    }

    // Names The Sender On The First Received Bubble Of A Run, Groups Only
    private void RefreshSenders()
    {
        ConversationInfo? c = _store.Find(ConversationId);
        MessageBubbleViewModel? previous = null;

        foreach (MessageBubbleViewModel b in Messages)
        {
            bool first_of_run = previous is null || previous.FromMe || previous.ParticipantId != b.ParticipantId;
            string name       = c is { IsGroup: true } && !b.FromMe && first_of_run ? ChatFormat.SenderName(c.FindParticipant(b.ParticipantId)) : string.Empty;

            b.SetSenderName(name.Length > 0 ? name : null);
            previous = b;
        }
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
