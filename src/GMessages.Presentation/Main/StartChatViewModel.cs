using System.Collections.ObjectModel;
using System.ComponentModel;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// The Start chat pane: pick people or type a number, then open the conversation with them.
/// </summary>
/// <remarks>
/// Call everything on the UI thread. <see cref="Results"/> shows the phone's top contacts first, then the rest.
/// </remarks>
public sealed class StartChatViewModel : INotifyPropertyChanged
{
    private readonly ChatStore _store;

    private List<ContactRowViewModel> _top = [];
    private List<ContactRowViewModel> _all = [];
    private bool _loaded;

    // Lookups Running: The Contact Load And Go Can Overlap
    private int _busy;
    private string _query = string.Empty;
    private string? _error;

    /// <summary>
    /// Creates the pane with no contacts yet.
    /// </summary>
    /// <param name="store">Gives the phone client.</param>
    /// <param name="post">Runs an action on the UI thread (the pane raises everything on the caller's thread, so it is not needed yet).</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public StartChatViewModel(ChatStore store, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(post);

        _store = store;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets or sets what the user typed; changing it filters <see cref="Results"/>.</summary>
    public string Query
    {
        get => _query;
        set
        {
            _query = value ?? string.Empty;
            Refresh();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Query)));
        }
    }

    /// <summary>Gets the rows that match <see cref="Query"/>; a "Send to" row comes first for a number or email.</summary>
    public ObservableCollection<ContactRowViewModel> Results { get; } = [];

    /// <summary>Gets the picked people (the chips).</summary>
    public ObservableCollection<ContactRowViewModel> Selected { get; } = [];

    /// <summary>Gets whether anyone is picked.</summary>
    public bool CanStart => Selected.Count > 0;

    /// <summary>Gets why the last Go failed (<see cref="ChatCopy.StartFailed"/>), or null; the next Go clears it.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            _error = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Error)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
        }
    }

    /// <summary>Gets whether a lookup is running (the contacts loading, or Go finding the conversation): the loading ring.</summary>
    public bool IsLoading => _busy > 0;

    /// <summary>Gets whether <see cref="Error"/> shows (for bindings, which skip their function when an argument is null).</summary>
    public bool HasError => _error is not null;

    /// <summary>
    /// Loads the top contacts, then the full contact list (once).
    /// </summary>
    /// <remarks>When the load fails (phone not connected or not answering, a network error), what loaded so far stays and a later call tries again.</remarks>
    /// <returns>A task that completes when the contacts are shown.</returns>
    public async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }

        Busy(1);
        try
        {
            IMessagingClient client = _store.RequireClient();

            ListTopContactsResponse top = await client.ListTopContactsAsync(CancellationToken.None);
            _top = [.. top.Contacts.Select(c => new ContactRowViewModel(c))];
            Refresh();

            ListContactsResponse all = await client.ListContactsAsync(CancellationToken.None);
            _all    = [.. all.Contacts.Select(c => new ContactRowViewModel(c))];
            _loaded = true;
            Refresh();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not Connected, Not Answering Or A Network Error: Opening Start chat Again Tries Again
            _store.LogFailure("load contacts", ex);
        }
        finally
        {
            Busy(-1);
        }
    }

    /// <summary>
    /// Picks a person, or removes them from the chips when already picked.
    /// </summary>
    /// <remarks>Picking the "Send to" row clears the query.</remarks>
    /// <param name="c">The row.</param>
    public void Toggle(ContactRowViewModel c)
    {
        ArgumentNullException.ThrowIfNull(c);

        ContactRowViewModel? picked = Selected.FirstOrDefault(s => s.Number == c.Number);
        if (picked is not null)
        {
            Selected.Remove(picked);
        }
        else
        {
            Selected.Add(c);
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStart)));

        // A Picked Person Leaves The List (Their Chip Shows Them); Removed, They Come Back
        if (c.IsSendTo)
        {
            Query = string.Empty;
        }
        else
        {
            Refresh();
        }
    }

    /// <summary>
    /// Opens (or creates) the conversation with everyone picked.
    /// </summary>
    /// <remarks>
    /// Ported from the bridge's create flow: each number is sent as <c>ContactNumber { MysteriousInt = 2, Number = n,
    /// Number2 = n }</c>. When the phone answers <c>CreateRcs</c> the request is sent again as an RCS group with an
    /// empty name. When it fails for any reason (not connected, not answering, connection closed, a network, timeout,
    /// server or decode error, or no success with a conversation, also after the RCS retry), <see cref="Error"/> says
    /// so and everyone stays picked; nothing is thrown.
    /// </remarks>
    /// <returns>The conversation ID, or null when it failed.</returns>
    /// <exception cref="InvalidOperationException">Nobody is picked.</exception>
    public async Task<string?> StartAsync()
    {
        if (!CanStart)
        {
            throw new InvalidOperationException("Nobody is picked.");
        }

        Error = null;
        Busy(1);
        try
        {
            return await GetOrCreateAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any Failure (Phone, Network, Timeout, Server, Decode) Is Shown, Never Thrown At The Click Handler
            _store.LogFailure("start chat", ex);
            Error = ChatCopy.StartFailed;
            return null;
        }
        finally
        {
            Busy(-1);
        }
    }

    // Counts A Lookup In (1) Or Out (-1)
    private void Busy(int change)
    {
        _busy += change;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoading)));
    }

    // The Create Request, Retried As An RCS Group When The Phone Asks; Throws When The Phone Can't
    private async Task<string> GetOrCreateAsync()
    {
        IMessagingClient client = _store.RequireClient();

        var request = new GetOrCreateConversationRequest();
        foreach (ContactRowViewModel c in Selected)
        {
            request.Numbers.Add(new ContactNumber { MysteriousInt = 2, Number = c.Number, Number2 = c.Number });
        }

        GetOrCreateConversationResponse response = await client.GetOrCreateConversationAsync(request, CancellationToken.None);

        if (response.Status == GetOrCreateConversationResponse.Types.Status.CreateRcs)
        {
            request.RCSGroupName   = string.Empty;
            request.CreateRCSGroup = true;
            response               = await client.GetOrCreateConversationAsync(request, CancellationToken.None);
        }

        // The Phone Must Have Answered Success With A Real Conversation
        if (response.Status != GetOrCreateConversationResponse.Types.Status.Success
            || string.IsNullOrEmpty(response.Conversation?.ConversationID))
        {
            throw new MessageSendException(response.Status.ToString());
        }

        return response.Conversation.ConversationID;
    }

    // =========================================================================
    // FILTERING
    // =========================================================================

    // Rebuilds Results: Send-To Row, Then Top Contacts, Then The Rest, Filtered By The Query, Without Anyone Picked (Their
    // Chips Show Them). The Same Rows Leave The List Alone, So A Row Being Clicked (The Full List Arriving After The Top
    // Contacts) Isn't Swapped Out Under The Click
    private void Refresh()
    {
        string query   = _query.Trim();
        string digits  = ContactRowViewModel.DigitsOf(query);
        var top_ids    = _top.Select(c => c.Number).ToHashSet();
        var picked_ids = Selected.Select(c => c.Number).ToHashSet();

        IEnumerable<ContactRowViewModel> matches = _top
            .Concat(_all.Where(c => !top_ids.Contains(c.Number)))
            .Where(c => !picked_ids.Contains(c.Number))
            .Where(c => query.Length == 0
                || c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (digits.Length > 0 && c.Digits.Contains(digits, StringComparison.Ordinal)));

        List<ContactRowViewModel> rows = [];
        if (query.Any(char.IsAsciiDigit) || query.Contains('@', StringComparison.Ordinal))
        {
            rows.Add(Results.FirstOrDefault(r => r.IsSendTo && r.Number == query) ?? new ContactRowViewModel(query));
        }

        rows.AddRange(matches);
        if (rows.SequenceEqual(Results))
        {
            return;
        }

        // Changed In Place, Not Cleared And Refilled: The List Then Animates Only What Changed (A Picked Row Fades Out And
        // The Rows Below Slide Up) And Keeps Its Scroll, Where A Refill Made It Jump (Owner, Plan 9)
        var kept = new HashSet<ContactRowViewModel>(rows, ReferenceEqualityComparer.Instance);
        for (int i = Results.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(Results[i]))
            {
                Results.RemoveAt(i);
            }
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (i < Results.Count && ReferenceEquals(Results[i], rows[i]))
            {
                continue;
            }

            int at = Results.IndexOf(rows[i]);
            if (at >= 0)
            {
                Results.Move(at, i);
            }
            else
            {
                Results.Insert(i, rows[i]);
            }
        }
    }
}
