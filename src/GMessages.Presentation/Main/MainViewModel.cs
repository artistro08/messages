using System.ComponentModel;
using GMessages.Presentation.Connection;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Events;

namespace GMessages.Presentation.Main;

/// <summary>
/// Holds the state of the main window: the folder lists, the open conversation, the actions and the banners.
/// </summary>
/// <remarks>
/// <para>
/// The window creates the view model when it opens. The connection and the store live longer than the window, so call
/// <see cref="Dispose"/> when the window closes. The view sets <see cref="IsNarrow"/> and
/// <see cref="IsWindowFocused"/>.
/// </para>
/// <para>
/// Each change raises <see cref="PropertyChanged"/> 1 time with an empty property name. That name tells <c>x:Bind</c>
/// to read every property again. Call every member on the UI thread.
/// </para>
/// </remarks>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    private readonly ConnectionController _connection;
    private readonly ChatStore _store;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly LinkPreviewer? _previews;
    private readonly Func<bool>? _showReceivedPreviews;

    // The conversations that the user archived in this session, with the latest last. Undo puts them back with the
    // button of the bar, or with Ctrl + Z at any time. Each other action on a conversation removes it from the list.
    private readonly List<string> _archived = [];
    private ITimer? _undoTimer;

    private readonly Dictionary<ConversationFolder, ConversationListViewModel> _folders = [];

    // A double click sends 1 request: a conversation with an action that waits on the phone accepts no other action.
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    private ConnectionState _state;
    private bool _phoneSilent;
    private bool _batteryLow;
    private bool _batteryDismissed;

    // A late callback must not open anything after Dispose. An example is a Start chat that ends after the window
    // closes.
    private bool _disposed;

    /// <summary>
    /// Creates the view model with an inbox list that is not loaded yet, and starts to watch the connection.
    /// </summary>
    /// <param name="connection">Gives the connection state and the live events of the phone.</param>
    /// <param name="store">Loads the conversations.</param>
    /// <param name="time">Gives the current time for the list times.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <param name="previews">Fetches the link cards for the open chat, or null for none. The caller owns it.</param>
    /// <param name="showReceivedPreviews">
    /// Tells whether a link in a received message gets a preview, or null for always.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// An argument other than <paramref name="previews"/> or <paramref name="showReceivedPreviews"/> is null.
    /// </exception>
    public MainViewModel(ConnectionController connection, ChatStore store, TimeProvider time, Action<Action> post, LinkPreviewer? previews = null, Func<bool>? showReceivedPreviews = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(post);

        _connection           = connection;
        _store                = store;
        _time                 = time;
        _post                 = post;
        _previews             = previews;
        _showReceivedPreviews = showReceivedPreviews;
        _state                = connection.State;
        Inbox                 = new ConversationListViewModel(store, ConversationFolder.Inbox, time, post);

        _connection.StateChanged += OnStateChanged;
        _connection.ClientEvent  += OnClientEvent;

        _batteryLow = store.IsPhoneBatteryLow;
        _store.PhoneBatteryChanged    += OnPhoneBatteryChanged;
        _store.PhoneBatteryLowAlerted += OnPhoneBatteryLowAlerted;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Occurs when the connection reaches <see cref="ConnectionState.Connected"/>. The window can then retry the photos
    /// that failed while the connection was down.
    /// </summary>
    /// <remarks>
    /// The lists reload by themselves at that time. They also reload when the phone answers after a silence.
    /// </remarks>
    public event EventHandler? Connected;

    /// <summary>Gets the inbox list.</summary>
    public ConversationListViewModel Inbox { get; }

    /// <summary>Gets the spam and blocked list, or null until the user opens it for the first time.</summary>
    public ConversationListViewModel? SpamBlocked => _folders.GetValueOrDefault(ConversationFolder.SpamBlocked);

    /// <summary>Gets the folder whose list shows now.</summary>
    public ConversationFolder Folder { get; private set; }

    /// <summary>Gets whether the archived list shows instead of the inbox.</summary>
    public bool IsArchivedView => Folder == ConversationFolder.Archived;

    /// <summary>Gets whether a folder list shows instead of the inbox. The Back button then shows.</summary>
    public bool IsFolderView => Folder != ConversationFolder.Inbox;

    /// <summary>Gets the list that shows now.</summary>
    public ConversationListViewModel CurrentList => _folders.GetValueOrDefault(Folder) ?? Inbox;

    /// <summary>Gets the open conversation's ID, or null when none is open.</summary>
    public string? SelectedConversationId { get; private set; }

    /// <summary>
    /// Gets whether a conversation is open. A binding uses it because a binding skips its function when an argument is
    /// null.
    /// </summary>
    public bool IsChatOpen => SelectedConversationId is not null;

    /// <summary>Gets whether the list is collapsed to avatars.</summary>
    public bool IsListCollapsed { get; private set; }

    /// <summary>Gets or sets whether the window is narrow. The view sets it.</summary>
    public bool IsNarrow
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    /// <summary>Gets or sets whether the window has focus. The view sets it.</summary>
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

    /// <summary>
    /// Gets the participant ID of the phone owner for the profile picture. The value is null until a conversation names
    /// the owner.
    /// </summary>
    public string? MeId => _store.Me?.Id;

    /// <summary>Gets whether Start chat shows in place of a conversation.</summary>
    public bool IsStartChatOpen { get; private set; }

    /// <summary>Gets the state of the Start chat pane while it is open, or null.</summary>
    public StartChatViewModel? StartChat { get; private set; }

    /// <summary>
    /// Gets whether the chat pane shows instead of the list. This occurs only in a narrow window with a conversation or
    /// Start chat open.
    /// </summary>
    public bool ShowChatPane => IsNarrow && (SelectedConversationId is not null || IsStartChatOpen);

    /// <summary>
    /// Gets whether the connection banner shows. A binding uses it because a binding skips its function when an
    /// argument is null.
    /// </summary>
    public bool HasStatusBanner => StatusBanner is not null;

    /// <summary>Gets the connection banner text, or null when the app is connected and the phone answers.</summary>
    public string? StatusBanner => _state switch
    {
        ConnectionState.Connecting or ConnectionState.Reconnecting => ChatCopy.Connecting,
        ConnectionState.Connected when _phoneSilent                => ChatCopy.WaitingForPhone,
        _                                                          => null,
    };

    /// <summary>Gets the failure banner text after a conversation action fails, or null.</summary>
    public string? ActionError { get; private set; }

    /// <summary>
    /// Gets whether the failure banner shows. A binding uses it because a binding skips its function when an argument
    /// is null.
    /// </summary>
    public bool HasActionError => ActionError is not null;

    /// <summary>
    /// Gets whether the low battery banner shows. The banner shows after a live low battery alert, until the phone
    /// reports a recovery or the user closes it.
    /// </summary>
    /// <remarks>The level of the phone is not known at start, so the banner waits for a live alert.</remarks>
    public bool IsPhoneBatteryLow => _batteryLow && !_batteryDismissed;

    /// <summary>
    /// Hides the battery banner until the next low battery alert. The close button of the banner calls it.
    /// </summary>
    public void DismissPhoneBattery()
    {
        _batteryDismissed = true;
        Changed();
    }

    private void OnPhoneBatteryChanged(object? sender, EventArgs e) => _post(() =>
    {
        if (_disposed)
        {
            return;
        }

        _batteryLow = _store.IsPhoneBatteryLow;
        Changed();
    });

    // Each low alert shows a closed banner again, also a repeat alert.
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

    /// <summary>Shows the list of a folder, and creates and loads the list the first time.</summary>
    /// <remarks>
    /// A narrow window shows 1 pane. An open chat or Start chat closes first, as Escape does, and then the list shows.
    /// A wide window keeps the open chat beside the list.
    /// </remarks>
    /// <param name="folder">The folder; <see cref="ConversationFolder.Inbox"/> shows the inbox.</param>
    public void OpenFolder(ConversationFolder folder)
    {
        if (_disposed)
        {
            return;
        }

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

    /// <summary>Shows the spam and blocked list, and creates and loads the list the first time.</summary>
    public void OpenSpamBlocked() => OpenFolder(ConversationFolder.SpamBlocked);

    /// <summary>Shows the archived list, and creates and loads the list the first time.</summary>
    public void OpenArchived() => OpenFolder(ConversationFolder.Archived);

    /// <summary>Shows the inbox again.</summary>
    public void BackToInbox() => OpenFolder(ConversationFolder.Inbox);

    /// <summary>Opens a conversation and closes Start chat.</summary>
    /// <remarks>
    /// The call does nothing after <see cref="Dispose"/>. Thus a Start chat that ends after the window closes opens no
    /// chat.
    /// </remarks>
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

        // A pick of the open conversation keeps its chat and the draft.
        if (Chat?.ConversationId != conversationId)
        {
            ReplaceChat(conversationId);
        }

        UpdateChatActive();
        Changed();
    }

    /// <summary>
    /// Closes the open conversation or Start chat, as Escape does. A narrow window then shows the list.
    /// </summary>
    /// <remarks>The store keeps the draft for the next open.</remarks>
    public void ExitConversation()
    {
        SelectedConversationId = null;
        IsStartChatOpen        = false;
        StartChat              = null;
        ReplaceChat(null);
        Changed();
    }

    /// <summary>
    /// Opens the conversation below the open one in the current list. The last conversation stays open at the end.
    /// </summary>
    /// <remarks>With no conversation open, the call opens the first conversation.</remarks>
    public void NextConversation() => MoveSelection(1);

    /// <summary>
    /// Opens the conversation above the open one in the current list. The first conversation stays open at the top.
    /// </summary>
    /// <remarks>With no conversation open, the call opens the first conversation.</remarks>
    public void PreviousConversation() => MoveSelection(-1);

    /// <summary>Shows Start chat in place of the open conversation, and loads the contacts the first time.</summary>
    public void OpenStartChat()
    {
        if (_disposed)
        {
            return;
        }

        SelectedConversationId = null;
        IsStartChatOpen        = true;
        ReplaceChat(null);

        // An open pane keeps what the user typed and picked.
        if (StartChat is null)
        {
            StartChat = new StartChatViewModel(_store);
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

        if (Chat is { } open)
        {
            KeepDraft(open);
            open.Dispose();
        }
    }

    // Saves the draft and files of a closing chat only when the user changed them in the chat. An empty draft forgets
    // the saved draft. A saved draft can load after the chat opens. The chat never showed it, so the empty box must not
    // clear it.
    private void KeepDraft(ChatViewModel chat)
    {
        if (!chat.IsDraftEdited)
        {
            return;
        }

        _store.SaveDraft(chat.ConversationId, chat.Draft, [.. chat.Attachments]);
    }

    private void ReplaceChat(string? conversationId)
    {
        if (Chat is { } closing)
        {
            KeepDraft(closing);
            closing.Dispose();
        }

        Chat = null;

        if (conversationId is not null)
        {
            Chat = new ChatViewModel(_store, conversationId, _time, _post, _previews, _showReceivedPreviews);
            Chat.RestoreDraft(_store.Draft(conversationId), _store.DraftFiles(conversationId));

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

    // The pinned grid comes first, as on the screen. Pinned is empty when the grid does not show.
    private void MoveSelection(int step)
    {
        List<ConversationRowViewModel> rows = [.. CurrentList.Pinned, .. CurrentList.Rows];
        if (rows.Count == 0)
        {
            return;
        }

        int current = rows.FindIndex(r => r.Id == SelectedConversationId);
        int next    = current < 0 ? 0 : Math.Clamp(current + step, 0, rows.Count - 1);
        Select(rows[next].Id);
    }

    // =========================================================================
    // CONVERSATION ACTIONS
    // =========================================================================

    /// <summary>Finds a conversation that the store knows, for its menu and the Details dialog.</summary>
    /// <param name="id">The conversation ID.</param>
    /// <returns>The conversation, or null when the store does not know it.</returns>
    public ConversationInfo? FindConversation(string id) => _store.Find(id);

    /// <summary>
    /// Archives, unarchives, mutes, unmutes, blocks, unblocks or deletes a conversation on the phone.
    /// </summary>
    /// <remarks>
    /// Call it on the UI thread. While a run for a conversation waits on the phone, another run for it returns false at
    /// once and sends nothing. The method logs each failure and shows <see cref="ActionError"/>. A failure can be no
    /// connection, a refusal, no answer or a network error. The view gets no exception. A success clears the banner. A
    /// success also closes the open chat when the action moves the chat out of its folder. Each action except mute and
    /// unmute moves it.
    /// </remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="action">What to do.</param>
    /// <returns>True when the phone made the change.</returns>
    public Task<bool> RunAsync(string conversationId, ConversationAction action) => RunAsync(conversationId, action, offerUndo: true);

    private async Task<bool> RunAsync(string conversationId, ConversationAction action, bool offerUndo)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

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
            // The click handler must get no exception.
            _store.LogFailure("update conversation", ex);
            ShowActionError(ChatCopy.ActionFailed);
            return false;
        }
        finally
        {
            _running.Remove(conversationId);
        }

        if (_disposed)
        {
            return true;
        }

        ActionError = null;

        // Each action removes the conversation from the undo list. An archive adds it again as the latest.
        _archived.Remove(conversationId);
        if (offerUndo)
        {
            ShowUndo(conversationId, action);
        }

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

    /// <summary>The Undo bar shows for this time after an archive.</summary>
    public static readonly TimeSpan UndoTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the text of the Undo bar, or null while the bar is hidden. Only an archive offers Undo, because the owner
    /// wants undo in 1 direction only.
    /// </summary>
    public string? UndoNotice { get; private set; }

    /// <summary>Gets whether the Undo bar shows.</summary>
    public bool HasUndo => UndoNotice is not null;

    /// <summary>Gets whether Ctrl + Z can put back a conversation that the user archived in this session.</summary>
    public bool CanUndo => _archived.Count > 0;

    /// <summary>
    /// Puts back the latest archive of this session with an unarchive through <see cref="RunAsync"/>. The row goes back
    /// to its place in the inbox.
    /// </summary>
    /// <remarks>
    /// The bar hides at once. When the undo fails, the conversation goes back to its place in the undo list. The next
    /// undo then tries the same conversation, unless the user archived another conversation during the undo.
    /// </remarks>
    /// <returns>True when the phone made the change.</returns>
    public async Task<bool> UndoAsync()
    {
        if (_archived.Count == 0)
        {
            return false;
        }

        int at    = _archived.Count - 1;
        string id = _archived[at];
        _archived.RemoveAt(at);
        HideUndo();

        // An archive made during the undo stays the latest, because the Undo bar shows that archive.
        bool undone = await RunAsync(id, ConversationAction.Unarchive, offerUndo: false);
        if (!undone && !_disposed)
        {
            _archived.Insert(Math.Min(at, _archived.Count), id);
        }

        return undone;
    }

    /// <summary>Hides the Undo bar.</summary>
    public void DismissUndo() => HideUndo();

    private void ShowUndo(string conversationId, ConversationAction action)
    {
        _undoTimer?.Dispose();
        _undoTimer = null;
        if (action != ConversationAction.Archive)
        {
            HideUndo();
            return;
        }

        _archived.Add(conversationId);
        UndoNotice = ChatCopy.ConversationArchived;
        _undoTimer = _time.CreateTimer(_ => _post(() =>
        {
            if (_archived.Count > 0 && _archived[^1] == conversationId)
            {
                HideUndo();
            }
        }), null, UndoTime, Timeout.InfiniteTimeSpan);
    }

    // The archives stay in the list, so Ctrl + Z can still put them back.
    private void HideUndo()
    {
        _undoTimer?.Dispose();
        _undoTimer = null;
        if (UndoNotice is null || _disposed)
        {
            return;
        }

        UndoNotice = null;
        Changed();
    }

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

    // The controller raises StateChanged through its own post. The view model reads the state again on the UI thread.
    private void OnStateChanged(object? sender, EventArgs e) => _post(() => ApplyState(_connection.State));

    // The controller already posts the client events to the UI thread.
    private void OnClientEvent(object? sender, object e) => ApplyEvent(e);

    internal void ApplyState(ConnectionState state)
    {
        if (_disposed)
        {
            return;
        }

        // A new state clears the silence. The phone is silent only after it says so again.
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

    // A load that failed while the connection was down left the lists empty or stale. A reload during a running load
    // waits for that load. Thus triggers that overlap cost 1 extra fetch at most.
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
