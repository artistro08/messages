using System.Collections.Concurrent;
using GMessages.Presentation.Connection;
using GMessages.Protocol.Events;
using GMessages.Protocol.Proto;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Loads conversations and messages, sends texts, and turns the phone's live events into chat events.
/// </summary>
/// <remarks>
/// <para>
/// Lives for the whole app so a message that arrives with the main window closed can still notify. It keeps only slim
/// <see cref="ConversationInfo"/> records and a ring of the last 256 message IDs; messages themselves are never kept.
/// </para>
/// <para>
/// <b>Threads.</b> Live events come from <see cref="ConnectionController.ClientEvent"/> on the UI thread. The cache is
/// safe to read from anywhere. Message text, names and numbers are never logged.
/// </para>
/// </remarks>
public sealed partial class ChatStore : IDisposable
{
    /// <summary>How many conversations or messages one page asks for.</summary>
    public const int PageSize = 25;

    private const int NotifiedRingSize = 256;

    private readonly ConnectionController _connection;
    private readonly Func<IMessagingClient?> _client;
    private readonly ILogger _log;

    // Cache Of Slim Conversation Records, By ID
    private readonly ConcurrentDictionary<string, ConversationInfo> _cache = new();

    // Last 256 Raised Message IDs (Queue Keeps The Order, Set Makes The Lookup Fast)
    private readonly Lock _ringLock = new();
    private readonly Queue<string> _ring = new();
    private readonly HashSet<string> _ringSet = [];

    private volatile ParticipantInfo? _me;
    private volatile IReadOnlyList<SIMCard> _sims = [];

    /// <summary>
    /// Creates the store and starts listening to live events.
    /// </summary>
    /// <param name="connection">The connection whose <see cref="ConnectionController.ClientEvent"/> feeds the store.</param>
    /// <param name="client">Gets the current phone client, or null when not connected.</param>
    /// <param name="log">The logger.</param>
    public ChatStore(ConnectionController connection, Func<IMessagingClient?> client, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(log);

        _connection = connection;
        _client     = client;
        _log        = log;

        _connection.ClientEvent += OnClientEvent;
    }

    /// <summary>Raised when a conversation's details changed (live update, or a fetch that changed the cache).</summary>
    public event EventHandler<ConversationInfo>? ConversationChanged;

    /// <summary>Raised when a message arrives or changes status.</summary>
    public event EventHandler<MessageArrivedEventArgs>? MessageArrived;

    /// <summary>Raised when the live connection recovered, so open views reload.</summary>
    public event EventHandler? Resynced;

    /// <summary>Gets the first participant marked as the phone's owner in any cached conversation.</summary>
    public ParticipantInfo? Me => _me;

    /// <summary>
    /// Gets the phone client.
    /// </summary>
    /// <returns>The client.</returns>
    /// <exception cref="MessagingUnavailableException">Not connected.</exception>
    public IMessagingClient RequireClient() => _client() ?? throw new MessagingUnavailableException();

    /// <summary>
    /// Finds a cached conversation.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <returns>The conversation, or null when not cached.</returns>
    public ConversationInfo? Find(string conversationId) => _cache.GetValueOrDefault(conversationId);

    /// <summary>
    /// Gets a conversation from the cache, fetching it from the phone when it is not cached.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The conversation, or null when the phone does not know it.</returns>
    /// <exception cref="MessagingUnavailableException">Not cached and not connected.</exception>
    public async Task<ConversationInfo?> GetAsync(string conversationId, CancellationToken ct)
    {
        if (Find(conversationId) is { } cached)
        {
            return cached;
        }

        // No ConfigureAwait(false): ConversationChanged Must Be Raised On The Caller Thread
        Conversation? fetched = await RequireClient().GetConversationAsync(conversationId, ct);

        return fetched is null ? null : CacheConversation(fetched);
    }

    /// <summary>
    /// Loads one page of a folder and caches it.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="cursor">Where the previous page ended, or null for the newest page.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>Up to 25 conversations, newest first, and the cursor for the next page (null when there is none).</returns>
    /// <exception cref="MessagingUnavailableException">Not connected.</exception>
    public async Task<(IReadOnlyList<ConversationInfo> Conversations, Cursor? Next)> LoadFolderAsync(ConversationFolder folder, Cursor? cursor, CancellationToken ct)
    {
        IMessagingClient client = RequireClient();

        ListConversationsRequest.Types.Folder protocol_folder = folder == ConversationFolder.SpamBlocked
            ? ListConversationsRequest.Types.Folder.SpamBlocked
            : ListConversationsRequest.Types.Folder.Inbox;

        ListConversationsResponse response = await client.ListConversationsAsync(protocol_folder, PageSize, cursor, ct);

        // No ConfigureAwait(false) Here: ConversationChanged Must Be Raised On The Caller Thread
        List<ConversationInfo> result = [];
        foreach (Conversation c in response.Conversations)
        {
            result.Add(CacheConversation(c));
        }

        return (result, response.Cursor);
    }

