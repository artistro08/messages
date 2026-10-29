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

    public List<(ListConversationsRequest.Types.Folder Folder, long Count)> ListCalls { get; } = [];

    public Task<ListConversationsResponse> ListConversationsAsync(ListConversationsRequest.Types.Folder folder, long count, Cursor? cursor, CancellationToken ct)
    {
        ListCalls.Add((folder, count));
        return Task.FromResult(ConversationsPage);
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

    public Task<GetOrCreateConversationResponse> GetOrCreateConversationAsync(GetOrCreateConversationRequest request, CancellationToken ct)
    {
        CreateCalls.Add(request.Clone());
        return Task.FromResult(CreateAnswers.Dequeue());
    }

    public Task<ListMessagesResponse> FetchMessagesAsync(string conversationId, long count, Cursor? cursor, CancellationToken ct) => Task.FromResult(MessagesPage);

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken ct)
    {
        Sent.Add(request);
        return Task.FromResult(SendAnswer);
    }

    public Task MarkReadAsync(string conversationId, string messageId, CancellationToken ct) => Task.CompletedTask;

    public ListContactsResponse ContactsAnswer { get; set; } = new();

    public ListTopContactsResponse TopContactsAnswer { get; set; } = new();

    public Task<ListContactsResponse> ListContactsAsync(CancellationToken ct) => Task.FromResult(ContactsAnswer);

    public Task<ListTopContactsResponse> ListTopContactsAsync(CancellationToken ct) => Task.FromResult(TopContactsAnswer);

    public Task<GetThumbnailResponse> GetParticipantThumbnailAsync(IReadOnlyList<string> participantIds, CancellationToken ct) => throw new NotSupportedException();

    public Task<GetThumbnailResponse> GetContactThumbnailAsync(IReadOnlyList<string> contactIds, CancellationToken ct) => throw new NotSupportedException();
}
