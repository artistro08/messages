using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using GMessages.Presentation.Connection;
using GMessages.Protocol;
using GMessages.Protocol.Proto;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Loads conversations and messages, sends messages, and turns the live events of the phone into chat events.
/// </summary>
/// <remarks>
/// <para>
/// The store lives for the whole app, so a message can notify while the main window is closed. It keeps slim
/// <see cref="ConversationInfo"/> records and a ring of the last 256 notified message IDs. It also keeps the newest
/// page of the last 20 opened conversations. Thus a chat shows at once when it opens again.
/// </para>
/// <para>
/// <b>Privacy.</b> Below the memory caches is the <see cref="MessageDatabase"/>. It keeps the conversations and the
/// newest messages of each one, encrypted with DPAPI for the current Windows user. <see cref="Reset"/> clears memory
/// and deletes the database file. A fetch that started before a reset writes nothing.
/// </para>
/// <para>
/// <b>Threads.</b> Live events come from <see cref="ConnectionController.ClientEvent"/> on the UI thread. Any thread
/// can read the cache. The store never uses the database on the thread of the caller. Reads, writes and the delete go
/// in order through a single background queue. A locked file can block a call for 30 s. The store never logs message
/// text, names or numbers.
/// </para>
/// </remarks>
public sealed partial class ChatStore : IDisposable
{
    /// <summary>The number of conversations or messages that 1 page asks for.</summary>
    public const int PageSize = 25;

    /// <summary>The number of conversations that keep their newest page of messages in memory.</summary>
    public const int CachedChatCount = 20;

    /// <summary>The largest attachment that the store sends, in bytes: 100 MiB, as LibGM <c>MaxFileSize</c>.</summary>
    public const long MaxAttachmentBytes = 100L * 1024 * 1024;

    private const int NotifiedRingSize = 256;

    // A message older than this is never new. A reaction or a status update to an old message must not notify.
    private static readonly TimeSpan NewMessageAge = TimeSpan.FromDays(1);

    private readonly ConnectionController _connection;
    private readonly Func<IMessagingClient?> _client;
    private readonly ILogger _log;
    private readonly MessageDatabase? _database;
    private readonly TimeProvider _time;
    private readonly MediaStore? _media;

    // Reset increments the generation. Work that started under an older generation belongs to the old account, and
    // the store drops it.
    private int _generation;

    // Reset holds this lock while it increments the generation and clears the conversations. LoadSavedAsync and
    // NoteEncryption hold it while they fill them.
    private readonly Lock _resetLock = new();

    private readonly Lock _queueLock = new();
    private Task _queue = Task.CompletedTask;

    private readonly ConcurrentDictionary<string, ConversationInfo> _cache = new();

    private readonly Lock _ringLock = new();
    private readonly Queue<string> _ring = new();
    private readonly HashSet<string> _ringSet = [];

    // The newest page of each of the last 20 opened conversations, oldest message first. The dictionary order is
    // least recently used first.
    private readonly Lock _pagesLock = new();
    private readonly OrderedDictionary<string, List<ChatMessage>> _pages = [];

    // The IDs of the messages in the pages that DropPagesAsync dropped, so IsCached still blocks a second toast for
    // them. _pagesLock guards this set.
    private readonly HashSet<string> _droppedIds = [];

    // The newest live media message of each conversation and its label. A conversation update has no snippet text
    // for media. This label names the media in the list, with or without a cached page.
    private readonly ConcurrentDictionary<string, (string MessageId, string Label)> _liveMedia = new();

    // The text of each new received message since the conversation was last read. The phone sends no unread count.
    // The store drops the list when the conversation turns read.
    private readonly ConcurrentDictionary<string, ImmutableList<string>> _unreadTexts = new();

    // When each conversation last changed live, as a Stopwatch timestamp. A list page that the store asked for before
    // that time keeps the live copy.
    private readonly ConcurrentDictionary<string, long> _liveAt = new();

    // The unsent draft of each conversation. The database keeps the text, so the draft outlives the window and the
    // app. The files stay in memory only.
    private readonly ConcurrentDictionary<string, string> _drafts                               = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<PendingAttachment>> _draftFiles = new(StringComparer.Ordinal);

    // The newest encryption system line of each conversation, with its time in microseconds.
    private readonly ConcurrentDictionary<string, (bool Encrypted, long At)> _encryption = new();

    private volatile ParticipantInfo? _me;
    private volatile IReadOnlyList<SIMCard> _sims = [];

    // A live MOBILE_BATTERY_LOW sets this flag. MOBILE_BATTERY_RESTORED or Reset clears it.
    private bool _batteryLow;

    /// <summary>
    /// Creates the store and subscribes to the live events of the connection.
    /// </summary>
    /// <param name="connection">
    /// The connection whose <see cref="ConnectionController.ClientEvent"/> supplies the live events.
    /// </param>
    /// <param name="client">Gets the current phone client, or null when the app is not connected.</param>
    /// <param name="log">The logger.</param>
    /// <param name="database">The database for this data folder, or null to keep all data in memory only.</param>
    /// <param name="time">The clock for the 1 day age limit of a new message, or null for the system clock.</param>
    /// <param name="media">The decrypted media cache that <see cref="Reset"/> clears, or null for none.</param>
    public ChatStore(ConnectionController connection, Func<IMessagingClient?> client, ILogger log, MessageDatabase? database = null, TimeProvider? time = null, MediaStore? media = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(log);

        _connection = connection;
        _client     = client;
        _log        = log;
        _database   = database;
        _time       = time ?? TimeProvider.System;
        _media      = media;

        _connection.ClientEvent += OnClientEvent;
    }

    /// <summary>Occurs when the details of a conversation change, from a live update or from a fetch.</summary>
    public event EventHandler<ConversationInfo>? ConversationChanged;

