using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using Google.Protobuf;

namespace GMessages.Protocol;

/// <summary>
/// The requests the app sends to the phone: conversations, messages, contacts and thumbnails.
/// </summary>
/// <remarks>
/// Ported from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">methods.go</see>.
/// Each method sends one request and waits for the phone's answer, which arrives over the long poll, so the client
/// must be connected. Every method that expects a typed answer throws <see cref="InvalidDataException"/> when the
/// answer is another type (Go's <c>typedResponse</c> in session_handler.go). Message contents are never logged;
/// <see cref="RpcSession"/> logs only request IDs and actions. Only the requests the app uses are ported.
/// </remarks>
public sealed partial class GmClient
{
    // 1 After The First ListConversations (Go: conversationsFetchedOnce)
    private int _conversationsFetchedOnce;

    // =========================================================================
    // CONVERSATIONS
    // =========================================================================

    /// <summary>
    /// Lists the conversations of a folder, newest first.
    /// </summary>
    /// <remarks>
    /// Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">ListConversations</see>.
    /// The first call on a client is sent as a <see cref="MessageType.BugleAnnotation"/> message and later calls as
    /// <see cref="MessageType.BugleMessage"/>, as Go does. Pass the cursor of the previous answer to get the next page.
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

        // First Call Is An Annotation, As In Go
        MessageType message_type = Interlocked.Exchange(ref _conversationsFetchedOnce, 1) == 0 ? MessageType.BugleAnnotation : MessageType.BugleMessage;

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(ActionType.ListConversations, request, MessageType: message_type), ct).ConfigureAwait(false);
        return Typed<ListConversationsResponse>(answer);
    }

    /// <summary>
    /// Gets one conversation.
    /// </summary>
    /// <remarks>Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">GetConversation</see>.</remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The conversation; null when the phone's answer has none (Go returns nil there).</returns>
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

    // =========================================================================
    // MESSAGES
    // =========================================================================

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
    /// Sent as user-initiated (Go's <c>sendUserMessage</c>): if the phone has not answered within 5 seconds,
    /// <see cref="RpcSession.PhoneSlow"/> is raised once and waiting continues. Use <see cref="NewTmpId"/> for the
    /// request's temporary IDs.
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
    /// Sent as user-initiated, like <see cref="SendMessageAsync"/>. Use <see cref="EmojiTypes.MakeReactionData"/> for
    /// the request's reaction data. The phone then sends the message again with its reactions updated.
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
    /// Always sends typing as true and waits for no answer, as Go does; the phone stops showing it on its own.
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
    /// Waits for the phone's answer and ignores its content, as Go does.
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
    /// Makes a temporary ID for a message that is about to be sent.
    /// </summary>
    /// <remarks>Ported from <c>GenerateTmpID</c> in <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/util/func.go">util/func.go</see>: a random GUID, like the official app's.</remarks>
    /// <returns>A new GUID as text.</returns>
    public static string NewTmpId() => Guid.NewGuid().ToString();

    // =========================================================================
    // CONTACTS AND THUMBNAILS
    // =========================================================================

    /// <summary>
    /// Lists the phone's contacts.
    /// </summary>
    /// <remarks>Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">ListContacts</see>; the request always carries the values 1, 350 and 50, as in Go.</remarks>
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
    /// <remarks>Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">ListTopContacts</see>; always asks for 8, as in Go.</remarks>
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

    // =========================================================================
    // HELPERS
    // =========================================================================

    private async Task<GetThumbnailResponse> GetThumbnailAsync(ActionType action, IReadOnlyList<string> identifiers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifiers);

        var request = new GetThumbnailRequest();
        request.Identifiers.AddRange(identifiers);

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(action, request), ct).ConfigureAwait(false);
        return Typed<GetThumbnailResponse>(answer);
    }

    // Port Of typedResponse: The Answer Must Be The Type The Action Is Documented To Return
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
