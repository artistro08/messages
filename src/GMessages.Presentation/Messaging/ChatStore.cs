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
/// Lives for the whole app so a message that arrives with the main window closed can still notify. It keeps slim
/// <see cref="ConversationInfo"/> records, a ring of the last 256 message IDs, and the newest page of messages of the
/// last 20 opened conversations, so reopening one shows it at once (see <see cref="CachedMessages"/>).
/// </para>
/// <para>
/// <b>Privacy.</b> Under the memory caches sits the <see cref="MessageDatabase"/>: conversations and each one's newest
/// messages, encrypted with DPAPI for the current Windows user, so chats open at once after a restart.
/// <see cref="Reset"/> clears memory and deletes the database file, and a fetch that started before it writes nothing.
/// </para>
/// <para>
/// <b>Threads.</b> Live events come from <see cref="ConnectionController.ClientEvent"/> on the UI thread. The cache is
/// safe to read from anywhere. The database is never touched on the caller's thread: reads, writes and the delete go,
/// in order, through one background queue (a locked file can block a call for 30 s). Message text, names and numbers
/// are never logged.
/// </para>
/// </remarks>
public sealed partial class ChatStore : IDisposable
{
    /// <summary>How many conversations or messages one page asks for.</summary>
    public const int PageSize = 25;

    /// <summary>How many conversations keep their newest page of messages in memory.</summary>
    public const int CachedChatCount = 20;

    private const int NotifiedRingSize = 256;

    // A Message Older Than This Never Counts As New (A Reaction Or Status Update To An Old Message)
    private static readonly TimeSpan NewMessageAge = TimeSpan.FromDays(1);

    private readonly ConnectionController _connection;
    private readonly Func<IMessagingClient?> _client;
    private readonly ILogger _log;
    private readonly MessageDatabase? _database;
    private readonly TimeProvider _time;

    // Bumped By Reset: Work Started Under An Older Generation Belongs To The Old Account And Is Dropped
    private int _generation;

    // Held By Reset While It Bumps The Generation And Clears The Conversations, And By LoadSavedAsync While It Fills Them
    private readonly Lock _resetLock = new();

    // Database Calls, Run One After Another Off The Caller's Thread
    private readonly Lock _queueLock = new();
    private Task _queue = Task.CompletedTask;

    // Cache Of Slim Conversation Records, By ID
    private readonly ConcurrentDictionary<string, ConversationInfo> _cache = new();

    // Last 256 Raised Message IDs (Queue Keeps The Order, Set Makes The Lookup Fast)
    private readonly Lock _ringLock = new();
    private readonly Queue<string> _ring = new();
    private readonly HashSet<string> _ringSet = [];

    // Newest Page Of The Last 20 Opened Conversations, Oldest First; The Dictionary Order Is Least Recently Used First
    private readonly Lock _pagesLock = new();
    private readonly OrderedDictionary<string, List<ChatMessage>> _pages = [];

    private volatile ParticipantInfo? _me;
    private volatile IReadOnlyList<SIMCard> _sims = [];

    /// <summary>
    /// Creates the store and starts listening to live events.
    /// </summary>
    /// <param name="connection">The connection whose <see cref="ConnectionController.ClientEvent"/> feeds the store.</param>
    /// <param name="client">Gets the current phone client, or null when not connected.</param>
    /// <param name="log">The logger.</param>
    /// <param name="database">The one database for this data folder, or null to keep everything in memory only.</param>
    /// <param name="time">The clock that decides when a message is too old to be new; the system clock when null.</param>
    public ChatStore(ConnectionController connection, Func<IMessagingClient?> client, ILogger log, MessageDatabase? database = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(log);

        _connection = connection;
        _client     = client;
        _log        = log;
        _database   = database;
        _time       = time ?? TimeProvider.System;

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
    /// Gets every cached conversation, in no order: what the app already knows, so a list can show at once while it
    /// asks the phone for the newest page.
    /// </summary>
    /// <returns>A copy of the cached conversations.</returns>
    public IReadOnlyList<ConversationInfo> CachedConversations() => [.. _cache.Values];

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
        int generation        = Generation;
        Conversation? fetched = await RequireClient().GetConversationAsync(conversationId, ct);

        return fetched is null || generation != Generation ? null : CacheConversation(fetched, generation);
    }

