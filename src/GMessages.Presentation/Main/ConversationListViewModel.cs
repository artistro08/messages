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
/// Folder membership follows the status: the inbox shows active conversations, spam and blocked shows the spam and
/// blocked folders. A live update that moves a conversation out of the folder removes its row.
/// </para>
/// </remarks>
public sealed class ConversationListViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ChatStore _store;
    private readonly ConversationFolder _folder;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;

    // Where The Last Page Ended; Null Means No More Pages
    private Cursor? _next;
    private bool _isLoading;

    // A Reload Asked For While A Load Was Running; Runs When That Load Ends
    private bool _reloadPending;

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

        // A Row Landing Mid-Load (A Live Update) Ends The Empty List, So The Ring Goes At Once
        Rows.CollectionChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadingFirstPage)));
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the rows, pinned first, then newest first.</summary>
    public ObservableCollection<ConversationRowViewModel> Rows { get; } = [];

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
    public bool IsLoadingFirstPage => _isLoading && Rows.Count == 0;

    /// <summary>
    /// Loads the newest page, replacing the rows.
    /// </summary>
    /// <remarks>Call on the UI thread. While a page is loading the call is remembered and runs once that load ends. When the load fails (phone not connected or not answering, a network error) the rows stay as they were.</remarks>
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
            (IReadOnlyList<ConversationInfo> page, Cursor? next) = await _store.LoadFolderAsync(_folder, null, CancellationToken.None);

            Rows.Clear();
            _next = next;
            foreach (ConversationInfo c in page)
            {
                Apply(c);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not Connected, Not Answering Or A Network Error: Reconnecting Or The Phone Answering Again Reloads
            _store.LogFailure("load conversations", ex);
        }
        finally
        {
            IsLoading = false;
        }

        await RunPendingReloadAsync();
    }

    /// <summary>
    /// Loads the next page below the rows.
    /// </summary>
    /// <remarks>Call on the UI thread. Does nothing while a page is loading or when there are no more pages.</remarks>
    /// <returns>A task that completes when the page is shown.</returns>
    public async Task LoadMoreAsync()
    {
        if (IsLoading || _next is null)
        {
            return;
        }

        IsLoading = true;
        try
        {
            (IReadOnlyList<ConversationInfo> page, Cursor? next) = await _store.LoadFolderAsync(_folder, _next, CancellationToken.None);

            _next = next;
            foreach (ConversationInfo c in page)
            {
                Apply(c);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not Connected, Not Answering Or A Network Error: Scrolling Again Tries Again
            _store.LogFailure("load more conversations", ex);
        }
        finally
        {
            IsLoading = false;
        }

        await RunPendingReloadAsync();
    }

    // A Resync Or Reload Arrived Mid-Load: The Page May Be Stale, So Load Again
    private async Task RunPendingReloadAsync()
    {
        if (_reloadPending)
        {
            _reloadPending = false;
            await LoadAsync();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _store.ConversationChanged -= OnConversationChanged;
        _store.Resynced            -= OnResynced;
    }

    // =========================================================================
    // LIVE UPDATES
    // =========================================================================

    private void OnConversationChanged(object? sender, ConversationInfo c) => Apply(c);

    private void OnResynced(object? sender, EventArgs e) => _post(() => _ = LoadAsync());

    // Inserts, Moves Or Updates The Row; Removes It When The Conversation Left This Folder
    private void Apply(ConversationInfo c)
    {
        int index = IndexOf(c.Id);

        if (!BelongsHere(c))
        {
            if (index >= 0)
            {
                Rows.RemoveAt(index);
            }

            return;
        }

        DateTimeOffset now = _time.GetLocalNow();

        if (index < 0)
        {
            var row = new ConversationRowViewModel(c, now);
            Rows.Insert(TargetIndex(row, null), row);
            return;
        }

        ConversationRowViewModel existing = Rows[index];
        existing.Update(c, now);

        int target = TargetIndex(existing, existing);
        if (target != index)
        {
            Rows.Move(index, target);
        }
    }

    private bool BelongsHere(ConversationInfo c) => _folder == ConversationFolder.Inbox
        ? c.IsMainView
        : c.Status is ConversationStatus.SpamFolder or ConversationStatus.BlockedFolder;

    private int IndexOf(string id)
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    // Where A Row Goes Among The Others: After Every Row That Sorts Before It
    private int TargetIndex(ConversationRowViewModel row, ConversationRowViewModel? skip)
    {
        int count = 0;
        foreach (ConversationRowViewModel other in Rows)
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
