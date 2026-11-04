using System.Collections.ObjectModel;
using System.ComponentModel;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// One folder's conversation list: pinned first, then newest first, kept current by live updates.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ChatStore"/> raises its events on the UI thread, so rows change there. The list subscribes to the
/// store (which outlives the window), so call <see cref="Dispose"/> when the window closes.
/// </para>
/// <para>
/// With <see cref="PinnedApart"/> on, pinned conversations go to <see cref="Pinned"/> (the inbox's grid) instead of
/// <see cref="Rows"/>; a row moves between the two as it is pinned or unpinned on the phone.
/// </para>
/// <para>
/// Folder membership follows the status: the inbox shows active conversations, spam and blocked shows the spam and
/// blocked folders, and archived shows archived and kept-archived ones. A live update that moves a conversation out
/// of the folder removes its row.
/// </para>
/// </remarks>
public sealed class ConversationListViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ChatStore _store;
    private readonly ConversationFolder _folder;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;

    // Every Conversation The Pages Listed Since The Newest Page: A Page With None New Is The End. Not The Rows, Which
    // Live Updates Also Fill (The Phone Pushes The Next Page's Conversations Too)
    private readonly HashSet<string> _paged = [];

    // Where The Last Page Ended; Null Means No More Pages
    private Cursor? _next;
    private bool _isLoading;
    private bool _isLoadingMore;

    // The First Load Ended (Shown Or Failed); Live Changes Animate Only After It
    private bool _hasLoaded;

    // A Reload, Or The Next Page, Asked For While A Load Was Running; Runs When That Load Ends
    private bool _reloadPending;
    private bool _morePending;

    // Pinned Conversations Go To Pinned Instead Of Rows
    private bool _pinnedApart;

    /// <summary>
    /// Creates an empty list and starts listening to the store.
    /// </summary>
    /// <param name="store">Loads conversations and raises live updates.</param>
    /// <param name="folder">Which folder this list shows.</param>
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

        // A Row Landing Mid-Load (A Live Update) Ends The Empty List, So The Ring Goes At Once
        Rows.CollectionChanged   += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadingFirstPage)));
        Pinned.CollectionChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadingFirstPage)));
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised just before the rows change: true for a live change (a new message lifts a row, a conversation arrives or
    /// leaves), false for a load or a page.
    /// </summary>
    /// <remarks>
    /// Raised ahead of the change so the view can turn the list's move and add animations on or off before the list
    /// sees it: only live changes animate, never the first load, a reload or a page.
    /// </remarks>
    public event EventHandler<bool>? RowsChanging;

    /// <summary>Gets the rows, pinned first (unless <see cref="PinnedApart"/>), then newest first.</summary>
    public ObservableCollection<ConversationRowViewModel> Rows { get; } = [];

    /// <summary>Gets the pinned rows, newest first, while <see cref="PinnedApart"/> is on; empty otherwise.</summary>
    public ObservableCollection<ConversationRowViewModel> Pinned { get; } = [];

    /// <summary>
    /// Gets or sets whether pinned conversations show in <see cref="Pinned"/> instead of <see cref="Rows"/>.
    /// </summary>
    /// <remarks>Turning it on or off moves the rows at once, without motion. Call on the UI thread.</remarks>
    public bool PinnedApart
    {
        get => _pinnedApart;
        set
        {
            if (_pinnedApart == value)
            {
                return;
            }

            // A Copy: Placing Moves Rows Between The Two
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
    /// Gets whether a page is loading into an empty list (the first load, or a reload after one that failed): the
    /// loading ring. A list with rows keeps them on screen while it reloads or loads more.
    /// </summary>
    public bool IsLoadingFirstPage => _isLoading && Rows.Count == 0 && Pinned.Count == 0;

    /// <summary>Gets whether the next page is loading below the rows: the small ring at the list's bottom.</summary>
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
    /// Loads the newest page, replacing the rows.
    /// </summary>
    /// <remarks>
    /// Call on the UI thread. An empty list first shows the conversations the store already has (the window reopened),
    /// so it never waits on the phone. The page then updates those rows in place and drops the ones it doesn't list.
    /// While a page is loading the call is remembered and runs once that load ends. When the load fails (phone not
    /// connected or not answering, a network error) the rows stay as they were.
    /// </remarks>
    /// <returns>A task that completes when the page is shown.</returns>
    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            _reloadPending = true;
            return;
        }

        // What The Store Already Has, Before The Phone Answers
        if (Rows.Count == 0 && Pinned.Count == 0)
        {
            foreach (ConversationInfo c in _store.CachedConversations())
            {
                Apply(c, live: false);
            }
        }

        IsLoading = true;
        try
        {
            (IReadOnlyList<ConversationInfo> page, Cursor? next) = await _store.LoadFolderAsync(_folder, null, CancellationToken.None);

            // Rows Stay The Same Objects, So The Selection And Scroll Survive The Refresh
            HashSet<string> listed = [.. page.Select(c => c.Id)];
            DropUnlisted(Pinned, listed);
            DropUnlisted(Rows, listed);

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
            // Not Connected, Not Answering Or A Network Error: Reconnecting Or The Phone Answering Again Reloads
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
    /// Call on the UI thread. Does nothing when there are no more pages. Asked while a page is loading, it runs once that
    /// load ends: the list asks when its last row shows, and a row that showed mid-load won't show again until it is
    /// scrolled away and back (the paging that stalled now and then). A page with no conversation the earlier pages
    /// didn't list (empty, or the phone ignored the cursor) is the end, so scrolling never asks for the same page forever.
    /// A page whose rows live updates had already shown loads the next one at once, as the last row didn't change.
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

        // The Rows Before The Page: Loading It Raises Live Updates That Add Its Rows Before It Returns
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

            // Every Row Of The Page Was On Screen Already (Live Updates Brought Them First): The Last Row Didn't Change,
            // So It Won't Show Again To Ask For The Next Page. Ask Now
            if (_next is not null && page.All(c => shown.Contains(c.Id)))
            {
                _morePending = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not Connected, Not Answering Or A Network Error: Scrolling Again Tries Again
            _store.LogFailure("load more conversations", ex);
        }
        finally
        {
            IsLoading     = false;
            IsLoadingMore = false;
        }

        await RunPendingReloadAsync();
    }

    // A Resync Or Reload Arrived Mid-Load: The Page May Be Stale, So Load Again. Then The Next Page, If It Was Asked For
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

    // A Live Update Before The First Load Ends, Or While A Load Or Page Runs, Is Part Of That Load (Loading A Page
    // Raises Them): It Doesn't Animate
    private void OnConversationChanged(object? sender, ConversationInfo c) => Apply(c, live: _hasLoaded && !IsLoading);

    private void OnResynced(object? sender, EventArgs e) => _post(() => _ = LoadAsync());

    // Inserts, Moves Or Updates The Row; Removes It When The Conversation Left This Folder. A Live Change Says So First
    // (RowsChanging), So The List Animates It
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
            var row                                           = new ConversationRowViewModel(c, now, _store.LiveUnreadCount(c.Id), draft);
            ObservableCollection<ConversationRowViewModel> to = ListFor(row);

            RowsChanging?.Invoke(this, live);
            to.Insert(TargetIndex(to, row, null), row);
            return;
        }

        existing.Update(c, now, _store.LiveUnreadCount(c.Id), draft);
        Place(existing, live);
    }

    // A Draft Kept Or Cleared: The Row Shows It (Or Its Newest Message Again), In Place
    private void OnDraftChanged(object? sender, string conversationId)
    {
        if (_store.Find(conversationId) is not { } c || Find(conversationId) is null)
        {
            return;
        }

        Apply(c, live: false);
    }

    // Moves A Row To Its Place: Within Its Collection, Or Across When It Was Pinned Or Unpinned (Or PinnedApart Changed)
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

    // Removes The Rows A Fresh Page Didn't List
    private void DropUnlisted(ObservableCollection<ConversationRowViewModel> list, HashSet<string> listed)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!listed.Contains(list[i].Id))
            {
                RowsChanging?.Invoke(this, false);
                list.RemoveAt(i);
            }
        }
    }

    // The Collection A Row Belongs In, And The One It Is In Now
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

    // Where A Row Goes Among The Others: After Every Row That Sorts Before It
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
