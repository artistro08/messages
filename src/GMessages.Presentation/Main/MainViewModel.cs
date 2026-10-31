using System.ComponentModel;
using GMessages.Presentation.Connection;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Events;

namespace GMessages.Presentation.Main;

/// <summary>
/// The main window's state: the inbox and folder lists, which conversation is open, the narrow layout, conversation
/// actions and the banners.
/// </summary>
/// <remarks>
/// <para>
/// Created when the window opens and dropped when it closes; call <see cref="Dispose"/> then, because the connection
/// and the store outlive it. The view sets <see cref="IsNarrow"/> and <see cref="IsWindowFocused"/>.
/// </para>
/// <para>
/// Each change raises <see cref="PropertyChanged"/> once with an empty property name, which tells <c>x:Bind</c> to
/// re-read every property. Call everything on the UI thread.
/// </para>
/// </remarks>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    private readonly ConnectionController _connection;
    private readonly ChatStore _store;
    private readonly TimeProvider _time;

    // The Archived Conversation The Undo Bar Offers To Unarchive, And The Timer That Hides It
    private string? _undo;
    private ITimer? _undoTimer;
    private readonly Action<Action> _post;

    // Fetches Link Preview Cards For The Open Chat; Null Means None
    private readonly LinkPreviewer? _previews;

    // Each Conversation's Unsent Draft While The Window Is Open, So Escape Or Another Chat Loses Nothing (Memory Only)
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);

    // The Folder Lists (Spam And Blocked, Archived), Made The First Time Each Opens
    private readonly Dictionary<ConversationFolder, ConversationListViewModel> _folders = [];

    // Conversations With An Action Waiting On The Phone, So A Double Click Sends One Request
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    private ConnectionState _state;
    private bool _phoneSilent;

    // Battery Banner: The Store's State, And Whether The User Closed The Banner Since The Last Low Alert
    private bool _batteryLow;
    private bool _batteryDismissed;

    // Closed With The Window: Late Callbacks (A Start Chat That Finishes After Closing) Must Not Open Anything
    private bool _disposed;

    /// <summary>
    /// Creates the view model with the inbox list (not loaded yet) and starts watching the connection.
    /// </summary>
    /// <param name="connection">Gives the connection state and the phone's live events.</param>
    /// <param name="store">Loads the conversations.</param>
    /// <param name="time">Gives the current time for the list times.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <param name="previews">Fetches link preview cards for the open chat; null for none. The caller owns it.</param>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="previews"/> is null.</exception>
    public MainViewModel(ConnectionController connection, ChatStore store, TimeProvider time, Action<Action> post, LinkPreviewer? previews = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(post);

        _connection = connection;
        _store      = store;
        _time       = time;
        _post       = post;
        _previews   = previews;
        _state      = connection.State;
        Inbox       = new ConversationListViewModel(store, ConversationFolder.Inbox, time, post);

        _connection.StateChanged += OnStateChanged;
        _connection.ClientEvent  += OnClientEvent;

        _batteryLow = store.IsPhoneBatteryLow;
        _store.PhoneBatteryChanged    += OnPhoneBatteryChanged;
        _store.PhoneBatteryLowAlerted += OnPhoneBatteryLowAlerted;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised when the connection arrives at <see cref="ConnectionState.Connected"/>, so the window can retry what
    /// failed while it was down (photos). The lists reload then by themselves, and again when the phone answers after
    /// a silence.
    /// </summary>
    public event EventHandler? Connected;

    /// <summary>Gets the inbox list.</summary>
    public ConversationListViewModel Inbox { get; }

    /// <summary>Gets the spam and blocked list, or null until it is first opened.</summary>
    public ConversationListViewModel? SpamBlocked => _folders.GetValueOrDefault(ConversationFolder.SpamBlocked);

    /// <summary>Gets the folder whose list shows now.</summary>
    public ConversationFolder Folder { get; private set; }

    /// <summary>Gets whether the spam and blocked list shows instead of the inbox.</summary>
    public bool IsSpamView => Folder == ConversationFolder.SpamBlocked;

    /// <summary>Gets whether the archived list shows instead of the inbox.</summary>
    public bool IsArchivedView => Folder == ConversationFolder.Archived;

    /// <summary>Gets whether a folder list shows instead of the inbox (the Back button shows).</summary>
    public bool IsFolderView => Folder != ConversationFolder.Inbox;

    /// <summary>Gets the list that shows now.</summary>
    public ConversationListViewModel CurrentList => _folders.GetValueOrDefault(Folder) ?? Inbox;

    /// <summary>Gets the open conversation's ID, or null when none is open.</summary>
    public string? SelectedConversationId { get; private set; }

    /// <summary>Gets whether a conversation is open (for bindings, which skip their function when an argument is null).</summary>
    public bool IsChatOpen => SelectedConversationId is not null;

    /// <summary>Gets whether the list is collapsed to avatars.</summary>
    public bool IsListCollapsed { get; private set; }

    /// <summary>Gets or sets whether the window is narrow (set by the view).</summary>
    public bool IsNarrow
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    /// <summary>Gets or sets whether the window has focus (set by the view).</summary>
    public bool IsWindowFocused
    {
        get;
        set
        {
            field = value;
            UpdateChatActive();
        }
    }

    /// <summary>Gets the open conversation's chat, or null when none is open.</summary>
    public ChatViewModel? Chat { get; private set; }

    /// <summary>Gets the phone owner's participant ID (for the profile picture), or null until a conversation names them.</summary>
    public string? MeId => _store.Me?.Id;

    /// <summary>Gets whether Start chat shows in place of a conversation.</summary>
    public bool IsStartChatOpen { get; private set; }

    /// <summary>Gets the Start chat pane's state while it is open, or null.</summary>
    public StartChatViewModel? StartChat { get; private set; }

    /// <summary>Gets whether the chat pane shows instead of the list (narrow only: a conversation or Start chat is open).</summary>
    public bool ShowChatPane => IsNarrow && (SelectedConversationId is not null || IsStartChatOpen);

    /// <summary>Gets whether the connection banner shows (for bindings, which skip their function when an argument is null).</summary>
    public bool HasStatusBanner => StatusBanner is not null;

    /// <summary>Gets the connection banner text, or null when connected and the phone answers.</summary>
    public string? StatusBanner => _state switch
    {
        ConnectionState.Connecting or ConnectionState.Reconnecting => ChatCopy.Connecting,
        ConnectionState.Connected when _phoneSilent                => ChatCopy.WaitingForPhone,
        _                                                          => null,
    };

    /// <summary>Gets the failure banner text after a conversation action failed, or null.</summary>
    public string? ActionError { get; private set; }

    /// <summary>Gets whether the failure banner shows (for bindings, which skip their function when an argument is null).</summary>
    public bool HasActionError => ActionError is not null;

    /// <summary>
    /// Gets whether the low battery banner shows: the phone reported a low battery live (its level isn't known at
    /// start) and the user hasn't closed the banner since the last low alert.
    /// </summary>
    public bool IsPhoneBatteryLow => _batteryLow && !_batteryDismissed;

    /// <summary>Hides the battery banner until the next low battery alert (the banner's own close button).</summary>
    public void DismissPhoneBattery()
    {
        // The Bar Already Closed Itself; Clearing This Lets The Next Low Alert Open It Again
        _batteryDismissed = true;
        Changed();
    }

    // Reads The Store's State On The UI Thread
    private void OnPhoneBatteryChanged(object? sender, EventArgs e) => _post(() =>
    {
        if (_disposed)
        {
            return;
        }

        _batteryLow = _store.IsPhoneBatteryLow;
        Changed();
    });

    // Every Low Alert, Even A Repeat, Brings Back A Banner The User Closed
    private void OnPhoneBatteryLowAlerted(object? sender, EventArgs e) => _post(() =>
    {
        if (_disposed)
        {
            return;
        }

        _batteryDismissed = false;
        Changed();
    });

    // =========================================================================
    // NAVIGATION
    // =========================================================================

    /// <summary>Shows a folder's list, creating and loading it the first time.</summary>
    /// <remarks>
    /// A narrow window shows one pane, so an open chat (or Start chat) closes first, as Escape does, and the folder's
    /// list shows. A wide window keeps the open chat beside the list.
    /// </remarks>
    /// <param name="folder">The folder; <see cref="ConversationFolder.Inbox"/> shows the inbox.</param>
    public void OpenFolder(ConversationFolder folder)
    {
        if (_disposed)
        {
            return;
        }

        // Narrow: The Folder's List Takes The Chat's Place
        if (ShowChatPane)
        {
            ExitConversation();
        }

        if (folder != ConversationFolder.Inbox && !_folders.ContainsKey(folder))
        {
            var list         = new ConversationListViewModel(_store, folder, _time, _post);
            _folders[folder] = list;
            _ = list.LoadAsync();
        }

        Folder = folder;
        Changed();
    }

    /// <summary>Shows the spam and blocked list, creating and loading it the first time.</summary>
    public void OpenSpamBlocked() => OpenFolder(ConversationFolder.SpamBlocked);

    /// <summary>Shows the archived list, creating and loading it the first time.</summary>
    public void OpenArchived() => OpenFolder(ConversationFolder.Archived);

    /// <summary>Shows the inbox again.</summary>
    public void BackToInbox() => OpenFolder(ConversationFolder.Inbox);

    /// <summary>Opens a conversation (closing Start chat).</summary>
    /// <remarks>Does nothing once disposed, so a Start chat that finishes after the window closed opens no chat.</remarks>
    /// <param name="conversationId">The conversation ID.</param>
    public void Select(string conversationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        if (_disposed)
        {
            return;
        }

        SelectedConversationId = conversationId;
        IsStartChatOpen        = false;
        StartChat              = null;

        // Picking The Open Conversation Again Keeps Its Chat (And The Draft)
        if (Chat?.ConversationId != conversationId)
        {
            ReplaceChat(conversationId);
        }

        UpdateChatActive();
        Changed();
    }

    /// <summary>Escape: closes the open conversation or Start chat; in narrow mode that shows the list. The draft is kept for the next open.</summary>
    public void ExitConversation()
    {
        SelectedConversationId = null;
        IsStartChatOpen        = false;
        StartChat              = null;
        ReplaceChat(null);
        Changed();
    }

    /// <summary>Opens the conversation below the open one in the current list (the last one stays open; none open selects the first).</summary>
    public void NextConversation() => MoveSelection(1);

    /// <summary>Opens the conversation above the open one in the current list (the first one stays open).</summary>
    public void PreviousConversation() => MoveSelection(-1);

    /// <summary>Shows Start chat in place of the open conversation, loading the contacts the first time.</summary>
    public void OpenStartChat()
    {
        if (_disposed)
        {
            return;
        }

        SelectedConversationId = null;
        IsStartChatOpen        = true;
        ReplaceChat(null);

        // Already Open: Keep What Was Typed And Picked
        if (StartChat is null)
        {
            StartChat = new StartChatViewModel(_store, _post);
            _ = StartChat.LoadAsync();
        }

        Changed();
    }

    /// <summary>Collapses the list to avatars, or expands it again.</summary>
    public void ToggleList()
    {
        IsListCollapsed = !IsListCollapsed;
        Changed();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _undoTimer?.Dispose();
        _connection.StateChanged -= OnStateChanged;
        _connection.ClientEvent  -= OnClientEvent;
        _store.PhoneBatteryChanged    -= OnPhoneBatteryChanged;
        _store.PhoneBatteryLowAlerted -= OnPhoneBatteryLowAlerted;
        Inbox.Dispose();
        foreach (ConversationListViewModel list in _folders.Values)
        {
            list.Dispose();
        }

        Chat?.Dispose();
    }

    // Disposes The Open Chat And Opens One For The New Selection (Null Closes It). The Closing Chat's Draft Is Kept (An
    // Empty One Forgotten) And The Opening Chat's Put Back
    private void ReplaceChat(string? conversationId)
    {
        if (Chat is { } closing)
        {
            if (closing.Draft.Length > 0)
            {
                _drafts[closing.ConversationId] = closing.Draft;
            }
            else
            {
                _drafts.Remove(closing.ConversationId);
            }

            closing.Dispose();
        }

        Chat = null;

        if (conversationId is not null)
        {
            Chat = new ChatViewModel(_store, conversationId, _time, _post, _previews);
            if (_drafts.TryGetValue(conversationId, out string? draft))
            {
                Chat.RestoreDraft(draft);
            }

            _ = Chat.LoadAsync();
        }

        UpdateChatActive();
    }

    private void UpdateChatActive()
    {
        if (Chat is not null)
        {
            Chat.IsActive = IsWindowFocused && Chat.ConversationId == SelectedConversationId;
        }
    }

    // Moves The Open Conversation By One Row, Stopping At Either End
    private void MoveSelection(int step)
    {
        var rows = CurrentList.Rows;
        if (rows.Count == 0)
        {
            return;
        }

        int current = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Id == SelectedConversationId)
            {
                current = i;
                break;
            }
        }

        int next = current < 0 ? 0 : Math.Clamp(current + step, 0, rows.Count - 1);
        Select(rows[next].Id);
    }

    // =========================================================================
    // CONVERSATION ACTIONS
    // =========================================================================

    /// <summary>Finds a conversation the store knows, for its menu and Details.</summary>
    /// <param name="id">The conversation ID.</param>
    /// <returns>The conversation, or null when the store doesn't know it.</returns>
    public ConversationInfo? FindConversation(string id) => _store.Find(id);

    /// <summary>
    /// Archives, unarchives, mutes, unmutes, blocks, unblocks or deletes a conversation on the phone.
    /// </summary>
    /// <remarks>
    /// Call on the UI thread. While a run for the same conversation waits on the phone, another returns false at once
    /// and sends nothing. Any failure (not connected, refused, not answering, a network error) is logged and shows
    /// <see cref="ActionError"/>; nothing is thrown at the view. A success clears the banner, and closes the open chat
    /// when the action moves it out of its folder (every action but mute and unmute).
    /// </remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="action">What to do.</param>
    /// <returns>True when the phone made the change.</returns>
    public Task<bool> RunAsync(string conversationId, ConversationAction action) => RunAsync(conversationId, action, offerUndo: true);

    // Runs The Action; An Archive Offers Undo Unless It Is The Undo Itself
    private async Task<bool> RunAsync(string conversationId, ConversationAction action, bool offerUndo)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        // Closed, Or Already Running For This Conversation
        if (_disposed || !_running.Add(conversationId))
        {
            return false;
        }

        try
        {
            await _store.UpdateConversationAsync(conversationId, action, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any Failure Is Shown, Never Thrown At The Click Handler
            _store.LogFailure("update conversation", ex);
            ShowActionError(ChatCopy.ActionFailed);
            return false;
        }
        finally
        {
            _running.Remove(conversationId);
        }

        // Closed While The Phone Answered
        if (_disposed)
        {
            return true;
        }

        ActionError = null;

        // An Archive Offers Undo For A Few Seconds; Another Action Replaces The Offer
        if (offerUndo)
        {
            ShowUndo(conversationId, action);
        }

        // The Open Chat Left Its Folder
        bool moved = action is not (ConversationAction.Mute or ConversationAction.Unmute);
        if (moved && conversationId == SelectedConversationId)
        {
            ExitConversation();
        }
        else
        {
            Changed();
        }

        return true;
    }

    /// <summary>Hides the failure banner.</summary>
    public void DismissActionError() => ShowActionError(null);

    /// <summary>How long the Undo bar shows after an archive.</summary>
    public static readonly TimeSpan UndoTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets what the Undo bar says ("Conversation archived"), or null while it is hidden. Only archiving offers Undo
    /// (owner: one way only).
    /// </summary>
    public string? UndoNotice { get; private set; }

    /// <summary>Gets whether the Undo bar shows.</summary>
    public bool HasUndo => UndoNotice is not null;

    /// <summary>
    /// Undoes the archive the Undo bar offers: an unarchive, through <see cref="RunAsync"/>, so the row goes back to its
    /// place in the inbox. The bar hides at once.
    /// </summary>
    /// <returns>True when the phone made the change.</returns>
    public Task<bool> UndoAsync()
    {
        if (_undo is not { } id)
        {
            return Task.FromResult(false);
        }

        HideUndo();
        return RunAsync(id, ConversationAction.Unarchive, offerUndo: false);
    }

    /// <summary>Hides the Undo bar.</summary>
    public void DismissUndo() => HideUndo();

    // Shows The Undo Bar For An Archive (Replacing One Showing), Hidden Again After UndoTime; Other Actions Hide It
    private void ShowUndo(string conversationId, ConversationAction action)
    {
        _undoTimer?.Dispose();
        _undoTimer = null;
        if (action != ConversationAction.Archive)
        {
            HideUndo();
            return;
        }

        _undo      = conversationId;
        UndoNotice = ChatCopy.ConversationArchived;
        _undoTimer = _time.CreateTimer(_ => _post(() =>
        {
            if (_undo == conversationId)
            {
                HideUndo();
            }
        }), null, UndoTime, Timeout.InfiniteTimeSpan);
    }

    private void HideUndo()
    {
        _undoTimer?.Dispose();
        _undoTimer = null;
        _undo      = null;
        if (UndoNotice is null || _disposed)
        {
            return;
        }

        UndoNotice = null;
        Changed();
    }

    // Sets The Failure Banner (Null Hides It); Nothing Once Closed
    private void ShowActionError(string? error)
    {
        if (_disposed)
        {
            return;
        }

        ActionError = error;
        Changed();
    }

    // =========================================================================
    // CONNECTION BANNER
    // =========================================================================

    // The Controller Raises StateChanged Through Its Own Post; Read The State Again On The UI Thread
    private void OnStateChanged(object? sender, EventArgs e) => _post(() => ApplyState(_connection.State));

    // Client Events Are Already Posted To The UI Thread By The Controller
    private void OnClientEvent(object? sender, object e) => ApplyEvent(e);

    internal void ApplyState(ConnectionState state)
    {
        if (_disposed)
        {
            return;
        }

        // A New State Starts Fresh: The Phone Is Not Silent Until It Says So
        bool arrived = _state != state;
        if (arrived)
        {
            _phoneSilent = false;
        }

        _state = state;
        Changed();

        if (arrived && state == ConnectionState.Connected)
        {
            ReloadLists();
            Connected?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void ApplyEvent(object e)
    {
        if (_disposed)
        {
            return;
        }

        switch (e)
        {
            case PhoneNotResponding:
                _phoneSilent = true;
                Changed();
                break;

            case PhoneRespondingAgain:
                _phoneSilent = false;
                Changed();
                ReloadLists();
                break;
        }
    }

    // Back Online Or The Phone Answers Again: A Load That Failed Meanwhile Left The Lists Empty Or Stale. A Reload
    // Asked For During A Running Load Waits For It, So Overlapping Triggers Cost One Extra Fetch At Most
    private void ReloadLists()
    {
        _ = Inbox.LoadAsync();
        foreach (ConversationListViewModel list in _folders.Values)
        {
            _ = list.LoadAsync();
        }
    }

    private void Changed() => PropertyChanged?.Invoke(this, AllChanged);
}
