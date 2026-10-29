using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// The requests the chat code sends to the phone; <see cref="GmMessagingClient"/> forwards them to the protocol client.
/// </summary>
/// <remarks>Exists so the chat code can be tested with a fake. Each member matches the <c>GmClient</c> method of the same name.</remarks>
public interface IMessagingClient
{
    /// <summary>Lists the conversations of a folder, newest first.</summary>
    Task<ListConversationsResponse> ListConversationsAsync(ListConversationsRequest.Types.Folder folder, long count, Cursor? cursor, CancellationToken ct);

    /// <summary>Gets one conversation; null when the phone has none.</summary>
    Task<Conversation?> GetConversationAsync(string conversationId, CancellationToken ct);

    /// <summary>Finds the conversation with the given numbers, or creates it.</summary>
    Task<GetOrCreateConversationResponse> GetOrCreateConversationAsync(GetOrCreateConversationRequest request, CancellationToken ct);

    /// <summary>Fetches a page of messages of a conversation, newest first.</summary>
    Task<ListMessagesResponse> FetchMessagesAsync(string conversationId, long count, Cursor? cursor, CancellationToken ct);

    /// <summary>Sends a message.</summary>
    Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken ct);

    /// <summary>Adds, switches or removes a reaction on a message.</summary>
    Task<SendReactionResponse> SendReactionAsync(SendReactionRequest request, CancellationToken ct);

    /// <summary>Tells the other side of a conversation that the user is typing; waits for no answer.</summary>
    Task SetTypingAsync(string conversationId, SIMPayload? sim, CancellationToken ct);

    /// <summary>Tells the phone a conversation was read up to a message.</summary>
    Task MarkReadAsync(string conversationId, string messageId, CancellationToken ct);

    /// <summary>Lists the phone's contacts.</summary>
    Task<ListContactsResponse> ListContactsAsync(CancellationToken ct);

    /// <summary>Lists the phone's most used contacts.</summary>
    Task<ListTopContactsResponse> ListTopContactsAsync(CancellationToken ct);

    /// <summary>Gets the photos of conversation participants.</summary>
    Task<GetThumbnailResponse> GetParticipantThumbnailAsync(IReadOnlyList<string> participantIds, CancellationToken ct);

    /// <summary>Gets the photos of contacts.</summary>
    Task<GetThumbnailResponse> GetContactThumbnailAsync(IReadOnlyList<string> contactIds, CancellationToken ct);
}
