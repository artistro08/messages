using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Tests.Messaging;

/// <summary>
/// An in-memory <see cref="IMessagingClient"/>: tests set the answers and read what was sent.
/// </summary>
internal sealed class FakeMessagingClient : IMessagingClient
{
    public Dictionary<string, Conversation> Conversations { get; } = [];

    public bool GetConversationThrows { get; set; }

    public bool YieldInFetch { get; set; }

    public List<string> GetConversationCalls { get; } = [];

    public ListConversationsResponse ConversationsPage { get; set; } = new();

    public ListMessagesResponse MessagesPage { get; set; } = new();

    public SendMessageResponse SendAnswer { get; set; } = new() { Status = SendMessageResponse.Types.Status.Success };

    public List<SendMessageRequest> Sent { get; } = [];

    /// <summary>When set, sending throws this instead of answering.</summary>
    public Exception? SendThrows { get; set; }

    public List<(string ConversationId, string MessageId)> MarkedRead { get; } = [];

    public List<(ListConversationsRequest.Types.Folder Folder, long Count)> ListCalls { get; } = [];

    /// <summary>When set, the next list call returns the page it had when called, but only after this completes.</summary>
    public TaskCompletionSource? ListGate { get; set; }

    /// <summary>When set, listing conversations fails with this.</summary>
    public Exception? ListThrows { get; set; }

    public async Task<ListConversationsResponse> ListConversationsAsync(ListConversationsRequest.Types.Folder folder, long count, Cursor? cursor, CancellationToken ct)
    {
        ListCalls.Add((folder, count));
        if (ListThrows is { } ex)
        {
            throw ex;
        }

        ListConversationsResponse page = ConversationsPage;

        if (ListGate is { } gate)
        {
            ListGate = null;
            await gate.Task;
        }

        return page;
    }

    public async Task<Conversation?> GetConversationAsync(string conversationId, CancellationToken ct)
    {
        if (YieldInFetch)
        {
            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        GetConversationCalls.Add(conversationId);
        if (GetConversationThrows)
        {
            throw new InvalidOperationException("fetch failed");
        }

        return Conversations.GetValueOrDefault(conversationId);
    }

    public Queue<GetOrCreateConversationResponse> CreateAnswers { get; } = new();

    public List<GetOrCreateConversationRequest> CreateCalls { get; } = [];

    /// <summary>When set, creating a conversation fails with this.</summary>
    public Exception? CreateThrows { get; set; }

    /// <summary>When set, the next create call answers only after this completes.</summary>
    public TaskCompletionSource? CreateGate { get; set; }

    public async Task<GetOrCreateConversationResponse> GetOrCreateConversationAsync(GetOrCreateConversationRequest request, CancellationToken ct)
    {
        CreateCalls.Add(request.Clone());
        if (CreateGate is { } gate)
        {
            CreateGate = null;
            await gate.Task;
        }

        return CreateThrows is { } ex ? throw ex : CreateAnswers.Dequeue();
    }

    /// <summary>Pages returned by successive message fetches before falling back to <see cref="MessagesPage"/>.</summary>
    public Queue<ListMessagesResponse> MessagePages { get; } = new();

    /// <summary>When set, the next message fetch picks its page now but returns it only after this completes.</summary>
    public TaskCompletionSource? FetchGate { get; set; }

    public int FetchCount { get; private set; }

    /// <summary>When set, message fetches with a cursor (older pages) fail with this.</summary>
    public Exception? OlderFetchThrows { get; set; }

    public async Task<ListMessagesResponse> FetchMessagesAsync(string conversationId, long count, Cursor? cursor, CancellationToken ct)
    {
        FetchCount++;
        if (cursor is not null && OlderFetchThrows is { } ex)
        {
            throw ex;
        }

        ListMessagesResponse page = MessagePages.Count > 0 ? MessagePages.Dequeue() : MessagesPage;

        if (FetchGate is { } gate)
        {
            FetchGate = null;
            await gate.Task;
        }

        return page;
    }

    /// <summary>When set, the next send answers only after this completes.</summary>
    public TaskCompletionSource? SendAnswerGate { get; set; }

    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken ct)
    {
        Sent.Add(request);
        if (SendAnswerGate is { } gate)
        {
            await gate.Task;
        }

        return SendThrows is { } ex ? throw ex : SendAnswer;
    }

    public Task MarkReadAsync(string conversationId, string messageId, CancellationToken ct)
    {
        MarkedRead.Add((conversationId, messageId));
        return Task.CompletedTask;
    }

    public ListContactsResponse ContactsAnswer { get; set; } = new();

    public ListTopContactsResponse TopContactsAnswer { get; set; } = new();

    /// <summary>When set, the full contact list fails with this (the top contacts still load).</summary>
    public Exception? ContactsThrows { get; set; }

    public Task<ListContactsResponse> ListContactsAsync(CancellationToken ct) =>
        ContactsThrows is { } ex ? Task.FromException<ListContactsResponse>(ex) : Task.FromResult(ContactsAnswer);

    /// <summary>When set, the next top contacts call answers only after this completes.</summary>
    public TaskCompletionSource? TopContactsGate { get; set; }

    public async Task<ListTopContactsResponse> ListTopContactsAsync(CancellationToken ct)
    {
        if (TopContactsGate is { } gate)
        {
            TopContactsGate = null;
            await gate.Task;
        }

        return TopContactsAnswer;
    }

    /// <summary>Photos by participant ID; IDs not here get no thumbnail.</summary>
    public Dictionary<string, byte[]> Photos { get; } = [];

    public List<IReadOnlyList<string>> ThumbnailCalls { get; } = [];

    public Task<GetThumbnailResponse> GetParticipantThumbnailAsync(IReadOnlyList<string> participantIds, CancellationToken ct)
    {
        ThumbnailCalls.Add([.. participantIds]);

        var response = new GetThumbnailResponse();
        foreach (string id in participantIds.Where(Photos.ContainsKey))
        {
            response.Thumbnail.Add(new GetThumbnailResponse.Types.Thumbnail { Identifier = id, Data = new ThumbnailData { ImageBuffer = Google.Protobuf.ByteString.CopyFrom(Photos[id]) } });
        }

        return Task.FromResult(response);
    }

    public Task<GetThumbnailResponse> GetContactThumbnailAsync(IReadOnlyList<string> contactIds, CancellationToken ct) => throw new NotSupportedException();
}