    /// <summary>
    /// Loads one page of messages.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="cursor">Where the previous page ended, or null for the newest page.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>Up to 25 messages, oldest first, and the cursor for the older page (null when there is none).</returns>
    /// <exception cref="MessagingUnavailableException">Not connected.</exception>
    public async Task<(IReadOnlyList<ChatMessage> Messages, Cursor? Older)> LoadMessagesAsync(string conversationId, Cursor? cursor, CancellationToken ct)
    {
        IMessagingClient client = RequireClient();

        ListMessagesResponse response = await client.FetchMessagesAsync(conversationId, PageSize, cursor, ct).ConfigureAwait(false);

        // The Phone Answers Newest First
        List<ChatMessage> messages = [.. response.Messages.Select(ChatMessage.From).Reverse()];

        return (messages, response.Cursor);
    }

    /// <summary>
    /// Sends a text message the way the bridge does.
    /// </summary>
    /// <remarks>
    /// Ported from <c>ConvertMatrixMessage</c> in the Go connector: the same temporary ID goes in the request and the
    /// payload, the message is sent as the conversation's default outgoing participant, and the SIM is the card
    /// belonging to that participant (from the last settings event). Phone-not-responding and connection-closed
    /// exceptions pass through for the caller to handle.
    /// </remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="text">The text.</param>
    /// <param name="tmpId">The temporary ID (<c>GmClient.NewTmpId()</c>), so the echo can be matched to the pending bubble.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The pending message as sent.</returns>
    /// <exception cref="MessagingUnavailableException">Not connected.</exception>
    /// <exception cref="MessageSendException">The phone answered with a status other than success.</exception>
    public async Task<ChatMessage> SendTextAsync(string conversationId, string text, string tmpId, CancellationToken ct)
    {
        IMessagingClient client = RequireClient();

        // Look Up The Conversation, Fetching It When Not Cached
        ConversationInfo? conversation = Find(conversationId);
        if (conversation is null)
        {
            Conversation? fetched = await client.GetConversationAsync(conversationId, ct);
            if (fetched is null)
            {
                throw new MessageSendException("ConversationNotFound");
            }

            conversation = CacheConversation(fetched);
        }

        string outgoing_id = conversation.DefaultOutgoingId;
        SIMCard? sim       = _sims.FirstOrDefault(s => s.SIMParticipant?.ID == outgoing_id);

        var request = new SendMessageRequest
        {
            ConversationID = conversationId,
            MessagePayload = new MessagePayload
            {
                TmpID          = tmpId,
                ConversationID = conversationId,
                ParticipantID  = outgoing_id,
                TmpID2         = tmpId,
            },
            SIMPayload = sim?.SIMData?.SIMPayload,
            TmpID      = tmpId,
            ForceRCS   = false,
        };
        request.MessagePayload.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = text } });

        SendMessageResponse response = await client.SendMessageAsync(request, ct).ConfigureAwait(false);
        if (response.Status != SendMessageResponse.Types.Status.Success)
        {
            LogSendRefused(conversationId, response.Status.ToString());
            throw new MessageSendException(response.Status.ToString());
        }

        return new ChatMessage(string.Empty, tmpId, conversationId, outgoing_id, DateTimeOffset.UtcNow, true, MessageKind.Text, text, MessageStatusType.OutgoingSending);
    }

    /// <summary>
    /// Tells the phone a conversation was read up to a message, then marks the cached conversation read.
    /// </summary>
    /// <remarks>
    /// The cached copy changes as soon as the phone answers, raising <see cref="ConversationChanged"/>, so the list
    /// stops showing it unread without waiting for the phone to send the conversation back.
    /// </remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="messageId">The newest message that was read.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task that completes when the phone answered.</returns>
    /// <exception cref="MessagingUnavailableException">Not connected.</exception>
    public async Task MarkReadAsync(string conversationId, string messageId, CancellationToken ct)
    {
        // No ConfigureAwait(false): ConversationChanged Must Be Raised On The Caller Thread
        await RequireClient().MarkReadAsync(conversationId, messageId, ct);

        if (Find(conversationId) is not { Unread: true } cached)
        {
            return;
        }

        ConversationInfo read = cached with { Unread = false };
        Upsert(read);
        ConversationChanged?.Invoke(this, read);
    }

    /// <summary>
    /// Forgets everything learned from the paired phone: cached conversations, the owner, the SIMs and the notified IDs.
    /// </summary>
    /// <remarks>
    /// Call on unpair or sign-out. Conversation IDs are small per-phone numbers, so a different phone paired later
    /// would otherwise get the old phone's names, owner and SIMs; it also drops the old account's names and numbers
    /// from memory.
    /// </remarks>
    public void Reset()
    {
        _cache.Clear();
        _me   = null;
        _sims = [];

        lock (_ringLock)
        {
            _ring.Clear();
            _ringSet.Clear();
        }
    }

    /// <summary>
    /// Logs that a view's request failed, with the error type only (never the message, which can hold content).
    /// </summary>
    /// <param name="action">What failed, such as "send".</param>
    /// <param name="ex">The error.</param>
    public void LogFailure(string action, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        LogActionFailed(action, ex.GetType().Name);
    }

    /// <inheritdoc />
    public void Dispose() => _connection.ClientEvent -= OnClientEvent;

    // =========================================================================
    // LIVE EVENTS
    // =========================================================================

    // Fire And Forget; HandleEventAsync Logs Its Own Failures
    private void OnClientEvent(object? sender, object e) => _ = HandleEventAsync(e);

    /// <summary>
    /// Maps one protocol event to the store's state and events (the constructor subscribes the real source; tests call this directly).
    /// </summary>
    /// <param name="e">A record from <c>GMessages.Protocol.Events</c>.</param>
    /// <returns>A task that completes when the event was handled.</returns>
    internal async Task HandleEventAsync(object e)
    {
        switch (e)
        {
            case ConversationUpdated updated:
                ConversationInfo info = ConversationInfo.From(updated.Conversation);
                Upsert(info);
                ConversationChanged?.Invoke(this, info);
                break;

            case MessageReceived received:
                await HandleMessageAsync(received);
                break;

            case SettingsChanged settings:
                _sims = [.. settings.Settings.SIMCards];
                break;

            case ListenRecovered:
                Resynced?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    // Raises MessageArrived With The Conversation From The Cache, Fetching It On A Miss
    private async Task HandleMessageAsync(MessageReceived received)
    {
        ChatMessage message = ChatMessage.From(received.Message);

        // No ConfigureAwait(false) Below: Events Must Be Raised On The Thread That Delivered The Client Event
        // Decide IsNew Before Any Await, So Two Quick Events Of One Message Can't Both Be New
        bool is_new = MarkNotified(message.Id);

        ConversationInfo? conversation = Find(message.ConversationId);
        if (conversation is null && _client() is { } client)
        {
            try
            {
                Conversation? fetched = await client.GetConversationAsync(message.ConversationId, CancellationToken.None);
                if (fetched is not null)
                {
                    conversation = CacheConversation(fetched);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogConversationFetchFailed(message.ConversationId, ex.GetType().Name);
            }
        }

        MessageArrived?.Invoke(this, new MessageArrivedEventArgs(message, conversation, received.IsOld, is_new));
    }

    // =========================================================================
    // CACHE
    // =========================================================================

    // Caches A Protocol Conversation And Raises ConversationChanged When The Cache Changed
    private ConversationInfo CacheConversation(Conversation c)
    {
        ConversationInfo info = ConversationInfo.From(c);
        if (Upsert(info))
        {
            ConversationChanged?.Invoke(this, info);
        }

        return info;
    }

    // Stores A Conversation; True When It Differs From What Was Cached (Participants Compared By Value)
    private bool Upsert(ConversationInfo info)
    {
        _me ??= info.Participants.FirstOrDefault(p => p.IsMe);

        ConversationInfo? old = _cache.GetValueOrDefault(info.Id);
        _cache[info.Id]       = info;

        return old is null
            || old with { Participants = info.Participants } != info
            || !old.Participants.SequenceEqual(info.Participants);
    }

    // Remembers A Message ID; True The First Time It Is Seen
    private bool MarkNotified(string messageId)
    {
        lock (_ringLock)
        {
            if (!_ringSet.Add(messageId))
            {
                return false;
            }

            _ring.Enqueue(messageId);
            if (_ring.Count > NotifiedRingSize)
            {
                _ringSet.Remove(_ring.Dequeue());
            }

            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Phone refused a send in {ConversationId} ({Status})")]
    private partial void LogSendRefused(string conversationId, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not fetch conversation {ConversationId} ({ErrorType})")]
    private partial void LogConversationFetchFailed(string conversationId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not {Action} ({ErrorType})")]
    private partial void LogActionFailed(string action, string errorType);
}
