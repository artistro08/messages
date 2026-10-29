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
    private string _query = string.Empty;

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

    /// <summary>
    /// Loads the top contacts, then the full contact list (once).
    /// </summary>
    /// <remarks>When the phone is not connected the lists stay empty and a later call tries again.</remarks>
    /// <returns>A task that completes when the contacts are shown.</returns>
    public async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }

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
        catch (MessagingUnavailableException)
        {
            // Not Connected: Opening Start chat Again Tries Again
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

        if (c.IsSendTo)
        {
            Query = string.Empty;
        }
    }

    /// <summary>
    /// Opens (or creates) the conversation with everyone picked.
    /// </summary>
    /// <remarks>
    /// Ported from the bridge's create flow: each number is sent as <c>ContactNumber { MysteriousInt = 2, Number = n,
    /// Number2 = n }</c>. When the phone answers <c>CreateRcs</c> the request is sent again as an RCS group with an
    /// empty name.
    /// </remarks>
    /// <returns>The conversation ID.</returns>
    /// <exception cref="InvalidOperationException">Nobody is picked.</exception>
    /// <exception cref="MessagingUnavailableException">Not connected.</exception>
    public async Task<string> StartAsync()
    {
        if (!CanStart)
        {
            throw new InvalidOperationException("Nobody is picked.");
        }

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

        return response.Conversation.ConversationID;
    }

    // =========================================================================
    // FILTERING
    // =========================================================================

    // Rebuilds Results: Send-To Row, Then Top Contacts, Then The Rest, Filtered By The Query
    private void Refresh()
    {
        string query   = _query.Trim();
        string digits  = ContactRowViewModel.DigitsOf(query);
        var top_ids    = _top.Select(c => c.Number).ToHashSet();

        IEnumerable<ContactRowViewModel> matches = _top
            .Concat(_all.Where(c => !top_ids.Contains(c.Number)))
            .Where(c => query.Length == 0
                || c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (digits.Length > 0 && c.Digits.Contains(digits, StringComparison.Ordinal)));

        Results.Clear();
        if (query.Any(char.IsAsciiDigit) || query.Contains('@', StringComparison.Ordinal))
        {
            Results.Add(new ContactRowViewModel(query));
        }

        foreach (ContactRowViewModel c in matches)
        {
            Results.Add(c);
        }
    }
}
