using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using Google.Protobuf;

namespace GMessages.Protocol;

/// <summary>
/// The requests that the app sends to the phone: conversations, messages, contacts and thumbnails.
/// </summary>
/// <remarks>
/// Ported from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">methods.go</see>.
/// Each method sends 1 request and waits for the answer of the phone. The answer comes over the long poll, so the
/// client must be connected. A method that expects a typed answer throws <see cref="InvalidDataException"/> for an
/// answer of a different type. That is Go's <c>typedResponse</c> in session_handler.go. The client never logs
/// message contents. <see cref="RpcSession"/> logs only request IDs and actions. This port includes only the
/// requests that the app uses.
/// </remarks>
public sealed partial class GmClient
{
    // Go's conversationsFetchedOnce. The value is 1 after the first ListConversations.
    private int _conversationsFetchedOnce;

    /// <summary>
    /// Lists the conversations of a folder, newest first.
    /// </summary>
    /// <remarks>
    /// Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">ListConversations</see>.
    /// As in Go, the first call on a client goes as a <see cref="MessageType.BugleAnnotation"/> message. Later calls go
    /// as <see cref="MessageType.BugleMessage"/>. To get the next page, give the cursor of the previous answer.
    /// </remarks>
    /// <param name="folder">The folder to list.</param>
    /// <param name="count">How many conversations to ask for.</param>
    /// <param name="cursor">Where the previous page ended, or null for the first page.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The conversations and the cursor for the next page.</returns>
    /// <exception cref="InvalidDataException">The phone answered with another type.</exception>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<ListConversationsResponse> ListConversationsAsync(ListConversationsRequest.Types.Folder folder, long count, Cursor? cursor, CancellationToken ct)
    {
        var request = new ListConversationsRequest { Folder = folder, Count = count, Cursor = cursor };

        MessageType messageType = Interlocked.Exchange(ref _conversationsFetchedOnce, 1) == 0 ? MessageType.BugleAnnotation : MessageType.BugleMessage;

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(ActionType.ListConversations, request, MessageType: messageType), ct).ConfigureAwait(false);
        return Typed<ListConversationsResponse>(answer);
    }

    /// <summary>
    /// Gets one conversation.
    /// </summary>
    /// <remarks>Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">GetConversation</see>.</remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The conversation, or null when the answer has none. Go returns nil in that case.</returns>
    /// <exception cref="InvalidDataException">The phone answered with another type.</exception>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<Conversation?> GetConversationAsync(string conversationId, CancellationToken ct)
    {
        var request = new GetConversationRequest { ConversationID = conversationId };

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(ActionType.GetConversation, request), ct).ConfigureAwait(false);
        return Typed<GetConversationResponse>(answer).Conversation;
    }

    /// <summary>
    /// Finds the conversation with the given numbers, or creates it.
    /// </summary>
    /// <remarks>Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">GetOrCreateConversation</see>.</remarks>
    /// <param name="request">The numbers, and the RCS group name and flag for a new RCS group.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The conversation and whether the phone found it, created it or needs RCS for it.</returns>
    /// <exception cref="InvalidDataException">The phone answered with another type.</exception>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<GetOrCreateConversationResponse> GetOrCreateConversationAsync(GetOrCreateConversationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(ActionType.GetOrCreateConversation, request), ct).ConfigureAwait(false);
        return Typed<GetOrCreateConversationResponse>(answer);
    }

    /// <summary>
    /// Archives, unarchives, mutes, unmutes, blocks, unblocks or deletes a conversation.
    /// </summary>
    /// <remarks>
    /// Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">UpdateConversation</see>.
    /// The request is user-initiated, as in <see cref="SendMessageAsync"/>. Archive and mute go in the update data of
    /// the request, as a new status or mute state. Block, unblock and delete go in its action, with the delete data.
    /// The phone then sends the conversation again with its new state.
    /// </remarks>
    /// <param name="request">The conversation ID, the action and the update or delete data.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>Whether the phone accepted the update.</returns>
    /// <exception cref="InvalidDataException">The phone answered with another type.</exception>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<UpdateConversationResponse> UpdateConversationAsync(UpdateConversationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(ActionType.UpdateConversation, request, UserInitiated: true), ct).ConfigureAwait(false);
        return Typed<UpdateConversationResponse>(answer);
    }

    /// <summary>
    /// Fetches a page of messages of a conversation, newest first.
    /// </summary>
    /// <remarks>Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">FetchMessages</see>.</remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="count">How many messages to ask for.</param>
    /// <param name="cursor">Where the previous page ended, or null for the newest page.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The messages and the cursor for the next page.</returns>
    /// <exception cref="InvalidDataException">The phone answered with another type.</exception>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<ListMessagesResponse> FetchMessagesAsync(string conversationId, long count, Cursor? cursor, CancellationToken ct)
    {
        var request = new ListMessagesRequest { ConversationID = conversationId, Count = count, Cursor = cursor };

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(ActionType.ListMessages, request), ct).ConfigureAwait(false);
        return Typed<ListMessagesResponse>(answer);
    }

    /// <summary>
    /// Sends a message.
    /// </summary>
    /// <remarks>
    /// Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">SendMessage</see>.
    /// The request is user-initiated, as in Go's <c>sendUserMessage</c>. When the phone does not answer in 5 seconds,
    /// <see cref="RpcSession.PhoneSlow"/> occurs 1 time and the wait continues. Use <see cref="NewTmpId"/> for the
    /// temporary IDs of the request.
    /// </remarks>
    /// <param name="request">The message to send.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The phone's status for the send.</returns>
    /// <exception cref="InvalidDataException">The phone answered with another type.</exception>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(ActionType.SendMessage, request, UserInitiated: true), ct).ConfigureAwait(false);
        return Typed<SendMessageResponse>(answer);
    }

    /// <summary>
    /// Adds, switches or removes a reaction on a message.
    /// </summary>
    /// <remarks>
    /// Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">SendReaction</see>.
    /// The request is user-initiated, as in <see cref="SendMessageAsync"/>. Use <see cref="EmojiTypes.MakeReactionData"/>
    /// for the reaction data of the request. The phone then sends the message again with its new reactions.
    /// </remarks>
    /// <param name="request">The message ID, reaction data, action and SIM.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>Whether the phone accepted the reaction.</returns>
    /// <exception cref="InvalidDataException">The phone answered with another type.</exception>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<SendReactionResponse> SendReactionAsync(SendReactionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(ActionType.SendReaction, request, UserInitiated: true), ct).ConfigureAwait(false);
        return Typed<SendReactionResponse>(answer);
    }

    /// <summary>
    /// Tells the other side of a conversation that the user is typing.
    /// </summary>
    /// <remarks>
    /// Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">SetTyping</see>.
    /// As in Go, the method always sends typing as true and does not wait for an answer. The phone stops the typing
    /// display by itself.
    /// </remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="sim">The SIM of the conversation, or null.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>A task that completes when the server has accepted the request.</returns>
    /// <exception cref="Http.GmRequestException">The server refused the request.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public Task SetTypingAsync(string conversationId, SIMPayload? sim, CancellationToken ct)
    {
        var request = new TypingUpdateRequest
        {
            Data       = new TypingUpdateRequest.Types.Data { ConversationID = conversationId, Typing = true },
            SIMPayload = sim,
        };

        return Rpc.SendNoResponseAsync(new SendParams(ActionType.TypingUpdates, request), ct);
    }

    /// <summary>
    /// Tells the phone that a conversation was read up to a message.
    /// </summary>
    /// <remarks>
    /// Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">MarkRead</see>.
    /// As in Go, the method waits for the answer of the phone and ignores its content.
    /// </remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="messageId">The ID of the newest message that was read.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task that completes when the phone answered.</returns>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task MarkReadAsync(string conversationId, string messageId, CancellationToken ct)
    {
        var request = new MessageReadRequest { ConversationID = conversationId, MessageID = messageId };

        await Rpc.SendAsync(new SendParams(ActionType.MessageRead, request), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a message from the phone.
    /// </summary>
    /// <remarks>Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">DeleteMessage</see>. Only the copy on this side goes. The other side keeps its copy.</remarks>
    /// <param name="messageId">The message ID.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The answer of the phone. <see cref="DeleteMessageResponse.Success"/> tells if the phone deleted the message.</returns>
    /// <exception cref="ArgumentException"><paramref name="messageId"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The phone answered with another type.</exception>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<DeleteMessageResponse> DeleteMessageAsync(string messageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(ActionType.DeleteMessage, new DeleteMessageRequest { MessageID = messageId }), ct).ConfigureAwait(false);
        return Typed<DeleteMessageResponse>(answer);
    }

    /// <summary>
    /// Makes a temporary ID for a message that is about to be sent.
    /// </summary>
    /// <remarks>Ported from <c>GenerateTmpID</c> in <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/util/func.go">util/func.go</see>. The ID is a random GUID, as in the official app.</remarks>
    /// <returns>A new GUID as text.</returns>
    public static string NewTmpId() => Guid.NewGuid().ToString();

    /// <summary>
    /// Lists the phone's contacts.
    /// </summary>
    /// <remarks>Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">ListContacts</see>. As in Go, the request always has the values 1, 350 and 50.</remarks>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The contacts.</returns>
    /// <exception cref="InvalidDataException">The phone answered with another type.</exception>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<ListContactsResponse> ListContactsAsync(CancellationToken ct)
    {
        var request = new ListContactsRequest { I1 = 1, I2 = 350, I3 = 50 };

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(ActionType.ListContacts, request), ct).ConfigureAwait(false);
        return Typed<ListContactsResponse>(answer);
    }

    /// <summary>
    /// Lists the phone's most used contacts.
    /// </summary>
    /// <remarks>Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">ListTopContacts</see>. As in Go, the request always asks for 8.</remarks>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The top contacts.</returns>
    /// <exception cref="InvalidDataException">The phone answered with another type.</exception>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<ListTopContactsResponse> ListTopContactsAsync(CancellationToken ct)
    {
        var request = new ListTopContactsRequest { Count = 8 };

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(ActionType.ListTopContacts, request), ct).ConfigureAwait(false);
        return Typed<ListTopContactsResponse>(answer);
    }

    /// <summary>
    /// Gets the photos of conversation participants.
    /// </summary>
    /// <remarks>Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">GetParticipantThumbnail</see>.</remarks>
    /// <param name="participantIds">The participant IDs.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The thumbnails.</returns>
    /// <exception cref="InvalidDataException">The phone answered with another type.</exception>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public Task<GetThumbnailResponse> GetParticipantThumbnailAsync(IReadOnlyList<string> participantIds, CancellationToken ct) =>
        GetThumbnailAsync(ActionType.GetParticipantsThumbnail, participantIds, ct);

    /// <summary>
    /// Gets the photos of contacts.
    /// </summary>
    /// <remarks>Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">GetContactThumbnail</see>.</remarks>
    /// <param name="contactIds">The contact IDs.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The thumbnails.</returns>
    /// <exception cref="InvalidDataException">The phone answered with another type.</exception>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public Task<GetThumbnailResponse> GetContactThumbnailAsync(IReadOnlyList<string> contactIds, CancellationToken ct) =>
        GetThumbnailAsync(ActionType.GetContactsThumbnail, contactIds, ct);

    private async Task<GetThumbnailResponse> GetThumbnailAsync(ActionType action, IReadOnlyList<string> identifiers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifiers);

        var request = new GetThumbnailRequest();
        request.Identifiers.AddRange(identifiers);

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(action, request), ct).ConfigureAwait(false);
        return Typed<GetThumbnailResponse>(answer);
    }

    // Port of typedResponse. The answer must have the type that the action returns.
    private static T Typed<T>(IncomingRpc answer) where T : class, IMessage
    {
        if (answer.DecryptedMessage is T typed)
        {
            return typed;
        }

        string actual = answer.DecryptedMessage?.GetType().Name ?? "null";
        throw new InvalidDataException($"unexpected response type {actual} for {answer.Raw.ResponseID}, expected {typeof(T).Name}");
    }
}