    /// <summary>
    /// Fills the conversation cache from the database, so the list shows at once after a restart.
    /// </summary>
    /// <remarks>
    /// Call once start-up ends paired. Reads on the thread pool and raises no events; a conversation the phone
    /// already sent is kept over the saved copy.
    /// </remarks>
    /// <returns>A task that completes when the saved conversations are cached.</returns>
    public Task LoadSavedAsync()
    {
        // Taken Now, Not When The Queue Gets To It: A Reset Meanwhile Means These Rows Are The Old Account's
        int generation = Generation;

        return Enqueue(database =>
        {
            if (generation != Generation)
            {
                return;
            }

            IReadOnlyList<Conversation> saved = database.LoadConversations();

            // Under The Reset Lock, So Reset Can't Land Between The Check And The Rows
            lock (_resetLock)
            {
                if (generation != Generation)
                {
                    return;
                }

                foreach (Conversation c in saved)
                {
                    ConversationInfo info = ConversationInfo.From(c);
                    if (_cache.TryAdd(info.Id, info))
                    {
                        _me ??= info.Participants.FirstOrDefault(p => p.IsMe);
                    }
                }
            }
        });
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

        int generation                     = Generation;
        ListConversationsResponse response = await client.ListConversationsAsync(protocol_folder, PageSize, cursor, ct);

        // Unpaired Or Signed Out Meanwhile: The Old Account's Page Is Dropped
        if (generation != Generation)
        {
            return ([], null);
        }

        // No ConfigureAwait(false) Here: ConversationChanged Must Be Raised On The Caller Thread
        List<ConversationInfo> result = [];
        foreach (Conversation c in response.Conversations)
        {
            result.Add(CacheConversation(c, generation));
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

        int generation                = Generation;
        ListMessagesResponse response = await client.FetchMessagesAsync(conversationId, PageSize, cursor, ct).ConfigureAwait(false);

        // Unpaired Or Signed Out Meanwhile: The Old Account's Page Is Dropped
        if (generation != Generation)
        {
            return ([], null);
        }

        // The Phone Answers Newest First. An Older Page Can Hold The Cursor's Own Message Or Newer Ones, Already Shown Below,
        // So Those Are Dropped (As mautrix-gmessages Does: pkg/connector/backfill.go)
        List<Message> page = [.. response.Messages.Where(m => cursor is null || (m.MessageID != cursor.LastItemID && (cursor.LastItemTimestamp <= 0 || m.Timestamp / 1000 < cursor.LastItemTimestamp)))];
        List<ChatMessage> messages = [.. page.Select(ChatMessage.From).Reverse()];

        // Google Usually Sends No Cursor: The Next Older Page Then Starts At This Page's Oldest Message (Its Time In
        // Milliseconds), As mautrix-gmessages Does. An Empty Page Means There Are No Older Messages
        Cursor? older = response.Cursor ?? (page.Count > 0 ? new Cursor { LastItemID = page[^1].MessageID, LastItemTimestamp = page[^1].Timestamp / 1000 } : null);

        // The Newest Page Is What Reopening The Chat Shows; Every Page Goes To The Database
        if (cursor is null)
        {
            CachePage(conversationId, messages, onlyIfAbsent: false, generation);
        }

        SaveMessages(conversationId, page, generation);

        return (messages, older);
    }

    /// <summary>
    /// Gets the newest page of a conversation from memory, else from the database (then kept in memory).
    /// </summary>
    /// <remarks>
    /// A memory hit completes at once, without leaving the caller's thread; the database is read on the background queue.
    /// Counts as a use, so the conversation is the last one the memory cache drops.
    /// </remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <returns>A copy of the page, oldest first, or null when neither memory nor the database has it.</returns>
    public async Task<IReadOnlyList<ChatMessage>?> CachedMessagesAsync(string conversationId)
    {
        if (CachedMessages(conversationId) is { } cached)
        {
            return cached;
        }

        if (_database is null)
        {
            return null;
        }

        // On The Queue, So A Read Never Reopens (And Recreates) The File After Reset Queued Its Delete
        int generation               = Generation;
        IReadOnlyList<Message> saved = [];
        await Enqueue(database =>
        {
            if (generation == Generation)
            {
                saved = database.LoadMessages(conversationId, PageSize);
            }
        }).ConfigureAwait(false);

        if (saved.Count == 0 || generation != Generation)
        {
            return null;
        }

        // A Page The Phone Sent Meanwhile Wins Over The Saved One
        CachePage(conversationId, [.. saved.Select(ChatMessage.From)], onlyIfAbsent: true, generation);
        return CachedMessages(conversationId);
    }

    /// <summary>
    /// Gets the newest page of a conversation from memory only, kept from its last load and the messages since.
    /// </summary>
    /// <remarks>Counts as a use, so the conversation is the last one the cache drops.</remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <returns>A copy of the page, oldest first, or null when the conversation isn't cached.</returns>
    public IReadOnlyList<ChatMessage>? CachedMessages(string conversationId)
    {
        lock (_pagesLock)
        {
            if (!_pages.Remove(conversationId, out List<ChatMessage>? page))
            {
                return null;
            }

            _pages.Add(conversationId, page);
            return [.. page];
        }
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
        int generation                 = Generation;
        ConversationInfo? conversation = Find(conversationId);
        if (conversation is null)
        {
            Conversation? fetched = await client.GetConversationAsync(conversationId, ct);
            if (fetched is null || generation != Generation)
            {
                throw new MessageSendException("ConversationNotFound");
            }

            conversation = CacheConversation(fetched, generation);
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

        var pending = new ChatMessage(string.Empty, tmpId, conversationId, outgoing_id, DateTimeOffset.UtcNow, true, MessageKind.Text, text, MessageStatusType.OutgoingSending);
        if (generation == Generation)
        {
            CacheMessage(pending);
        }

        return pending;
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
        int generation = Generation;
        await RequireClient().MarkReadAsync(conversationId, messageId, ct);

        if (generation != Generation || Find(conversationId) is not { Unread: true } cached)
        {
            return;
        }

        ConversationInfo read = cached with { Unread = false };
        Upsert(read);
        ConversationChanged?.Invoke(this, read);
    }

    /// <summary>
    /// Forgets everything learned from the paired phone: cached conversations, the owner, the SIMs, the notified IDs
    /// and the database.
    /// </summary>
    /// <remarks>
    /// Call on unpair or sign-out. Conversation IDs are small per-phone numbers, so a different phone paired later
    /// would otherwise get the old phone's names, owner and SIMs; it also drops the old account's names and numbers
    /// from memory and disk. A fetch that started before the reset drops its result: no cache write, no database
    /// write, no event. The file is deleted on the background queue, after any write already queued.
    /// </remarks>
    public void Reset()
    {
        lock (_resetLock)
        {
            Interlocked.Increment(ref _generation);
            _cache.Clear();
            _me = null;
        }

        _ = Enqueue(database => database.Delete());
        _sims = [];

        lock (_ringLock)
        {
            _ring.Clear();
            _ringSet.Clear();
        }

        lock (_pagesLock)
        {
            _pages.Clear();
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
                SaveConversation(updated.Conversation, Generation);
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
        // Decide IsNew Before Any Await, So Two Quick Events Of One Message Can't Both Be New. A Message Older Than A Day
        // Is Never New Either: A Reaction Or Status Update To It Must Not Notify (mautrix-gmessages, GetReactions)
        int generation = Generation;
        bool is_new    = MarkNotified(message.Id) && message.Time >= _time.GetUtcNow() - NewMessageAge;
        CacheMessage(message);
        SaveMessages(message.ConversationId, [received.Message], generation);

        ConversationInfo? conversation = Find(message.ConversationId);
        if (conversation is null && _client() is { } client)
        {
            try
            {
                Conversation? fetched = await client.GetConversationAsync(message.ConversationId, CancellationToken.None);
                if (fetched is not null && generation == Generation)
                {
                    conversation = CacheConversation(fetched, generation);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogConversationFetchFailed(message.ConversationId, ex.GetType().Name);
            }
        }

        // Unpaired Or Signed Out Meanwhile: Nothing Of The Old Account Is Raised
        if (generation != Generation)
        {
            return;
        }

        MessageArrived?.Invoke(this, new MessageArrivedEventArgs(message, conversation, received.IsOld, is_new));
    }

    // =========================================================================
    // CACHE
    // =========================================================================

    // Caches And Saves A Protocol Conversation, Raising ConversationChanged When The Cache Changed
    private ConversationInfo CacheConversation(Conversation c, int generation)
    {
        ConversationInfo info = ConversationInfo.From(c);
        SaveConversation(c, generation);
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

    // Keeps A Conversation's Newest Page As The Most Recently Used, Dropping The Least Recently Used Past 20. Messages
    // Cached While The Page Was On Its Way (Newer Than Anything In It, And Not In It) Stay Below It. The Generation Is
    // Checked Under The Lock, So A Page Can't Land After Reset Cleared The Pages
    private void CachePage(string conversationId, List<ChatMessage> page, bool onlyIfAbsent, int generation)
    {
        lock (_pagesLock)
        {
            if (generation != Generation || (onlyIfAbsent && _pages.ContainsKey(conversationId)))
            {
                return;
            }

            List<ChatMessage> merged = [.. page];
            if (_pages.Remove(conversationId, out List<ChatMessage>? old) && page.Count > 0)
            {
                DateTimeOffset newest = page.Max(m => m.Time);
                merged.AddRange(old.Where(m => m.Time > newest && !page.Any(p => SameMessage(p, m))));
                if (merged.Count > PageSize)
                {
                    merged.RemoveRange(0, merged.Count - PageSize);
                }
            }

            _pages.Add(conversationId, merged);
            if (_pages.Count > CachedChatCount)
            {
                _pages.RemoveAt(0);
            }
        }
    }

    // Puts An Arrived Or Sent Message Into Its Cached Page (Only Conversations Opened Before Have One): It Replaces The
    // Copy With Its ID, Or Its Pending Copy By Temporary ID, And Goes In By Time. The Page Keeps Its Newest 25
    private void CacheMessage(ChatMessage message)
    {
        lock (_pagesLock)
        {
            if (!_pages.TryGetValue(message.ConversationId, out List<ChatMessage>? page))
            {
                return;
            }

            // A Pending Copy Never Replaces The Delivered One: The Echo Can Arrive Before The Send Call Returns
            if (message.Id.Length == 0 && page.Any(m => m.Id.Length > 0 && SameMessage(m, message)))
            {
                return;
            }

            page.RemoveAll(m => SameMessage(m, message));

            int at = page.FindLastIndex(m => m.Time <= message.Time) + 1;
            page.Insert(at, message);
            if (page.Count > PageSize)
            {
                page.RemoveAt(0);
            }
        }
    }

    // The Same Message: The Same ID, Or The Same Temporary ID (A Pending Copy And Its Echo)
    private static bool SameMessage(ChatMessage a, ChatMessage b) =>
        (a.Id.Length > 0 && a.Id == b.Id) || (a.TmpId.Length > 0 && a.TmpId == b.TmpId);

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

    // =========================================================================
    // DATABASE
    // =========================================================================

    private int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Waits for every database call queued so far (tests).
    /// </summary>
    /// <returns>A task that completes when the queue is empty up to now.</returns>
    internal Task FlushAsync()
    {
        lock (_queueLock)
        {
            return _queue;
        }
    }

    // Runs A Database Call On The Background Queue, After Every One Queued Before It. Without A Database Nothing Runs
    private Task Enqueue(Action<MessageDatabase> work)
    {
        if (_database is not { } database)
        {
            return Task.CompletedTask;
        }

        lock (_queueLock)
        {
            _queue = _queue.ContinueWith(
                _ =>
                {
                    try
                    {
                        work(database);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        LogActionFailed("use the message database", ex.GetType().Name);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

            return _queue;
        }
    }

    // Queues A Conversation Save, Dropped When A Reset Came After The Generation Was Taken
    private void SaveConversation(Conversation conversation, int generation)
    {
        if (_database is null || conversation.ConversationID.Length == 0)
        {
            return;
        }

        _ = Enqueue(database =>
        {
            if (generation == Generation)
            {
                database.SaveConversation(conversation);
            }
        });
    }

    // Queues A Message Save, Dropped When A Reset Came After The Generation Was Taken
    private void SaveMessages(string conversationId, IReadOnlyList<Message> messages, int generation)
    {
        List<Message> saved = [.. messages.Where(m => m.MessageID.Length > 0)];
        if (_database is null || conversationId.Length == 0 || saved.Count == 0)
        {
            return;
        }

        _ = Enqueue(database =>
        {
            if (generation == Generation)
            {
                database.SaveMessages(conversationId, saved);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Phone refused a send in {ConversationId} ({Status})")]
    private partial void LogSendRefused(string conversationId, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not fetch conversation {ConversationId} ({ErrorType})")]
    private partial void LogConversationFetchFailed(string conversationId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not {Action} ({ErrorType})")]
    private partial void LogActionFailed(string action, string errorType);
}
