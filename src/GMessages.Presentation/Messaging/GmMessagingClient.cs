using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// The real <see cref="IMessagingClient"/>: every member forwards to the protocol client.
/// </summary>
/// <param name="client">The protocol client.</param>
public sealed class GmMessagingClient(GmClient client) : IMessagingClient
{
    /// <inheritdoc />
    public Task<ListConversationsResponse> ListConversationsAsync(ListConversationsRequest.Types.Folder folder, long count, Cursor? cursor, CancellationToken ct) => client.ListConversationsAsync(folder, count, cursor, ct);

    /// <inheritdoc />
    public Task<Conversation?> GetConversationAsync(string conversationId, CancellationToken ct) => client.GetConversationAsync(conversationId, ct);

    /// <inheritdoc />
    public Task<GetOrCreateConversationResponse> GetOrCreateConversationAsync(GetOrCreateConversationRequest request, CancellationToken ct) => client.GetOrCreateConversationAsync(request, ct);

    /// <inheritdoc />
    public Task<UpdateConversationResponse> UpdateConversationAsync(UpdateConversationRequest request, CancellationToken ct) => client.UpdateConversationAsync(request, ct);

    /// <inheritdoc />
    public Task<ListMessagesResponse> FetchMessagesAsync(string conversationId, long count, Cursor? cursor, CancellationToken ct) => client.FetchMessagesAsync(conversationId, count, cursor, ct);

    /// <inheritdoc />
    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken ct) => client.SendMessageAsync(request, ct);

    /// <inheritdoc />
    public Task<SendReactionResponse> SendReactionAsync(SendReactionRequest request, CancellationToken ct) => client.SendReactionAsync(request, ct);

    /// <inheritdoc />
    public Task SetTypingAsync(string conversationId, SIMPayload? sim, CancellationToken ct) => client.SetTypingAsync(conversationId, sim, ct);

    /// <inheritdoc />
    public Task MarkReadAsync(string conversationId, string messageId, CancellationToken ct) => client.MarkReadAsync(conversationId, messageId, ct);

    /// <inheritdoc />
    public Task<ListContactsResponse> ListContactsAsync(CancellationToken ct) => client.ListContactsAsync(ct);

    /// <inheritdoc />
    public Task<ListTopContactsResponse> ListTopContactsAsync(CancellationToken ct) => client.ListTopContactsAsync(ct);

    /// <inheritdoc />
    public Task<GetThumbnailResponse> GetParticipantThumbnailAsync(IReadOnlyList<string> participantIds, CancellationToken ct) => client.GetParticipantThumbnailAsync(participantIds, ct);

    /// <inheritdoc />
    public Task<GetThumbnailResponse> GetContactThumbnailAsync(IReadOnlyList<string> contactIds, CancellationToken ct) => client.GetContactThumbnailAsync(contactIds, ct);

    /// <inheritdoc />
    public Task<MediaContent> UploadMediaAsync(ReadOnlyMemory<byte> plain, string fileName, string mime, IProgress<double>? progress, CancellationToken ct) => client.UploadMediaAsync(plain, fileName, mime, progress, ct);

    /// <inheritdoc />
    public Task<byte[]> DownloadMediaAsync(string mediaId, ReadOnlyMemory<byte> key, CancellationToken ct) => client.DownloadMediaAsync(mediaId, key, ct);

    /// <inheritdoc />
    public Task GetFullSizeImageAsync(string messageId, string actionMessageId, CancellationToken ct) => client.GetFullSizeImageAsync(messageId, actionMessageId, ct);
}
