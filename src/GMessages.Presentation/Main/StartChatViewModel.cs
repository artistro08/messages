using System.Collections.ObjectModel;
using System.ComponentModel;
using GMessages.Presentation.Messaging;
using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// Holds the state of the Start chat pane. The user picks people or types a number, and then opens the conversation
/// with them.
/// </summary>
/// <remarks>
/// Call every member on the UI thread. <see cref="Results"/> shows the top contacts of the phone first, and then the
/// rest.
/// </remarks>
public sealed class StartChatViewModel : INotifyPropertyChanged
{
    private readonly ChatStore _store;

    private List<ContactRowViewModel> _top = [];
    private List<ContactRowViewModel> _all = [];
    private bool _loaded;

    // The number of lookups that run now. The contact load and Go can overlap.
    private int _busy;
    private string _query = string.Empty;
    private string? _error;

    /// <summary>
    /// Creates the pane with no contacts yet.
    /// </summary>
    /// <param name="store">Gives the phone client.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public StartChatViewModel(ChatStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets or sets what the user typed. A change filters <see cref="Results"/>.</summary>
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

    /// <summary>
    /// Gets the rows that match <see cref="Query"/>. A "Send to" row comes first for a number or an email address.
    /// </summary>
    public ObservableCollection<ContactRowViewModel> Results { get; } = [];

    /// <summary>Gets the picked people, which show as chips.</summary>
    public ObservableCollection<ContactRowViewModel> Selected { get; } = [];

    /// <summary>Gets whether anyone is picked.</summary>
    public bool CanStart => Selected.Count > 0;

    /// <summary>
    /// Gets the text <see cref="ChatCopy.StartFailed"/> after the last Go failed, or null. The next Go clears it.
    /// </summary>
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

    /// <summary>
    /// Gets whether a lookup runs, which shows the loading ring. A lookup is the contact load or the search of Go for
    /// the conversation.
    /// </summary>
    public bool IsLoading => _busy > 0;

    /// <summary>
    /// Gets whether <see cref="Error"/> shows. A binding uses it because a binding skips its function when an argument
    /// is null.
    /// </summary>
    public bool HasError => _error is not null;

    /// <summary>
    /// Loads the top contacts and then the full contact list.
    /// </summary>
    /// <remarks>
    /// After a full load, a call does nothing. When the load fails, the contacts that loaded stay, and a later call
    /// tries again. A load can fail with no connection, no answer or a network error.
    /// </remarks>
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
            // The next open of Start chat tries again.
            _store.LogFailure("load contacts", ex);
        }
        finally
        {
            Busy(-1);
        }
    }

    /// <summary>
    /// Picks a person, or removes the person from the chips when the person is already picked.
    /// </summary>
    /// <remarks>A pick of the "Send to" row clears the query.</remarks>
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

        // A picked person leaves the list, because the chip shows the person. A removed person comes back.
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
    /// Opens or creates the conversation with all picked people.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The method ports the create flow of the bridge. The request sends each number in this form:
    /// <c>ContactNumber { MysteriousInt = 2, Number = n, Number2 = n }</c>. When the phone answers <c>CreateRcs</c>,
    /// the method sends the request again as an RCS group with an empty name.
    /// </para>
    /// <para>
    /// On a failure, <see cref="Error"/> tells the user and all picked people stay. The method throws nothing. A
    /// failure is no connection, no answer, a closed connection, or a network, timeout, server or decode error. An
    /// answer without success and a conversation is also a failure, after the RCS retry too.
    /// </para>
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
            // The click handler must get no exception.
            _store.LogFailure("start chat", ex);
            Error = ChatCopy.StartFailed;
            return null;
        }
        finally
        {
            Busy(-1);
        }
    }

    private void Busy(int change)
    {
        _busy += change;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoading)));
    }

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

        if (response.Status != GetOrCreateConversationResponse.Types.Status.Success
            || string.IsNullOrEmpty(response.Conversation?.ConversationID))
        {
            throw new MessageSendException(response.Status.ToString());
        }

        return response.Conversation.ConversationID;
    }

    // The "Send to" row comes first, then the top contacts, then the rest. A picked person shows only as a chip. When
    // the rows stay the same, the list stays as it is. The full list arrives after the top contacts, and a row under a
    // click must stay the same object.
    private void Refresh()
    {
        string query              = _query.Trim();
        string digits             = ContactRowViewModel.DigitsOf(query);
        HashSet<string> topIds    = [.. _top.Select(c => c.Number)];
        HashSet<string> pickedIds = [.. Selected.Select(c => c.Number)];

        IEnumerable<ContactRowViewModel> matches = _top
            .Concat(_all.Where(c => !topIds.Contains(c.Number)))
            .Where(c => !pickedIds.Contains(c.Number))
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

        // The list changes in place, with no clear and refill. The view then animates only the changes and keeps its
        // scroll position. A picked row fades out, and the rows below it move up. In plan 9 the owner saw a refill make
        // the scroll jump.
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