    /// <summary>Occurs when a message arrives or changes status.</summary>
    public event EventHandler<MessageArrivedEventArgs>? MessageArrived;

    /// <summary>Occurs when the live connection recovers, so open views can load again.</summary>
    public event EventHandler? Resynced;

    /// <summary>Occurs when a person in a cached conversation starts or stops typing.</summary>
    public event EventHandler<TypingEventArgs>? TypingChanged;

    /// <summary>Occurs when <see cref="IsPhoneBatteryLow"/> changes.</summary>
    public event EventHandler? PhoneBatteryChanged;

    /// <summary>Occurs on each live low battery alert, also on a repeat, so a closed banner can show again.</summary>
    public event EventHandler? PhoneBatteryLowAlerted;

    /// <summary>
    /// Gets whether the phone reported a low battery and did not report a recovery yet.
    /// </summary>
    /// <remarks>
    /// The level is not known at start, so this is false until a live alert arrives. The protocol drops catch-up
    /// alerts. The store keeps the state, so it survives when the window closes. <see cref="Reset"/> clears it.
    /// </remarks>
    public bool IsPhoneBatteryLow => Volatile.Read(ref _batteryLow);

    /// <summary>Gets the decrypted media cache, or null when the store has none.</summary>
    public MediaStore? Media => _media;

    /// <summary>
    /// Gets the first participant marked as the owner in any cached conversation. A phone with 2 SIMs has 2 such IDs.
    /// The App reads this one only for the profile photo.
    /// </summary>
    public ParticipantInfo? Me => _me;

    /// <summary>
    /// Gets the phone client.
    /// </summary>
    /// <exception cref="MessagingUnavailableException">The app is not connected to the phone.</exception>
    public IMessagingClient RequireClient() => _client() ?? throw new MessagingUnavailableException();

    /// <summary>
    /// Finds a cached conversation.
    /// </summary>
    /// <returns>The conversation, or null when it is not cached.</returns>
    public ConversationInfo? Find(string conversationId) => _cache.GetValueOrDefault(conversationId);

    /// <summary>
    /// Gets all cached conversations in no order. A list can show them at once while it asks the phone for the newest
    /// page.
    /// </summary>
    /// <returns>A copy of the cached conversations.</returns>
    public IReadOnlyList<ConversationInfo> CachedConversations() => [.. _cache.Values];

    /// <summary>
    /// Gets the text of each new message that a conversation received live since it was last read, oldest first.
    /// </summary>
    /// <remarks>
    /// The phone only says if a conversation is unread, so the store keeps each received message as it arrives. Each
    /// entry is its <see cref="ChatFormat.UnreadLine"/>: the first name of the sender in a group, or the media label.
    /// The list starts again when the phone or this app marks the conversation read. The count of the list is the
    /// unread count. The store does not keep messages that came while the app was not running. A caller shows an
    /// unread conversation with no texts as 1 unread message.
    /// </remarks>
    /// <returns>The texts, or empty when the store kept none.</returns>
    public IReadOnlyList<string> LiveUnreadTexts(string conversationId) =>
        _unreadTexts.GetValueOrDefault(conversationId) ?? [];

    /// <summary>
    /// Gets the unsent draft of a conversation.
    /// </summary>
    /// <returns>The draft, or empty when there is none.</returns>
    public string Draft(string conversationId) => _drafts.GetValueOrDefault(conversationId) ?? string.Empty;

    /// <summary>
    /// Gets the files that the user picked or pasted to send with the unsent draft of a conversation.
    /// </summary>
    /// <returns>The files, or empty when there are none.</returns>
    public IReadOnlyList<PendingAttachment> DraftFiles(string conversationId) => _draftFiles.GetValueOrDefault(conversationId) ?? [];

    /// <summary>
    /// Occurs on the thread of the caller when the draft text or files of a conversation change. The list row can then
    /// show the draft. The event argument is the conversation ID.
    /// </summary>
    public event EventHandler<string>? DraftChanged;

    /// <summary>
    /// Keeps the unsent draft of a conversation and saves its text. The draft is then still there after the window or
    /// the app closes.
    /// </summary>
    /// <remarks>
    /// An empty text forgets the draft. The files stay in memory only while the app runs. A pasted picture has no file
    /// to open after a restart. The save queue writes the text in order, encrypted like every other body.
    /// A sign-out deletes the text with the database.
    /// ponytail: a quit right after a catch-up can cut off the draft save with hundreds of other queued saves. Give
    /// drafts their own locked write path if that becomes a problem.
    /// </remarks>
    /// <param name="text">The draft text.</param>
    /// <param name="files">The files to send with the draft, or null for none.</param>
    public void SaveDraft(string conversationId, string text, IReadOnlyList<PendingAttachment>? files = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(text);

        files ??= [];
        bool filesChanged = !DraftFiles(conversationId).SequenceEqual(files);
        if (files.Count > 0)
        {
            _draftFiles[conversationId] = [.. files];
        }
        else
        {
            _draftFiles.TryRemove(conversationId, out _);
        }

        // A cleared draft stays as an empty entry, so a saved draft that LoadSavedAsync loads late cannot bring it
        // back.
        bool textChanged = !_drafts.TryGetValue(conversationId, out string? old) || old != text;
        _drafts[conversationId] = text;
        if (textChanged || filesChanged)
        {
            DraftChanged?.Invoke(this, conversationId);
        }

        if (!textChanged)
        {
            return;
        }

        // The save queue runs this write after the delete of a sign-out. Thus a draft never gets into the file of the
        // next account.
        SaveIfCurrent(conversationId, Generation, database => database.SaveDraft(conversationId, text));
    }

