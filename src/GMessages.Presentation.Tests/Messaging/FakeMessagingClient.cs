using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Tests.Messaging;

/// <summary>
/// An in-memory <see cref="IMessagingClient"/>. A test sets the answers and reads what the client sent.
/// </summary>
internal sealed class FakeMessagingClient : IMessagingClient
{
    public Dictionary<string, Conversation> Conversations { get; } = [];

    public bool GetConversationThrows { get; set; }

    public bool YieldInFetch { get; set; }

    public List<string> GetConversationCalls { get; } = [];

    public ListConversationsResponse ConversationsPage { get; set; } = new();

    /// <summary>
    /// The next list calls return these pages in order. After the queue is empty, they return
    /// <see cref="ConversationsPage"/>.
    /// </summary>
    public Queue<ListConversationsResponse> NextPages { get; } = new();

    public ListMessagesResponse MessagesPage { get; set; } = new();

    public SendMessageResponse SendAnswer { get; set; } = new() { Status = SendMessageResponse.Types.Status.Success };

    public List<SendMessageRequest> Sent { get; } = [];

    /// <summary>When set, a send throws this exception and gives no answer.</summary>
    public Exception? SendThrows { get; set; }

    public List<(string ConversationId, string MessageId)> MarkedRead { get; } = [];

    public List<(ListConversationsRequest.Types.Folder Folder, long Count)> ListCalls { get; } = [];

    /// <summary>The cursor of each list call, in call order. A null cursor asks for the newest page.</summary>
    public List<Cursor?> ListCursors { get; } = [];

    /// <summary>
    /// When set, the next list call picks its page at once and returns the page after this completes.
    /// </summary>
    public TaskCompletionSource? ListGate { get; set; }

    /// <summary>When set, a list call fails with this exception.</summary>
    public Exception? ListThrows { get; set; }

    public async Task<ListConversationsResponse> ListConversationsAsync(ListConversationsRequest.Types.Folder folder, long count, Cursor? cursor, CancellationToken ct)
    {
        ListCalls.Add((folder, count));
        ListCursors.Add(cursor);
        if (ListThrows is { } ex)
        {
            throw ex;
        }

        ListConversationsResponse page = NextPages.Count > 0 ? NextPages.Dequeue() : ConversationsPage;

        if (ListGate is { } gate)
        {
            ListGate = null;
            await gate.Task;
        }

        return page;
    }

    /// <summary>When set, the next conversation fetch gives its answer after this completes.</summary>
    public TaskCompletionSource? GetConversationGate { get; set; }

    public async Task<Conversation?> GetConversationAsync(string conversationId, CancellationToken ct)
    {
        if (GetConversationGate is { } gate)
        {
            GetConversationGate = null;
            await gate.Task;
        }

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

    /// <summary>When set, a create call fails with this exception.</summary>
    public Exception? CreateThrows { get; set; }

    /// <summary>When set, the next create call gives its answer after this completes.</summary>
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

    public List<UpdateConversationRequest> ConversationUpdates { get; } = [];

    /// <summary>When set, each conversation update answers with <c>Success = false</c>.</summary>
    public bool RefuseUpdates { get; set; }

    /// <summary>When set, the next conversation update gives its answer after this completes.</summary>
    public TaskCompletionSource? UpdateGate { get; set; }

    public async Task<UpdateConversationResponse> UpdateConversationAsync(UpdateConversationRequest request, CancellationToken ct)
    {
        ConversationUpdates.Add(request.Clone());
        if (UpdateGate is { } gate)
        {
            UpdateGate = null;
            await gate.Task;
        }

        return new UpdateConversationResponse { Success = !RefuseUpdates };
    }

    /// <summary>
    /// Successive message fetches return these pages. After the queue is empty, they return
    /// <see cref="MessagesPage"/>.
    /// </summary>
    public Queue<ListMessagesResponse> MessagePages { get; } = new();

    /// <summary>
    /// When set, the next message fetch picks its page at once and returns the page after this completes.
    /// </summary>
    public TaskCompletionSource? FetchGate { get; set; }

    public int FetchCount { get; private set; }

    /// <summary>
    /// When set, a message fetch with a cursor, which asks for older pages, fails with this exception.
    /// </summary>
    public Exception? OlderFetchThrows { get; set; }

    /// <summary>
    /// When set, each message fetch gets its page from the count and cursor, as the phone does.
    /// This takes priority over the queue.
    /// </summary>
    public Func<long, Cursor?, ListMessagesResponse>? PageFor { get; set; }

    public async Task<ListMessagesResponse> FetchMessagesAsync(string conversationId, long count, Cursor? cursor, CancellationToken ct)
    {
        FetchCount++;
        if (cursor is not null && OlderFetchThrows is { } ex)
        {
            throw ex;
        }

        ListMessagesResponse page = PageFor?.Invoke(count, cursor) ?? (MessagePages.Count > 0 ? MessagePages.Dequeue() : MessagesPage);

        if (FetchGate is { } gate)
        {
            FetchGate = null;
            await gate.Task;
        }

        return page;
    }

    /// <summary>When set, the next send gives its answer after this completes.</summary>
    public TaskCompletionSource? SendAnswerGate { get; set; }

    /// <summary>When set, a send for which this returns true throws an <see cref="HttpRequestException"/>.</summary>
    public Func<SendMessageRequest, bool>? SendFails { get; set; }

    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken ct)
    {
        Sent.Add(request);
        if (SendAnswerGate is { } gate)
        {
            await gate.Task;
        }

        if (SendFails?.Invoke(request) == true)
        {
            throw new HttpRequestException();
        }

        return SendThrows is { } ex ? throw ex : SendAnswer;
    }

