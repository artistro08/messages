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

    /// <summary>When set, the next conversation fetch answers only after this completes.</summary>
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

    public List<UpdateConversationRequest> ConversationUpdates { get; } = [];

    /// <summary>When set, conversation updates are answered with <c>Success = false</c>.</summary>
    public bool RefuseUpdates { get; set; }

    /// <summary>When set, the next conversation update answers only after this completes.</summary>
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

    public SendReactionResponse ReactionAnswer { get; set; } = new() { Success = true };

    public List<SendReactionRequest> Reactions { get; } = [];

    /// <summary>When set, sending a reaction fails with this.</summary>
    public Exception? ReactionThrows { get; set; }

    public Task<SendReactionResponse> SendReactionAsync(SendReactionRequest request, CancellationToken ct)
    {
        Reactions.Add(request.Clone());
        return ReactionThrows is { } ex ? Task.FromException<SendReactionResponse>(ex) : Task.FromResult(ReactionAnswer);
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

    /// <summary>Uploaded files by media ID (plain bytes), and what downloads answer with.</summary>
    public Dictionary<string, byte[]> Media { get; } = [];

    public List<(string MessageId, string ActionMessageId)> FullSizeCalls { get; } = [];

    public Task<MediaContent> UploadMediaAsync(ReadOnlyMemory<byte> plain, string fileName, string mime, IProgress<double>? progress, CancellationToken ct)
    {
        string id = "media-" + (Media.Count + 1);
        Media[id] = plain.ToArray();
        progress?.Report(1);
        return Task.FromResult(new MediaContent { MediaID = id, MediaName = fileName, MimeType = mime, Size = plain.Length, Format = GMessages.Protocol.MediaFormatsMap.FromMime(mime) });
    }

    /// <summary>Every media ID a download asked for, in order (downloads run on the thread pool, so lock it to read).</summary>
    public List<string> DownloadCalls { get; } = [];

    /// <summary>When set, downloads wait for it before answering.</summary>
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