    /// <summary>
    /// Gets whether a conversation is end-to-end encrypted.
    /// </summary>
    /// <remarks>
    /// The store follows the newest encryption system line in any page or live message, see
    /// <see cref="ChatFormat.EncryptionOf"/>. The database keeps that state, so the lock shows after a restart.
    /// </remarks>
    /// <returns>
    /// The <see cref="ConversationInfo.IsEncrypted"/> flag of the conversation when the phone sent it. That flag is
    /// current, and the phone sends it on every chat. Otherwise, true when the newest system line says encrypted.
    /// False when that line says not encrypted, or when the store saw no line.
    /// </returns>
    public bool IsEncrypted(string conversationId) =>
        Find(conversationId)?.IsEncrypted ?? (_encryption.TryGetValue(conversationId, out (bool Encrypted, long At) state) && state.Encrypted);

    /// <summary>
    /// Gets a conversation from the cache, or fetches it from the phone when it is not cached.
    /// </summary>
    /// <returns>The conversation, or null when the phone does not know it.</returns>
    /// <exception cref="MessagingUnavailableException">
    /// The conversation is not cached and the app is not connected to the phone.
    /// </exception>
    public async Task<ConversationInfo?> GetAsync(string conversationId, CancellationToken ct)
    {
        if (Find(conversationId) is { } cached)
        {
            return cached;
        }

        // No ConfigureAwait(false): the store must raise ConversationChanged on the thread of the caller.
        int generation        = Generation;
        Conversation? fetched = await RequireClient().GetConversationAsync(conversationId, ct);

        return fetched is null || generation != Generation ? null : CacheConversation(fetched, generation);
    }

    /// <summary>
    /// Fills the conversation cache from the database, so the list shows at once after a restart.
    /// </summary>
    /// <remarks>
    /// Call it once when start-up ends paired. It reads on the thread pool and raises no events. A conversation that
    /// the phone already sent stays over the saved copy. It also loads the saved encryption state and draft of each
    /// conversation, see <see cref="IsEncrypted"/>.
    /// </remarks>
    /// <returns>A task that completes when the saved conversations are cached.</returns>
    public Task LoadSavedAsync()
    {
        // The generation comes from this call, not from the queued work. After a reset, the saved rows belong to the
        // old account.
        int generation = Generation;

        return EnqueueAsync(database =>
        {
            if (generation != Generation)
            {
                return;
            }

            IReadOnlyList<Conversation> saved                                 = database.LoadConversations();
            IReadOnlyDictionary<string, (bool Encrypted, long At)> encryption = database.LoadEncryption();
            IReadOnlyDictionary<string, string> drafts                        = database.LoadDrafts();

            // A conversation with no snippet text names the media of its saved newest message. The reads run before
            // the reset lock, so a Reset on the UI thread never waits for them.
            List<ConversationInfo> infos = [];
            foreach (Conversation c in saved)
            {
                ConversationInfo info = ConversationInfo.From(c);
                if (info.Snippet.Length == 0 && info.LatestMessageId.Length > 0 && database.LoadMessage(info.Id, info.LatestMessageId) is { } latest
                    && ChatMessage.From(latest).Media is { } media)
                {
                    info = info with { SnippetMedia = ChatFormat.MediaLabel(media) };
                }

                infos.Add(info);
            }

            // The reset lock keeps a Reset out between the check and the rows.
            lock (_resetLock)
            {
                if (generation != Generation)
                {
                    return;
                }

                foreach (ConversationInfo info in infos)
                {
                    if (_cache.TryAdd(info.Id, info))
                    {
                        _me ??= info.Participants.FirstOrDefault(p => p.IsMe);
                    }
                }

                // A newer encryption line that the store saw during the load wins.
                foreach ((string id, (bool Encrypted, long At) state) in encryption)
                {
                    _encryption.AddOrUpdate(id, state, (_, old) => state.At > old.At ? state : old);
                }

                // A draft that the user typed during the load wins.
                foreach ((string id, string draft) in drafts)
                {
                    _drafts.TryAdd(id, draft);
                }
            }
        });
    }

    /// <summary>
    /// Loads 1 page of a folder and caches it.
    /// </summary>
    /// <param name="cursor">The end of the previous page, or null for the newest page.</param>
    /// <returns>Up to 25 conversations, newest first, and the cursor for the next page or null.</returns>
    /// <exception cref="MessagingUnavailableException">The app is not connected to the phone.</exception>
    public async Task<(IReadOnlyList<ConversationInfo> Conversations, Cursor? Next)> LoadFolderAsync(ConversationFolder folder, Cursor? cursor, CancellationToken ct)
    {
        IMessagingClient client = RequireClient();

        ListConversationsRequest.Types.Folder protocolFolder = folder switch
        {
            ConversationFolder.SpamBlocked => ListConversationsRequest.Types.Folder.SpamBlocked,
            ConversationFolder.Archived    => ListConversationsRequest.Types.Folder.Archive,
            ConversationFolder.Inbox       => ListConversationsRequest.Types.Folder.Inbox,
            _                              => throw new ArgumentOutOfRangeException(nameof(folder)),
        };

        // No ConfigureAwait(false): the store must raise ConversationChanged on the thread of the caller.
        int generation                     = Generation;
        long asked                         = Stopwatch.GetTimestamp();
        ListConversationsResponse response = await client.ListConversationsAsync(protocolFolder, PageSize, cursor, ct);

        if (generation != Generation)
        {
            return ([], null);
        }

        // The log gets counts only, so it shows where paging stops.
        LogConversationPage(folder, PageSize, cursor is not null, response.Conversations.Count, response.Cursor is not null);

        // A conversation that changed live during the wait keeps the live copy. The phone made the page before the
        // change. Until the next update, the page copy can show a read chat as unread, without its newest message.
        List<ConversationInfo> result = [];
        foreach (Conversation c in response.Conversations)
        {
            bool newerHere = _liveAt.TryGetValue(c.ConversationID, out long changed) && changed > asked;
            result.Add(newerHere && Find(c.ConversationID) is { } kept ? kept : CacheConversation(c, generation));
        }

        // When the phone sends no cursor, the next page starts at the oldest conversation of this page. The cursor time
        // is in milliseconds, as for message pages. An empty page means that there are no more pages.
        Conversation? last = response.Conversations.LastOrDefault();
        Cursor? next       = response.Cursor ?? (last is not null ? new Cursor { LastItemID = last.ConversationID, LastItemTimestamp = last.LastMessageTimestamp / 1000 } : null);
        return (result, next);
    }

