using System.Collections.ObjectModel;
using System.ComponentModel;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// Shows the conversations of 1 folder, pinned first and then newest first. Live updates keep the list current.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ChatStore"/> raises its events on the UI thread, so the rows change on the UI thread. The store lives
/// longer than the window, and the list listens to its events. Call <see cref="Dispose"/> when the window closes.
/// </para>
/// <para>
/// With <see cref="PinnedApart"/> on, <see cref="Pinned"/> holds the pinned conversations for the grid of the inbox,
/// and <see cref="Rows"/> holds the others. A row moves between the 2 when the phone pins or unpins it.
/// </para>
/// <para>
/// The folder of a conversation follows its status. The inbox shows the active conversations, except an empty
/// one-on-one chat. Spam and blocked shows the spam and blocked folders. Archived shows the archived and kept-archived
/// conversations. A live update that moves a conversation out of the folder removes its row.
/// </para>
/// </remarks>
public sealed class ConversationListViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ChatStore _store;
    private readonly ConversationFolder _folder;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;

    // The conversations that the pages listed since the newest page. A page with no new conversation ends the paging.
    // The rows cannot serve here. Live updates also add rows, because the phone pushes the next page too.
    private readonly HashSet<string> _paged = [];

    // The conversations that changed live while a load ran. A fresh page keeps their rows, because the phone made the
    // page before the change.
    private readonly HashSet<string> _changedMidLoad = [];

    // Null means that no more pages are left.
    private Cursor? _next;
    private bool _isLoading;
    private bool _isLoadingMore;

    // Live changes animate only after the first load ends, with a page or with a failure.
    private bool _hasLoaded;

    // A reload or a next page that a caller asks for during a load runs when that load ends.
    private bool _reloadPending;
    private bool _morePending;

    private bool _pinnedApart;

    /// <summary>
    /// Creates an empty list and starts to listen to the store.
    /// </summary>
    /// <param name="store">Loads conversations and raises live updates.</param>
    /// <param name="folder">The folder that the list shows.</param>
    /// <param name="time">Gives the current time for the list times.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ConversationListViewModel(ChatStore store, ConversationFolder folder, TimeProvider time, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(post);

        _store  = store;
        _folder = folder;
        _time   = time;
        _post   = post;

        _store.ConversationChanged += OnConversationChanged;
        _store.Resynced            += OnResynced;
        _store.DraftChanged        += OnDraftChanged;

        // A live row during a load ends the empty list, so the ring hides at once.
        Rows.CollectionChanged   += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadingFirstPage)));
        Pinned.CollectionChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadingFirstPage)));
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Occurs just before the rows change. The argument is true for a live change and false for a load or a page.
    /// </summary>
    /// <remarks>
    /// A live change is a new message that lifts a row, or a conversation that arrives or leaves. The event comes
    /// before the change, so the view can enable or disable the move and add animations first. Only live changes
    /// animate, never the first load, a reload or a page.
    /// </remarks>
    public event EventHandler<bool>? RowsChanging;

    /// <summary>
    /// Gets the rows, pinned first and then newest first. With <see cref="PinnedApart"/> on, the pinned rows are in
    /// <see cref="Pinned"/> instead.
    /// </summary>
    public ObservableCollection<ConversationRowViewModel> Rows { get; } = [];

    /// <summary>
    /// Gets the pinned rows, newest first, while <see cref="PinnedApart"/> is on. The collection is empty otherwise.
    /// </summary>
    public ObservableCollection<ConversationRowViewModel> Pinned { get; } = [];

    /// <summary>
    /// Gets or sets whether pinned conversations show in <see cref="Pinned"/> instead of <see cref="Rows"/>.
    /// </summary>
    /// <remarks>A change moves the rows at once, with no animation. Call it on the UI thread.</remarks>
    public bool PinnedApart
    {
        get => _pinnedApart;
        set
        {
            if (_pinnedApart == value)
            {
                return;
            }

            // Place moves rows between the 2 collections, so the loop runs on a copy.
            _pinnedApart                        = value;
            List<ConversationRowViewModel> rows = [.. Pinned, .. Rows];
            foreach (ConversationRowViewModel row in rows)
            {
                Place(row, live: false);
            }
        }
    }

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoading)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadingFirstPage)));
        }
    }

    /// <summary>
    /// Gets whether a page is loading into an empty list, which shows the loading ring. This occurs on the first load,
    /// or on a reload after a failed load.
    /// </summary>
    /// <remarks>A list with rows keeps them on the screen while it reloads or loads more.</remarks>
    public bool IsLoadingFirstPage => _isLoading && Rows.Count == 0 && Pinned.Count == 0;

    /// <summary>
    /// Gets whether the next page is loading below the rows. The small ring at the bottom of the list then shows.
    /// </summary>
    public bool IsLoadingMore
    {
        get => _isLoadingMore;
        private set
        {
            if (_isLoadingMore == value)
            {
                return;
            }

            _isLoadingMore = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadingMore)));
        }
    }

    /// <summary>
    /// Loads the newest page in place of the rows.
    /// </summary>
    /// <remarks>
    /// Call it on the UI thread. An empty list first shows the conversations that the store already has, so the list
    /// never waits on the phone. The store has them when the window opens again. The page then updates those rows in
    /// place and drops the rows that it does not list. A row that got a new message or first showed during the load
    /// stays. A call during a load runs when that load ends. When the load fails, the rows stay as they were. A load can fail with no
    /// connection, no answer or a network error.
    /// </remarks>
    /// <returns>A task that completes when the page is shown.</returns>
    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            _reloadPending = true;
            return;
        }

        if (Rows.Count == 0 && Pinned.Count == 0)
        {
            foreach (ConversationInfo c in _store.CachedConversations())
            {
                Apply(c, live: false);
            }
        }

        _changedMidLoad.Clear();
        IsLoading = true;
        try
        {
            (IReadOnlyList<ConversationInfo> page, Cursor? next) = await _store.LoadFolderAsync(_folder, null, CancellationToken.None);

            // The rows stay the same objects, so the selection and the scroll position survive the refresh.
            HashSet<string> listed = [.. page.Select(c => c.Id)];
            HashSet<string> kept   = [.. listed, .. _changedMidLoad];
            DropUnlisted(Pinned, kept);
            DropUnlisted(Rows, kept);

            _next = next;
            _paged.Clear();
            _paged.UnionWith(listed);
            foreach (ConversationInfo c in page)
            {
                Apply(c, live: false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A reconnect or the next answer of the phone reloads the list.
            _store.LogFailure("load conversations", ex);
        }
        finally
        {
            IsLoading  = false;
            _hasLoaded = true;
        }

        await RunPendingReloadAsync();
    }

    /// <summary>
    /// Loads the next page below the rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call it on the UI thread. The call does nothing when no more pages are left. A call during a load runs when that
    /// load ends. The view asks when the last row shows. A row that showed during a load does not show again until the
    /// user scrolls it out of view and then into view again. Without the wait, the paging sometimes stopped.
    /// </para>
    /// <para>
    /// A page with no conversation that the earlier pages did not list ends the paging. Such a page is empty, or the
    /// phone ignored the cursor. Thus the scroll never asks for the same page forever. Live updates can show all rows
    /// of a page before the page arrives. The last row then does not change, so the next page loads at once.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes when the page is shown.</returns>
    public async Task LoadMoreAsync()
    {
        if (IsLoading)
        {
            _morePending = true;
            return;
        }

        if (_next is null)
        {
            return;
        }

        // The set must hold the rows from before the page. The load raises live updates that add the rows of the page
        // before the load returns.
        HashSet<string> shown = [.. Pinned.Select(r => r.Id), .. Rows.Select(r => r.Id)];

        IsLoading     = true;
        IsLoadingMore = true;
        try
        {
            (IReadOnlyList<ConversationInfo> page, Cursor? next) = await _store.LoadFolderAsync(_folder, _next, CancellationToken.None);

            bool isNew = false;
            foreach (ConversationInfo c in page)
            {
                isNew |= _paged.Add(c.Id);
            }

            _next = isNew ? next : null;
            foreach (ConversationInfo c in page)
            {
                Apply(c, live: false);
            }

            // Live updates showed each row of the page before the page came. The last row did not change, so it does
            // not show again to ask for the next page. The list asks now.
            if (_next is not null && page.All(c => shown.Contains(c.Id)))
            {
                _morePending = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The next scroll to the end tries again.
            _store.LogFailure("load more conversations", ex);
        }
        finally
        {
            IsLoading     = false;
            IsLoadingMore = false;
        }

        await RunPendingReloadAsync();
    }

    // A resync or a reload during a load can make the page stale, so the list loads again. A next page that a caller
    // asked for runs after that.
    private async Task RunPendingReloadAsync()
    {
        if (_reloadPending)
        {
            _reloadPending = false;
            await LoadAsync();
            return;
        }

        if (_morePending)
        {
            _morePending = false;
            await LoadMoreAsync();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _store.ConversationChanged -= OnConversationChanged;
        _store.Resynced            -= OnResynced;
        _store.DraftChanged        -= OnDraftChanged;
    }

    // =========================================================================
    // LIVE UPDATES
    // =========================================================================

    // The load of a page raises live updates too. Thus an update does not animate before the first load ends or during
    // a load.
    private void OnConversationChanged(object? sender, ConversationInfo c)
    {
        // Only a new message keeps a row that the page does not list. A mark-read keeps no row of a deleted chat.
        if (IsLoading && (Find(c.Id) is not { } row || c.LastMessageTime > row.LastMessageTime))
        {
            _changedMidLoad.Add(c.Id);
        }

        Apply(c, live: _hasLoaded && !IsLoading);
    }

    private void OnResynced(object? sender, EventArgs e) => _post(() => _ = LoadAsync());

    // A live change raises RowsChanging first, so the view animates it.
    private void Apply(ConversationInfo c, bool live)
    {
        ConversationRowViewModel? existing = Find(c.Id);

        if (!BelongsHere(c))
        {
            if (existing is not null)
            {
                RowsChanging?.Invoke(this, live);
                ListOf(existing).Remove(existing);
            }

            return;
        }

        DateTimeOffset now = _time.GetLocalNow();
        string draft       = ChatFormat.DraftLine(_store.Draft(c.Id), _store.DraftFiles(c.Id));

        if (existing is null)
        {
            var row                                           = new ConversationRowViewModel(c, now, _store.LiveUnreadTexts(c.Id), draft);
            ObservableCollection<ConversationRowViewModel> to = ListFor(row);

            RowsChanging?.Invoke(this, live);
            to.Insert(TargetIndex(to, row, null), row);
            return;
        }

        existing.Update(c, now, _store.LiveUnreadTexts(c.Id), draft);
        Place(existing, live);
    }

    // The row shows the draft, or the newest message again, and does not move.
    private void OnDraftChanged(object? sender, string conversationId)
    {
        if (_store.Find(conversationId) is not { } c || Find(conversationId) is null)
        {
            return;
        }

        Apply(c, live: false);
    }

    // A row moves to the other collection when the phone pins or unpins it, or when PinnedApart changes.
    private void Place(ConversationRowViewModel row, bool live)
    {
        ObservableCollection<ConversationRowViewModel> from = ListOf(row);
        ObservableCollection<ConversationRowViewModel> to   = ListFor(row);
        int index                                           = from.IndexOf(row);

        if (from == to)
        {
            int target = TargetIndex(to, row, row);
            if (target != index)
            {
                RowsChanging?.Invoke(this, live);
                to.Move(index, target);
            }

            return;
        }

        RowsChanging?.Invoke(this, live);
        from.RemoveAt(index);
        to.Insert(TargetIndex(to, row, null), row);
    }

    private void DropUnlisted(ObservableCollection<ConversationRowViewModel> list, HashSet<string> kept)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(list[i].Id))
            {
                RowsChanging?.Invoke(this, false);
                list.RemoveAt(i);
            }
        }
    }

    private ObservableCollection<ConversationRowViewModel> ListFor(ConversationRowViewModel row) => _pinnedApart && row.IsPinned ? Pinned : Rows;

    private ObservableCollection<ConversationRowViewModel> ListOf(ConversationRowViewModel row) => Pinned.Contains(row) ? Pinned : Rows;

    private bool BelongsHere(ConversationInfo c) => _folder switch
    {
        ConversationFolder.Inbox       => c.IsMainView,
        ConversationFolder.Archived    => c.Status is ConversationStatus.Archived or ConversationStatus.KeepArchived,
        ConversationFolder.SpamBlocked => c.Status is ConversationStatus.SpamFolder or ConversationStatus.BlockedFolder,
        _                              => throw new InvalidOperationException($"Unknown folder {_folder}."),
    };

    private ConversationRowViewModel? Find(string id) =>
        Pinned.FirstOrDefault(r => r.Id == id) ?? Rows.FirstOrDefault(r => r.Id == id);

    // A row goes after each row that sorts before it. A row with the same time stays above it.
    private static int TargetIndex(ObservableCollection<ConversationRowViewModel> list, ConversationRowViewModel row, ConversationRowViewModel? skip)
    {
        int count = 0;
        foreach (ConversationRowViewModel other in list)
        {
            if (other == skip)
            {
                continue;
            }

            bool before = other.IsPinned != row.IsPinned
                ? other.IsPinned
                : other.LastMessageTime >= row.LastMessageTime;

            if (!before)
            {
                break;
            }

            count++;
        }

        return count;
    }
}