    public SendReactionResponse ReactionAnswer { get; set; } = new() { Success = true };

    public List<SendReactionRequest> Reactions { get; } = [];

    /// <summary>When set, a reaction send fails with this exception.</summary>
    public Exception? ReactionThrows { get; set; }

    public Task<SendReactionResponse> SendReactionAsync(SendReactionRequest request, CancellationToken ct)
    {
        Reactions.Add(request.Clone());
        return ReactionThrows is { } ex ? Task.FromException<SendReactionResponse>(ex) : Task.FromResult(ReactionAnswer);
    }

    /// <summary>Every message ID that a delete call asked for.</summary>
    public List<string> Deletes { get; } = [];

    /// <summary>Whether the phone reports that it deleted the message.</summary>
    public bool DeleteSucceeds { get; set; } = true;

    public Task<DeleteMessageResponse> DeleteMessageAsync(string messageId, CancellationToken ct)
    {
        Deletes.Add(messageId);
        return Task.FromResult(new DeleteMessageResponse { Success = DeleteSucceeds });
    }

    public List<(string ConversationId, SIMPayload? Sim)> Typing { get; } = [];

    public Task SetTypingAsync(string conversationId, SIMPayload? sim, CancellationToken ct)
    {
        Typing.Add((conversationId, sim?.Clone()));
        return Task.CompletedTask;
    }

    public Task MarkReadAsync(string conversationId, string messageId, CancellationToken ct)
    {
        MarkedRead.Add((conversationId, messageId));
        return Task.CompletedTask;
    }

    public ListContactsResponse ContactsAnswer { get; set; } = new();

    public ListTopContactsResponse TopContactsAnswer { get; set; } = new();

    /// <summary>When set, the full contact list fails with this exception. The top contacts still load.</summary>
    public Exception? ContactsThrows { get; set; }

    public Task<ListContactsResponse> ListContactsAsync(CancellationToken ct) =>
        ContactsThrows is { } ex ? Task.FromException<ListContactsResponse>(ex) : Task.FromResult(ContactsAnswer);

    /// <summary>When set, the next top contacts call gives its answer after this completes.</summary>
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

    /// <summary>Photos by participant ID. A participant ID that is not here gets no thumbnail.</summary>
    public Dictionary<string, byte[]> Photos { get; } = [];

    public List<IReadOnlyList<string>> ThumbnailCalls { get; } = [];

    /// <summary>When set, the next thumbnail call gives its answer after this completes.</summary>
    public TaskCompletionSource? ThumbnailGate { get; set; }

    public async Task<GetThumbnailResponse> GetParticipantThumbnailAsync(IReadOnlyList<string> participantIds, CancellationToken ct)
    {
        ThumbnailCalls.Add([.. participantIds]);
        if (ThumbnailGate is { } gate)
        {
            ThumbnailGate = null;
            await gate.Task;
        }

        var response = new GetThumbnailResponse();
        foreach (string id in participantIds.Where(Photos.ContainsKey))
        {
            response.Thumbnail.Add(new GetThumbnailResponse.Types.Thumbnail { Identifier = id, Data = new ThumbnailData { ImageBuffer = Google.Protobuf.ByteString.CopyFrom(Photos[id]) } });
        }

        return response;
    }

    public Task<GetThumbnailResponse> GetContactThumbnailAsync(IReadOnlyList<string> contactIds, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>Uploaded files by media ID, as plain bytes. A download answers with these bytes.</summary>
    public Dictionary<string, byte[]> Media { get; } = [];

    /// <summary>When set, an upload throws this exception and gives no answer.</summary>
    public Exception? UploadThrows { get; set; }

    public List<(string MessageId, string ActionMessageId)> FullSizeCalls { get; } = [];

    public Task<MediaContent> UploadMediaAsync(ReadOnlyMemory<byte> plain, string fileName, string mime, IProgress<double>? progress, CancellationToken ct)
    {
        if (UploadThrows is { } ex)
        {
            throw ex;
        }

        string id = "media-" + (Media.Count + 1);
        Media[id] = plain.ToArray();
        progress?.Report(1);
        return Task.FromResult(new MediaContent { MediaID = id, MediaName = fileName, MimeType = mime, Size = plain.Length, Format = GMessages.Protocol.MediaFormatsMap.FromMime(mime) });
    }

    /// <summary>
    /// Every media ID that a download asked for, in order.
    /// Downloads run on the thread pool, so a reader must lock this list.
    /// </summary>
    public List<string> DownloadCalls { get; } = [];

    /// <summary>When set, a download waits for this before it answers.</summary>
    public TaskCompletionSource? DownloadGate { get; set; }

    public async Task<byte[]> DownloadMediaAsync(string mediaId, ReadOnlyMemory<byte> key, CancellationToken ct)
    {
        lock (DownloadCalls)
        {
            DownloadCalls.Add(mediaId);
        }

        if (DownloadGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        return Media.TryGetValue(mediaId, out byte[]? bytes) ? bytes : throw new KeyNotFoundException(mediaId);
    }

    public Task GetFullSizeImageAsync(string messageId, string actionMessageId, CancellationToken ct)
    {
        FullSizeCalls.Add((messageId, actionMessageId));
        return Task.CompletedTask;
    }
}