    /// <summary>
    /// Loads 1 page of messages.
    /// </summary>
    /// <param name="cursor">The end of the previous page, or null for the newest page.</param>
    /// <returns>Up to 25 messages, oldest first, and the cursor for the older page or null.</returns>
    /// <exception cref="MessagingUnavailableException">The app is not connected to the phone.</exception>
    public async Task<(IReadOnlyList<ChatMessage> Messages, Cursor? Older)> LoadMessagesAsync(string conversationId, Cursor? cursor, CancellationToken ct)
    {
        IMessagingClient client = RequireClient();

        int generation                = Generation;
        long asked                    = Stopwatch.GetTimestamp();
        ListMessagesResponse response = await client.FetchMessagesAsync(conversationId, PageSize, cursor, ct).ConfigureAwait(false);
        long pageMs                   = (long)Stopwatch.GetElapsedTime(asked).TotalMilliseconds;
        LogPhonePage(pageMs, response.Messages.Count);

        if (generation != Generation)
        {
            return ([], null);
        }

        // The phone answers newest first. An older page can hold the cursor message or newer ones, which the view
        // already shows. The store drops them, as pkg/connector/backfill.go in mautrix-gmessages does.
        List<Message> page = [.. response.Messages.Where(m => cursor is null || (m.MessageID != cursor.LastItemID && (cursor.LastItemTimestamp <= 0 || m.Timestamp / 1000 < cursor.LastItemTimestamp)))];
        List<ChatMessage> messages = [.. page.Select(ChatMessage.From).Reverse()];

        // Google usually sends no cursor. The next older page then starts at the oldest message of this page, as
        // mautrix-gmessages does. The cursor time is in milliseconds. An empty page means that there are no older
        // messages.
        Cursor? older = response.Cursor ?? (page.Count > 0 ? new Cursor { LastItemID = page[^1].MessageID, LastItemTimestamp = page[^1].Timestamp / 1000 } : null);

        // The memory cache keeps only the newest page, which shows when the chat opens again. The database gets every
        // page.
        if (cursor is null)
        {
            CachePage(conversationId, messages, onlyIfAbsent: false, generation);
        }

        SaveMessages(conversationId, page, generation);
        NoteEncryption(conversationId, messages, generation);

        return (messages, older);
    }

    /// <summary>
    /// Gets the newest page of a conversation from memory, or else from the database. A page from the database stays
    /// in memory.
    /// </summary>
    /// <remarks>
    /// A memory hit completes at once, on the thread of the caller. A miss reads the database at once on the thread
    /// pool, ahead of the queued saves. The call counts as a use, so the memory cache drops this conversation last.
    /// </remarks>
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

        int generation               = Generation;
        IReadOnlyList<Message> saved = [];
        long asked                   = Stopwatch.GetTimestamp();
        long started                 = asked;
        await ReadNowAsync(database =>
        {
            started = Stopwatch.GetTimestamp();
            if (generation == Generation)
            {
                saved = database.LoadMessages(conversationId, PageSize);
            }
        }).ConfigureAwait(false);
        long waitMs = (long)Stopwatch.GetElapsedTime(asked, started).TotalMilliseconds;
        long readMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogSavedPageRead(waitMs, readMs, saved.Count);

        if (saved.Count == 0 || generation != Generation)
        {
            return null;
        }

        // A page that the phone sent during the read wins over the saved page.
        List<ChatMessage> savedPage = [.. saved.Select(ChatMessage.From)];
        CachePage(conversationId, savedPage, onlyIfAbsent: true, generation);
        NoteEncryption(conversationId, savedPage, generation);
        return CachedMessages(conversationId);
    }

    /// <summary>
    /// Gets the newest page of a conversation from memory only. The page holds its last load and the messages since.
    /// </summary>
    /// <remarks>The call counts as a use, so the cache drops this conversation last.</remarks>
    /// <returns>A copy of the page, oldest first, or null when the conversation is not cached.</returns>
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
    /// Finds 1 message in the cached page of the conversation, or else in the database.
    /// </summary>
    /// <remarks>
    /// The view uses it for the quote of a reply. A memory hit completes at once on the thread of the caller and does
    /// not count as a use. A miss reads the database at once on the thread pool, ahead of the queued saves.
    /// </remarks>
    /// <returns>The message, or null when neither memory nor the database has it.</returns>
    public async Task<ChatMessage?> FindMessageAsync(string conversationId, string messageId)
    {
        // An empty ID matches a pending message, which has no ID yet.
        if (messageId.Length == 0)
        {
            return null;
        }

        lock (_pagesLock)
        {
            if (_pages.TryGetValue(conversationId, out List<ChatMessage>? page) && page.FirstOrDefault(m => m.Id == messageId) is { } hit)
            {
                return hit;
            }
        }

        if (_database is null)
        {
            return null;
        }

        int generation = Generation;
        Message? saved = null;
        await ReadNowAsync(database =>
        {
            if (generation == Generation)
            {
                saved = database.LoadMessage(conversationId, messageId);
            }
        }).ConfigureAwait(false);

        return saved is null || generation != Generation ? null : ChatMessage.From(saved);
    }

    /// <summary>
    /// Sends a text message the way the bridge does.
    /// </summary>
    /// <remarks>
    /// Ported from <c>ConvertMatrixMessage</c> in the Go connector. The request and the payload carry the same
    /// temporary ID. The message goes as the default outgoing participant of the conversation. The SIM is the card of
    /// that participant, from the last settings event. The exceptions for a phone that does not respond and for a
    /// closed connection go to the caller.
    /// </remarks>
    /// <param name="tmpId">
    /// The temporary ID from <c>GmClient.NewTmpId()</c>. It matches the echo to the pending bubble.
    /// </param>
    /// <param name="replyToId">The ID of the quoted message for an RCS reply, or null for a plain message.</param>
    /// <returns>The pending message as sent.</returns>
    /// <exception cref="MessagingUnavailableException">The app is not connected to the phone.</exception>
    /// <exception cref="MessageSendException">The phone answered with a status other than success.</exception>
    public async Task<ChatMessage> SendTextAsync(string conversationId, string text, string tmpId, string? replyToId, CancellationToken ct)
    {
        IMessagingClient client = RequireClient();

        int generation                = Generation;
        ConversationInfo conversation = await RequireConversationAsync(client, conversationId, generation, ct).ConfigureAwait(false);
        string outgoingId             = conversation.DefaultOutgoingId;

        SendMessageRequest request = NewSendRequest(conversationId, conversation, tmpId);
        request.MessagePayload.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = text } });

        // A reply carries the ID of the quoted message, as in handlematrix.go.
        if (replyToId is not null)
        {
            request.Reply = new ReplyPayload { MessageID = replyToId };
        }

        await SendCheckedAsync(client, request, ct).ConfigureAwait(false);

        var pending = ChatMessage.Pending(tmpId, conversationId, outgoingId, _time.GetUtcNow(), MessageKind.Text, text) with
        {
            ReplyToId = replyToId ?? string.Empty,
        };
        if (generation == Generation)
        {
            CacheMessage(pending);
        }

        return pending;
    }

    /// <summary>
    /// Uploads an attachment, encrypted, and does not send it yet.
    /// </summary>
    /// <remarks>
    /// The result is the <see cref="MediaContent"/> to send, with its media ID and decryption key. A retry sends it
    /// again without a new upload. A file over <see cref="MaxAttachmentBytes"/> fails before the upload starts. The
    /// store never logs the file name or type.
    /// </remarks>
    /// <param name="bytes">The bytes of the file.</param>
    /// <param name="fileName">The file name that the other side sees.</param>
    /// <param name="mime">The mime type, such as <c>image/jpeg</c>.</param>
    /// <param name="progress">Receives the done part of the upload, from 0 to 1, or null.</param>
    /// <returns>The uploaded media part.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="fileName"/> or <paramref name="mime"/> is null.
    /// </exception>
    /// <exception cref="AttachmentTooBigException">The file is over <see cref="MaxAttachmentBytes"/>.</exception>
    /// <exception cref="MessagingUnavailableException">The app is not connected to the phone.</exception>
    public Task<MediaContent> UploadAttachmentAsync(ReadOnlyMemory<byte> bytes, string fileName, string mime, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(mime);

        if (bytes.Length > MaxAttachmentBytes)
        {
            throw new AttachmentTooBigException();
        }

        return RequireClient().UploadMediaAsync(bytes, fileName, mime, progress, ct);
    }

    /// <summary>
    /// Sends an uploaded attachment, with an optional caption, the way the bridge does.
    /// </summary>
    /// <remarks>
    /// Ported from <c>ConvertMatrixMessage</c> in handlematrix.go of the Go connector. The uploaded
    /// <see cref="MediaContent"/> is the first <see cref="MessageInfo"/> of the message, and the caption is a second
    /// one. RCS refuses captions on media, see capabilities.go. Thus in an RCS chat, the caption goes as a separate
    /// text message right after the media. If only that caption fails, the media still counts as sent and the store
    /// logs the failure by type. Thus a retry never sends the media twice. The message goes as the default outgoing
    /// participant with its SIM, as in <see cref="SendTextAsync"/>. A reply carries the ID of the quoted message, as a
    /// text reply does. The store never logs the file name or type.
    /// </remarks>
    /// <param name="media">The uploaded media part from <see cref="UploadAttachmentAsync"/>.</param>
    /// <param name="caption">The caption, or null or empty for none.</param>
    /// <param name="tmpId">
    /// The temporary ID from <c>GmClient.NewTmpId()</c>. It matches the echo to the pending bubble.
    /// </param>
    /// <param name="replyToId">The ID of the quoted message, or null.</param>
    /// <returns>The pending media message as sent. A caption that goes separately is not in it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="media"/> is null.</exception>
    /// <exception cref="MessagingUnavailableException">The app is not connected to the phone.</exception>
    /// <exception cref="MessageSendException">The phone answered with a status other than success.</exception>
    public async Task<ChatMessage> SendUploadedMediaAsync(string conversationId, MediaContent media, string? caption, string tmpId, string? replyToId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(media);

        IMessagingClient client = RequireClient();

        int generation                = Generation;
        ConversationInfo conversation = await RequireConversationAsync(client, conversationId, generation, ct).ConfigureAwait(false);
        string text                   = caption ?? string.Empty;
        bool captionApart             = conversation.IsRcs && text.Length > 0;

        SendMessageRequest request = NewSendRequest(conversationId, conversation, tmpId);
        request.MessagePayload.MessageInfo.Add(new MessageInfo { MediaContent = media });
        if (text.Length > 0 && !captionApart)
        {
            request.MessagePayload.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = text } });
        }

        if (replyToId is not null)
        {
            request.Reply = new ReplyPayload { MessageID = replyToId };
        }

        await SendCheckedAsync(client, request, ct).ConfigureAwait(false);
        LogMediaSent(conversationId, media.Size);

        var pending = ChatMessage.Pending(tmpId, conversationId, conversation.DefaultOutgoingId, _time.GetUtcNow(), MediaPart.KindOf(media.Format), captionApart ? string.Empty : text) with
        {
            Media     = MediaPart.From(media),
            IsRcs     = conversation.IsRcs,
            ReplyToId = replyToId ?? string.Empty,
        };
        if (generation == Generation)
        {
            CacheMessage(pending);
        }

        if (captionApart)
        {
            try
            {
                await SendTextAsync(conversationId, text, GmClient.NewTmpId(), null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogActionFailed("send caption", ex.GetType().Name);
            }
        }

        return pending;
    }

    /// <summary>
    /// Adds, switches or removes my reaction on a message, the way the bridge does.
    /// </summary>
    /// <remarks>
    /// Ported from <c>HandleMatrixReaction</c> and <c>HandleMatrixReactionRemove</c> in the Go connector. The emoji
    /// maps to its LibGM type. The request carries the SIM of the conversation, as in <see cref="SendTextAsync"/>, but
    /// a removal carries no SIM. The bubble changes when the echo from the phone arrives.
    /// </remarks>
    /// <param name="messageId">The message that gets the reaction.</param>
    /// <param name="action">Add, switch from another emoji of mine, or remove.</param>
    /// <returns>A task that completes when the phone answered.</returns>
    /// <exception cref="MessagingUnavailableException">The app is not connected to the phone.</exception>
    /// <exception cref="MessageSendException">
    /// The phone refused the reaction, or the conversation is unknown.
    /// </exception>
    public async Task SendReactionAsync(string conversationId, string messageId, string emoji, SendReactionRequest.Types.Action action, CancellationToken ct)
    {
        IMessagingClient client = RequireClient();

        ConversationInfo conversation = await RequireConversationAsync(client, conversationId, Generation, ct).ConfigureAwait(false);

        var request = new SendReactionRequest
        {
            MessageID    = messageId,
            ReactionData = EmojiTypes.MakeReactionData(emoji),
            Action       = action,
            SIMPayload   = action == SendReactionRequest.Types.Action.Remove ? null : SimOf(conversation),
        };

        SendReactionResponse response = await client.SendReactionAsync(request, ct).ConfigureAwait(false);
        if (!response.Success)
        {
            LogSendRefused(conversationId, "ReactionRefused");
            throw new MessageSendException("ReactionRefused");
        }
    }

    /// <summary>
    /// Tells the other side of a conversation that I am typing.
    /// </summary>
    /// <remarks>
    /// The request carries the SIM of the conversation, as in <see cref="SendTextAsync"/>. The phone sends no answer.
    /// </remarks>
    /// <returns>A task that completes when the request is sent.</returns>
    /// <exception cref="MessagingUnavailableException">The app is not connected to the phone.</exception>
    /// <exception cref="MessageSendException">The conversation is unknown.</exception>
    public async Task SendTypingAsync(string conversationId, CancellationToken ct)
    {
        IMessagingClient client = RequireClient();

        ConversationInfo conversation = await RequireConversationAsync(client, conversationId, Generation, ct).ConfigureAwait(false);
        await client.SetTypingAsync(conversationId, SimOf(conversation), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a message on the phone, then from the cached page and the database.
    /// </summary>
    /// <remarks>
    /// Only the copy on this side goes. The other side keeps its copy. The phone updates the snippet of the
    /// conversation itself.
    /// </remarks>
    /// <returns>True when the phone deleted the message, or false when the phone refused.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="conversationId"/> or <paramref name="messageId"/> is empty.
    /// </exception>
    /// <exception cref="MessagingUnavailableException">The app is not connected to the phone.</exception>
    public async Task<bool> DeleteMessageAsync(string conversationId, string messageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        IMessagingClient client = RequireClient();

        int generation               = Generation;
        DeleteMessageResponse answer = await client.DeleteMessageAsync(messageId, ct).ConfigureAwait(false);
        if (!answer.Success || generation != Generation)
        {
            return answer.Success;
        }

        ForgetMessage(conversationId, messageId, generation);
        return true;
    }

    private void ForgetMessage(string conversationId, string messageId, int generation)
    {
        lock (_pagesLock)
        {
            if (_pages.TryGetValue(conversationId, out List<ChatMessage>? page))
            {
                page.RemoveAll(m => m.Id == messageId);
            }
        }

        if (_database is not null)
        {
            _ = EnqueueAsync(database =>
            {
                if (generation == Generation)
                {
                    database.DeleteMessage(conversationId, messageId);
                }
            });
        }
    }

    // No ConfigureAwait(false): the store must raise ConversationChanged on the thread of the caller.
    private async Task<ConversationInfo> RequireConversationAsync(IMessagingClient client, string conversationId, int generation, CancellationToken ct)
    {
        if (Find(conversationId) is { } cached)
        {
            return cached;
        }

        Conversation? fetched = await client.GetConversationAsync(conversationId, ct);
        if (fetched is null || generation != Generation)
        {
            throw new MessageSendException("ConversationNotFound");
        }

        return CacheConversation(fetched, generation);
    }

    // The SIM card of the default outgoing participant, from the last settings event.
    private SIMPayload? SimOf(ConversationInfo conversation) =>
        _sims.FirstOrDefault(s => s.SIMParticipant?.ID == conversation.DefaultOutgoingId)?.SIMData?.SIMPayload;

    private SendMessageRequest NewSendRequest(string conversationId, ConversationInfo conversation, string tmpId) => new()
    {
        ConversationID = conversationId,
        MessagePayload = new MessagePayload
        {
            TmpID          = tmpId,
            ConversationID = conversationId,
            ParticipantID  = conversation.DefaultOutgoingId,
            TmpID2         = tmpId,
        },
        SIMPayload = SimOf(conversation),
        TmpID      = tmpId,
        ForceRCS   = false,
    };

    private async Task SendCheckedAsync(IMessagingClient client, SendMessageRequest request, CancellationToken ct)
    {
        SendMessageResponse response = await client.SendMessageAsync(request, ct).ConfigureAwait(false);
        if (response.Status != SendMessageResponse.Types.Status.Success)
        {
            LogSendRefused(request.ConversationID, response.Status.ToString());
            throw new MessageSendException(response.Status.ToString());
        }
    }

    /// <summary>
    /// Tells the phone that a conversation is read up to a message, then marks the cached conversation read.
    /// </summary>
    /// <remarks>
    /// The cached copy changes when the phone answers and raises <see cref="ConversationChanged"/>. Thus the list stops
    /// showing it unread before the phone sends the conversation back. The saved row turns read too, so the chat does
    /// not show unread for a moment on the next start.
    /// </remarks>
    /// <param name="messageId">The newest message that the user read.</param>
    /// <returns>A task that completes when the phone answered.</returns>
    /// <exception cref="MessagingUnavailableException">The app is not connected to the phone.</exception>
    public async Task MarkReadAsync(string conversationId, string messageId, CancellationToken ct)
    {
        // No ConfigureAwait(false): the store must raise ConversationChanged on the thread of the caller.
        int generation = Generation;
        await RequireClient().MarkReadAsync(conversationId, messageId, ct);

        // The live unread texts start again, also when the cached conversation never turned unread.
        _unreadTexts.TryRemove(conversationId, out _);

        if (generation != Generation || Find(conversationId) is not { Unread: true } cached)
        {
            return;
        }

        ConversationInfo read = cached with { Unread = false };
        Upsert(read);

        // The saved row turns read only when its newest message is the read one or mine. Mine can be a reply from a
        // toast. The check runs with the write, so a message that came in meanwhile keeps the row unread on disk.
        SaveIfCurrent(conversationId, generation, database => database.UpdateConversation(conversationId, c =>
        {
            if (!c.Unread || (c.LatestMessageID != messageId && c.LatestMessage?.FromMe != 1))
            {
                return false;
            }

            c.Unread = false;
            return true;
        }));

        ConversationChanged?.Invoke(this, read);
    }

    /// <summary>
    /// Archives, unarchives, mutes, unmutes, blocks, unblocks or deletes a conversation on the phone.
    /// </summary>
    /// <remarks>
    /// The store sends the request from <see cref="BuildRequest"/>. When the phone accepts it, the cached conversation
    /// gets its new status from <see cref="StatusAfter"/> or its new mute state. That change raises
    /// <see cref="ConversationChanged"/>, so the lists move the conversation at once. The saved row changes too, so
    /// the next start does not show it in its old place for a moment. A <see cref="Reset"/> during the request
    /// applies nothing. The store logs a refusal. Callers log all other failures, as they do for the other writers.
    /// </remarks>
    /// <param name="action">The action to do.</param>
    /// <returns>A task that completes when the phone answered.</returns>
    /// <exception cref="MessagingUnavailableException">The app is not connected to the phone.</exception>
    /// <exception cref="MessageSendException">The phone refused the update, or the conversation is unknown.</exception>
    public async Task UpdateConversationAsync(string conversationId, ConversationAction action, CancellationToken ct)
    {
        IMessagingClient client = RequireClient();

        // No ConfigureAwait(false): the store must raise ConversationChanged on the thread of the caller.
        int generation                = Generation;
        ConversationInfo conversation = await RequireConversationAsync(client, conversationId, generation, ct);

        UpdateConversationResponse response = await client.UpdateConversationAsync(BuildRequest(conversation, action), ct);
        if (!response.Success)
        {
            LogConversationUpdateRefused(conversationId);
            throw new MessageSendException("ConversationUpdateRefused");
        }

        if (generation != Generation || Find(conversationId) is not { } cached)
        {
            return;
        }

        ConversationStatus? newStatus = StatusAfter(action);
        ConversationInfo updated      = newStatus is { } status
            ? cached with { Status = status }
            : cached with { IsMuted = action == ConversationAction.Mute };
        bool changed                  = Upsert(updated);

        // The saved row changes also when the cached copy already had the change. Thus the next start does not show
        // the chat in its old place before the page from the phone comes. A deleted chat leaves the database with all
        // its messages.
        if (action == ConversationAction.Delete)
        {
            _drafts.TryRemove(conversationId, out _);
            _draftFiles.TryRemove(conversationId, out _);
            SaveIfCurrent(conversationId, generation, database => database.DeleteConversation(conversationId));
        }
        else
        {
            ConversationMuteStatus mute = updated.IsMuted ? ConversationMuteStatus.Mute : ConversationMuteStatus.Unmute;
            SaveIfCurrent(conversationId, generation, database => database.UpdateConversation(conversationId, c =>
            {
                bool same = newStatus is { } target ? c.Status == target : c.Muted == mute;
                if (same)
                {
                    return false;
                }

                if (newStatus is { } newOne)
                {
                    c.Status = newOne;
                }
                else
                {
                    c.Muted = mute;
                }

                return true;
            }));
        }

        if (changed)
        {
            ConversationChanged?.Invoke(this, updated);
        }
    }

    /// <summary>
    /// Builds the phone request for a conversation action, the way LibGM and the Go connector do.
    /// </summary>
    /// <remarks>
    /// Archive, unarchive, mute and unmute fill only <c>UpdateData</c>, as <c>conversation_builder.go</c> in LibGM
    /// does. Delete, block and unblock set the action and the ID. They also fill <c>DeleteData</c> with the number of
    /// the other person, as <c>DeleteConversation</c> in <c>methods.go</c> and <c>HandleMatrixBlockUser</c> do. A
    /// block also sends <c>Action5</c>. The number comes from the first visible participant who is not me and has a
    /// number. Only a one-on-one chat sends it, as <c>findPhoneForConversation</c> does. Otherwise the number stays
    /// unset.
    /// </remarks>
    /// <returns>The request.</returns>
    internal static UpdateConversationRequest BuildRequest(ConversationInfo conversation, ConversationAction action)
    {
        string id = conversation.Id;

        switch (action)
        {
            case ConversationAction.Archive or ConversationAction.Unarchive:
                ConversationStatus status = action == ConversationAction.Archive ? ConversationStatus.Archived : ConversationStatus.Active;
                return new UpdateConversationRequest { UpdateData = new UpdateConversationData { ConversationID = id, Status = status } };

            case ConversationAction.Mute or ConversationAction.Unmute:
                ConversationMuteStatus mute = action == ConversationAction.Mute ? ConversationMuteStatus.Mute : ConversationMuteStatus.Unmute;
                return new UpdateConversationRequest { UpdateData = new UpdateConversationData { ConversationID = id, Mute = mute } };
        }

        var deleteData  = new DeleteConversationData { ConversationID = id };
        string? phone   = conversation.IsGroup ? null : conversation.Participants.FirstOrDefault(p => p.IsVisible && !p.IsMe && p.Number.Length > 0)?.Number;
        if (phone is not null)
        {
            deleteData.Phone = phone;
        }

        var request = new UpdateConversationRequest
        {
            Action = action switch
            {
                ConversationAction.Delete         => ConversationActionStatus.Delete,
                ConversationAction.Block          => ConversationActionStatus.Block,
                ConversationAction.BlockAndReport => ConversationActionStatus.BlockAndReport,
                ConversationAction.Unblock        => ConversationActionStatus.Unblock,
                _                                 => throw new ArgumentOutOfRangeException(nameof(action)),
            },
            ConversationID = id,
            DeleteData     = deleteData,
        };

        if (action is ConversationAction.Block or ConversationAction.BlockAndReport)
        {
            request.Action5 = new ConversationAction5 { Field2 = true };
        }

        return request;
    }

    /// <summary>
    /// Gets the status of a conversation after the phone accepts an action.
    /// </summary>
    /// <returns>The new status, or null when the action keeps the status, as mute and unmute do.</returns>
    internal static ConversationStatus? StatusAfter(ConversationAction action) => action switch
    {
        ConversationAction.Archive                                      => ConversationStatus.Archived,
        ConversationAction.Unarchive or ConversationAction.Unblock      => ConversationStatus.Active,
        ConversationAction.Block or ConversationAction.BlockAndReport   => ConversationStatus.BlockedFolder,
        ConversationAction.Delete                                       => ConversationStatus.Deleted,
        ConversationAction.Mute or ConversationAction.Unmute            => null,
        _                                                               => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>
    /// Forgets all data from the paired phone: cached conversations, the owner, the SIMs and the notified IDs. It also
    /// deletes the decrypted media in memory and in the temp folder, and the database.
    /// </summary>
    /// <remarks>
    /// Call it on unpair or sign-out. Conversation IDs are small numbers on each phone. Without a reset, a different
    /// phone paired later gets the names, owner and SIMs of the old phone. The reset also removes the names and
    /// numbers of the old account from memory and disk. A fetch that started before the reset drops its result: no
    /// cache write, no database write, no event. The background queue deletes the file after the writes that are
    /// already in the queue.
    /// </remarks>
    public void Reset()
    {
        lock (_resetLock)
        {
            Interlocked.Increment(ref _generation);
            _cache.Clear();
            _encryption.Clear();
            _liveMedia.Clear();
            _unreadTexts.Clear();
            _drafts.Clear();
            _draftFiles.Clear();
            _me = null;
        }

        _ = EnqueueAsync(database => database.Delete());
        _sims = [];
        SetBatteryLow(false);

        lock (_ringLock)
        {
            _ring.Clear();
            _ringSet.Clear();
        }

        lock (_pagesLock)
        {
            _pages.Clear();
            _droppedIds.Clear();
        }

        _media?.Clear();
    }

    /// <summary>
    /// Logs that a request of a view failed, with the error type only. The error message can hold content, so the
    /// log never gets it.
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Phone refused a send in {ConversationId} ({Status})")]
    private partial void LogSendRefused(string conversationId, string status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent an attachment of {Size} bytes in {ConversationId}")]
    private partial void LogMediaSent(string conversationId, long size);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Phone refused a conversation update ({ConversationId})")]
    private partial void LogConversationUpdateRefused(string conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not fetch conversation {ConversationId} ({ErrorType})")]
    private partial void LogConversationFetchFailed(string conversationId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not {Action} ({ErrorType})")]
    private partial void LogActionFailed(string action, string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversation page ({Folder}): asked {Asked} with a cursor: {CursorSent}; got {Got}, cursor back: {CursorBack}")]
    private partial void LogConversationPage(ConversationFolder folder, int asked, bool cursorSent, int got, bool cursorBack);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chat open: saved page read in {ReadMs} ms after {WaitMs} ms waiting, {Count} messages")]
    private partial void LogSavedPageRead(long waitMs, long readMs, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message page from the phone in {ElapsedMs} ms, {Count} messages")]
    private partial void LogPhonePage(long elapsedMs, int count);

    /// <summary>
    /// Logs 1 step of a chat open, in milliseconds since the user picked the chat. The log gets counts only, never
    /// content or IDs.
    /// </summary>
    /// <param name="step">The step, such as "cache shown" or "first bubbles drawn".</param>
    /// <param name="elapsedMs">The milliseconds since the user picked the chat.</param>
    /// <param name="count">The number of bubbles or messages in the step.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Chat open: {Step} at {ElapsedMs} ms, {Count} bubbles")]
    public partial void LogChatOpenStep(string step, long elapsedMs, int count);
}
