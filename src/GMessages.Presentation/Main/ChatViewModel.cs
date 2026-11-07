using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using GMessages.Presentation.Messaging;
using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// Shows 1 open conversation: its bubbles, the draft, the sends with their retries and the live updates.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ChatStore"/> raises its events on the UI thread. Thus the bubbles change on the UI thread. The store
/// lives longer than the window, and the view model listens to its events. Call <see cref="Dispose"/> when the chat
/// closes.
/// </para>
/// <para>
/// When the chat opens, it shows at once the page that <see cref="ChatStore"/> kept from the last time. Then the chat
/// merges the page of the phone into it with no ring. <see cref="IsLoadingHistory"/> asks for the loading ring only
/// when the chat has nothing to show.
/// </para>
/// <para>
/// A send adds a bubble at once. Any failure marks the bubble failed: no connection, no answer, a refusal or a network
/// error. The bubble keeps its text, so <see cref="RetryAsync"/> can send it again. The view gets no exception.
/// </para>
/// </remarks>
public sealed partial class ChatViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// The typing dots show for this time when no stop comes from the phone. The Go connector uses the same timeout in
    /// <c>handlegmessages.go</c>.
    /// </summary>
    public static readonly TimeSpan TypingTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan TypingResend = TimeSpan.FromSeconds(10);

    /// <summary>2 bubbles from the same sender stay in 1 group when this time or less separates them.</summary>
    public static readonly TimeSpan GroupGap = TimeSpan.FromMinutes(3);

    /// <summary>The chips together can hold this number of bytes at most: 200 MiB.</summary>
    public const long MaxTotalAttachmentBytes = 200L * 1024 * 1024;

    /// <summary>
    /// The jump from a reply quote loads this number of older pages at most to find the quoted message. Each page holds
    /// 25 messages.
    /// </summary>
    public const int ReplyJumpPages = 10;

    // TrimToNewest keeps this number of bubbles at most.
    internal const int MaxLoaded = 200;

    private readonly ChatStore _store;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly LinkPreviewer? _previews;
    private readonly long _openedAt = Stopwatch.GetTimestamp();

    // The avatar that the header shows. A conversation change compares the new avatar against it.
    private AvatarParts _avatar = new(string.Empty, AvatarParts.FallbackColor, string.Empty, string.Empty, 0, string.Empty);

    // Each refresh reads the setting. Null means that a link in a received message always gets a preview.
    private readonly Func<bool>? _showReceivedPreviews;

    private readonly ITimer _typingTimer;

    // ponytail: a group shows 1 typer at a time. Keep a set for each participant if group typing must be exact.
    private string _typingParticipant = string.Empty;
    private DateTimeOffset _typingUntil;
    private bool _isTyping;
    private DateTimeOffset? _typingSentAt;

    private bool _isEncrypted;
    private MessageBubbleViewModel? _replyingTo;

    // A callback that was posted before Dispose does nothing after it.
    private bool _disposed;

    // Null means that no older page is left.
    private Cursor? _older;

    // After a trim, a live update for a trimmed message waits for the older page. See OnMessageArrived.
    private bool _trimmed;

    // A list that is too short loads the next older page on its own only while this is true. It is true 1 time after
    // the newest page. Then it is true only after an older page that added bubbles. A failed load stops it until the
    // next good load. The resync after a reconnect brings that load.
    private bool _fillOlder;
    private bool _isLoading;
    private bool _isLoadingHistory;
    private bool _isLoadingOlder;

    // A reload request during a load runs when that load ends.
    private bool _reloadPending;

    // The chat counts the phone pages that showed. A saved page that comes after a phone page is stale, and the chat
    // drops it.
    private int _phonePages;

    // After the first page shows, a bubble that the chat adds live animates in. The page can be saved or from the
    // phone.
    private bool _pageShown;

    private string _draft = string.Empty;
    private string _attachError = string.Empty;
    private int _nextAttachmentId;

    // The dictionary holds each attachment bubble sent from here. Its bytes stay until the upload is done. Then the
    // upload stays, so a retry sends it again. Dispose clears the dictionary.
    private readonly Dictionary<MessageBubbleViewModel, OutgoingMedia> _outgoing = [];

    // The 2 lookups find the bubbles by message ID and by temporary ID. OnMessagesChanged and Index keep them in step
    // with Messages. A lookup checks that the bubble still has the key.
    private readonly Dictionary<string, MessageBubbleViewModel> _byId    = [];
    private readonly Dictionary<string, MessageBubbleViewModel> _byTmpId = [];

    // The last full refresh used this conversation. A live change refreshes all the bubbles when the participants
    // changed since then. See SameForBubbles.
    private ConversationInfo? _bubblesFor;

    /// <summary>
    /// Creates an empty chat and starts to listen to the store.
    /// </summary>
    /// <param name="store">Loads and sends messages and raises live updates.</param>
    /// <param name="conversationId">The conversation to show.</param>
    /// <param name="time">Gives the current time.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <param name="previews">Fetches the link cards for the bubbles of an inbox chat, or null for none.</param>
    /// <param name="showReceivedPreviews">
    /// Tells whether a link in a received message gets a preview, or null for always. A preview fetches the link of a
    /// stranger. A link in a message from the user always gets a preview.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// An argument other than <paramref name="previews"/> or <paramref name="showReceivedPreviews"/> is null.
    /// </exception>
    public ChatViewModel(ChatStore store, string conversationId, TimeProvider time, Action<Action> post, LinkPreviewer? previews = null, Func<bool>? showReceivedPreviews = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(post);

        _store         = store;
        ConversationId = conversationId;
        _time          = time;
        _post          = post;
        _previews      = previews;

        _showReceivedPreviews = showReceivedPreviews;

        _typingTimer = time.CreateTimer(_ => _post(OnTypingTimeout), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _isEncrypted = store.IsEncrypted(conversationId);

        Messages.CollectionChanged += OnMessagesChanged;

        _store.MessageArrived      += OnMessageArrived;
        _store.Resynced            += OnResynced;
        _store.TypingChanged       += OnTypingChanged;
        _store.ConversationChanged += OnConversationChanged;
        ApplyConversation(_store.Find(conversationId));

        Attachments.CollectionChanged += (_, _) =>
        {
            IsDraftEdited = true;
            Raise(nameof(HasAttachments));
            Raise(nameof(CanSend));
        };

        // The pane binds to the paths under Voice, so each step of the recording raises Voice.
        Voice.PropertyChanged += (_, _) =>
        {
            Raise(nameof(Voice));
            Raise(nameof(CanSend));
        };
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Logs 1 step of the chat open with the milliseconds since the pick and the count of the bubbles. The line holds
    /// no content and no IDs.
    /// </summary>
    /// <param name="step">The step, such as "first bubbles drawn".</param>
    public void LogOpenStep(string step)
    {
        long elapsedMs = (long)Stopwatch.GetElapsedTime(_openedAt).TotalMilliseconds;
        _store.LogChatOpenStep(step, elapsedMs, Messages.Count);
    }

    /// <summary>Gets the conversation ID.</summary>
    public string ConversationId { get; }

    /// <summary>
    /// Gets the previewer that fetches the link cards, or null for none. The chat also uses it to fetch a pasted web
    /// picture.
    /// </summary>
    public LinkPreviewer? Previews => _previews;

    /// <summary>Gets the conversation name.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the avatar letter: the letter of the conversation, or of the first other person in a group.
    /// </summary>
    public string Initial { get; private set; } = string.Empty;

    /// <summary>Gets the avatar color as opaque ARGB.</summary>
    public uint AvatarColor { get; private set; } = AvatarParts.FallbackColor;

    /// <summary>Gets the first other person, whose photo covers the avatar, or empty.</summary>
    public string PhotoId { get; private set; } = string.Empty;

    /// <summary>Gets the letter of the back circle: the second other person of a group, else empty.</summary>
    public string SecondInitial { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the color of the back circle as opaque ARGB. The value is 0, with no back circle, except in a group with 2
    /// other people.
    /// </summary>
    public uint SecondColor { get; private set; }

    /// <summary>Gets the participant whose photo covers the back circle, or empty.</summary>
    public string SecondPhotoId { get; private set; } = string.Empty;

    /// <summary>Gets the conversation for its menu and Details, or null until the store knows it.</summary>
    public ConversationInfo? Conversation { get; private set; }

    /// <summary>Gets whether the conversation is muted on the phone.</summary>
    public bool IsMuted { get; private set; }

    /// <summary>
    /// Gets whether the mute button of the header shows, as Mute or Unmute in the menu. It does not show in the spam
    /// and blocked folders.
    /// </summary>
    public bool CanMute => Conversation is { Status: not (ConversationStatus.SpamFolder or ConversationStatus.BlockedFolder) };

    /// <summary>Gets the empty message box's placeholder: "RCS Message" in an RCS chat, else "Text message".</summary>
    public string DraftPlaceholder => Conversation?.IsRcs == true ? ChatCopy.RcsMessage : ChatCopy.TextMessage;

    /// <summary>Gets the bubbles, oldest first.</summary>
    public ObservableCollection<MessageBubbleViewModel> Messages { get; } = [];

    /// <summary>Gets or sets the text that the user types.</summary>
    /// <remarks>
    /// In an RCS chat, a change to the text tells the other side about typing. It does so at most 1 time in each
    /// <see cref="TypingResend"/>.
    /// </remarks>
    public string Draft
    {
        get => _draft;
        set
        {
            IsDraftEdited |= (value ?? string.Empty) != _draft;
            _draft         = value ?? string.Empty;
            Raise(nameof(Draft));
            Raise(nameof(CanSend));

            DateTimeOffset now = _time.GetUtcNow();
            if (_draft.Length > 0 && _store.Find(ConversationId) is { IsRcs: true } && (_typingSentAt is null || now - _typingSentAt >= TypingResend))
            {
                _typingSentAt = now;
                _ = SendTypingAsync();
            }
        }
    }

    /// <summary>
    /// Restores the draft from the last time that the chat was open, with its files as chips. The other side gets no
    /// typing signal.
    /// </summary>
    /// <remarks>
    /// New chips continue the numbers of the kept chips, so the AutomationId of each chip stays unique.
    /// </remarks>
    /// <param name="draft">The kept draft.</param>
    /// <param name="files">The kept files, or null for none.</param>
    public void RestoreDraft(string draft, IReadOnlyList<PendingAttachment>? files = null)
    {
        _draft = draft ?? string.Empty;
        foreach (PendingAttachment file in files ?? [])
        {
            Attachments.Add(file);
            _nextAttachmentId = Math.Max(_nextAttachmentId, file.Id);
        }

        IsDraftEdited = false;
        Raise(nameof(Draft));
        Raise(nameof(CanSend));
    }

    /// <summary>
    /// Gets whether the text or the files changed since the chat opened or since <see cref="RestoreDraft"/>. Then the
    /// close of the chat saves the draft. An untouched box keeps the saved draft, also a draft that loaded later.
    /// </summary>
    public bool IsDraftEdited { get; private set; }

    /// <summary>Gets the bubble being replied to, or null.</summary>
    public MessageBubbleViewModel? ReplyingTo
    {
        get => _replyingTo;
        private set
        {
            _replyingTo = value;
            Raise(nameof(ReplyingTo));
            Raise(nameof(IsReplying));
        }
    }

    /// <summary>Gets whether the user writes a reply. The reply chip then shows.</summary>
    public bool IsReplying => _replyingTo is not null;

    /// <summary>Gets whether the other person types. The typing dots then show.</summary>
    public bool IsTyping
    {
        get => _isTyping;
        private set => Set(ref _isTyping, value, nameof(IsTyping));
    }

    /// <summary>Gets the avatar letter of the typer, beside the typing dots.</summary>
    public string TypingInitial { get; private set; } = string.Empty;

    /// <summary>Gets the typer's avatar color as opaque ARGB.</summary>
    public uint TypingColor { get; private set; } = AvatarParts.FallbackColor;

    /// <summary>Gets the participant whose photo covers the typer's avatar, or empty.</summary>
    public string TypingPhotoId { get; private set; } = string.Empty;

    /// <summary>Gets the typing bubble's name for screen readers, such as "Ann is typing".</summary>
    public string TypingName { get; private set; } = ChatCopy.Typing;

    /// <summary>Gets whether the conversation is end-to-end encrypted. The lock then shows.</summary>
    public bool IsEncrypted
    {
        get => _isEncrypted;
        private set => Set(ref _isEncrypted, value, nameof(IsEncrypted));
    }

    /// <summary>
    /// Gets whether the chat has something to send: text that is not only white space, an attachment or a recording.
    /// </summary>
    public bool CanSend => !string.IsNullOrWhiteSpace(_draft) || Attachments.Count > 0 || Voice.IsPreview;

    /// <summary>Gets the voice message: in the recording, or in the preview chip until Send.</summary>
    public VoiceDraft Voice { get; } = new();

    /// <summary>Gets the chips: the photos and files that the next Send sends, in order.</summary>
    public ObservableCollection<PendingAttachment> Attachments { get; } = [];

    /// <summary>Gets whether any attachment waits for Send. The chips then show.</summary>
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Gets the note shown over the chips when a picked file was refused, or empty.</summary>
    public string AttachError
    {
        get => _attachError;
        private set
        {
            if (_attachError == value)
            {
                return;
            }

            _attachError = value;
            Raise(nameof(AttachError));
            Raise(nameof(HasAttachError));
        }
    }

    /// <summary>Gets whether the refused-file note shows.</summary>
    public bool HasAttachError => _attachError.Length > 0;

    /// <summary>
    /// Gets or sets whether the window has focus with this chat open. Incoming messages then count as read.
    /// <see cref="MainViewModel"/> sets it.
    /// </summary>
    /// <remarks>
    /// A return of the focus reads the messages that came while the focus was away. The chat stayed open, so nothing
    /// else reads them. A chat that still loads reads itself when its page is in.
    /// </remarks>
    public bool IsActive
    {
        get;
        set
        {
            bool cameBack = value && !field;
            field         = value;
            if (cameBack && !IsLoading && _store.Find(ConversationId) is { Unread: true, LatestMessageId.Length: > 0 } unread)
            {
                _ = MarkReadAsync(unread.LatestMessageId);
            }
        }
    }

    /// <summary>
    /// Gets whether a list that is too short to scroll loads the next older page on its own.
    /// </summary>
    /// <remarks>
    /// It is true 1 time after the newest page loads. Then it is true only after an older page that added bubbles,
    /// while older pages are left. A failed load stops it until the next good newest page. The failure can be no
    /// connection, no answer or a network error. Thus the view never spins on a phone that cannot answer.
    /// </remarks>
    public bool CanFillOlder => _fillOlder && _older is not null;

    /// <summary>Gets whether a page loads.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value, nameof(IsLoading));
    }

    /// <summary>
    /// Gets whether the newest page loads with no bubbles to show yet, because no saved copy exists. The large ring
    /// then shows.
    /// </summary>
    public bool IsLoadingHistory
    {
        get => _isLoadingHistory;
        private set => Set(ref _isLoadingHistory, value, nameof(IsLoadingHistory));
    }

    /// <summary>
    /// Gets whether an older page loads above the bubbles. The small ring then shows at the top. A refresh of the saved
    /// copy shows no ring, by a ruling of the owner.
    /// </summary>
    public bool IsLoadingOlder
    {
        get => _isLoadingOlder;
        private set => Set(ref _isLoadingOlder, value, nameof(IsLoadingOlder));
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private MessageBubbleViewModel AddPending(ChatMessage pending)
    {
        MessageBubbleViewModel bubble = NewBubble(pending);
        MarkNewIfShown(bubble);
        Messages.Add(bubble);
        RefreshAround(bubble);
        return bubble;
    }

    // The bytes go in as inline data. Thus the bubble shows and plays them before the upload.
    private ChatMessage MediaMessage(string name, string mime, byte[] bytes, int width, int height, string caption, string? replyTo, bool isRcs)
    {
        MessageKind kind = MediaPart.KindOf(MediaFormatsMap.FromMime(mime));
        var media        = new MediaPart(string.Empty, [], string.Empty, [], bytes, name, bytes.LongLength, width, height, kind, mime);

        return ChatMessage.Pending(GmClient.NewTmpId(), ConversationId, string.Empty, _time.GetUtcNow(), kind, caption) with
        {
            Media     = media,
            IsRcs     = isRcs,
            ReplyToId = replyTo ?? string.Empty,
        };
    }

    // A bubble sent from here keeps its attachment. A failed bubble from before the chat opened sends the uploaded
    // media of its echo. A text bubble gives null.
    private OutgoingMedia? OutgoingOf(MessageBubbleViewModel bubble)
    {
        if (_outgoing.TryGetValue(bubble, out OutgoingMedia? kept))
        {
            return kept;
        }

        if (bubble is not { FromMe: true, Media: { MediaId.Length: > 0, Key.Length: > 0 } m })
        {
            return null;
        }

        var echoed = new OutgoingMedia([], m.Name, m.Mime)
        {
            Uploaded = new MediaContent
            {
                Format        = MediaFormatsMap.FromMime(m.Mime),
                MediaID       = m.MediaId,
                MediaName     = m.Name,
                Size          = m.Size,
                DecryptionKey = Google.Protobuf.ByteString.CopyFrom(m.Key),
                MimeType      = m.Mime,
            },
        };
        _outgoing[bubble] = echoed;
        return echoed;
    }

    // The bytes stay until the upload is done. Then the chat frees them and keeps the upload.
    private sealed class OutgoingMedia(byte[] bytes, string name, string mime)
    {
        public byte[] Bytes { get; set; } = bytes;

        public string Name { get; } = name;

        public string Mime { get; } = mime;

        public MediaContent? Uploaded { get; set; }
    }

    // The report goes to the UI thread through the post of the chat, not to the thread of the upload. It goes only
    // when the whole percent moves, so a big upload does not flood the UI thread.
    private sealed class PostedProgress(Action<double> report) : IProgress<double>
    {
        private int _percent = -1;

        public void Report(double value)
        {
            int percent = (int)(Math.Clamp(value, 0, 1) * 100);
            if (Interlocked.Exchange(ref _percent, percent) != percent)
            {
                report(value);
            }
        }
    }

    // The ID matches first. Then the temporary ID matches a bubble sent from here.
    private MessageBubbleViewModel? FindBubble(ChatMessage m) =>
        ById(m.Id) ?? (m.TmpId.Length > 0 && _byTmpId.TryGetValue(m.TmpId, out MessageBubbleViewModel? b) && b.TmpId == m.TmpId ? b : null);

    private MessageBubbleViewModel? ById(string id) =>
        id.Length > 0 && _byId.TryGetValue(id, out MessageBubbleViewModel? b) && b.Id == id ? b : null;

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _byId.Clear();
            _byTmpId.Clear();
            foreach (MessageBubbleViewModel b in Messages)
            {
                Index(b);
            }

            return;
        }

        foreach (MessageBubbleViewModel b in e.OldItems ?? Array.Empty<MessageBubbleViewModel>())
        {
            if (_byId.TryGetValue(b.Id, out MessageBubbleViewModel? byId) && byId == b)
            {
                _byId.Remove(b.Id);
            }

            if (_byTmpId.TryGetValue(b.TmpId, out MessageBubbleViewModel? byTmp) && byTmp == b)
            {
                _byTmpId.Remove(b.TmpId);
            }
        }

        foreach (MessageBubbleViewModel b in e.NewItems ?? Array.Empty<MessageBubbleViewModel>())
        {
            Index(b);
        }
    }

    // Call it again when an echo gives the bubble its ID. The first bubble with an ID keeps the ID.
    private void Index(MessageBubbleViewModel b)
    {
        if (b.Id.Length > 0)
        {
            _byId.TryAdd(b.Id, b);
        }

        if (b.TmpId.Length > 0)
        {
            _byTmpId.TryAdd(b.TmpId, b);
        }
    }

    // Shows the newest page in place of the bubbles. It reuses and updates the bubbles that show, so the list does not
    // draw them again. Unsent bubbles stay at the bottom unless the page holds them. Bubbles that came during the load
    // and are newer than the page also stay. The older pages above go.
    private void ShowPage(IReadOnlyList<ChatMessage> page)
    {
        _pageShown = true;
        List<MessageBubbleViewModel> wanted  = [];
        HashSet<MessageBubbleViewModel> keep = [];
        foreach (ChatMessage m in page.Where(m => !ChatFormat.IsHiddenLine(m)))
        {
            MessageBubbleViewModel? bubble = FindBubble(m);
            if (bubble is null || keep.Contains(bubble))
            {
                bubble = NewBubble(m);
            }
            else
            {
                bubble.Update(m);
                Index(bubble);
            }

            wanted.Add(bubble);
            keep.Add(bubble);
        }

        DateTimeOffset? newest         = page.Count > 0 ? page.Max(m => m.Time) : null;
        MessageBubbleViewModel[] newer = [.. Messages
            .Where(b => !keep.Contains(b) && (b.Id.Length == 0 || b.SentAt > newest))
            .OrderBy(b => b.SentAt)];
        wanted.AddRange(newer);
        keep.UnionWith(newer);

        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Messages[i]))
            {
                Messages.RemoveAt(i);
            }
        }

        // Usually each bubble is in its place already, so the loop looks only for a bubble out of place. A position map
        // goes stale with each insert or move.
        HashSet<MessageBubbleViewModel> shown = [.. Messages];
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < Messages.Count && ReferenceEquals(Messages[i], wanted[i]))
            {
                continue;
            }

            if (shown.Contains(wanted[i]))
            {
                Messages.Move(Messages.IndexOf(wanted[i]), i);
            }
            else
            {
                Messages.Insert(i, wanted[i]);
            }
        }

        RefreshBubbles();
    }

    private void ApplyConversation(ConversationInfo? c)
    {
        if (c is null)
        {
            return;
        }

        AvatarParts avatar = AvatarParts.Of(c);
        _avatar            = avatar;
        Conversation       = c;
        IsMuted            = c.IsMuted;
        Title              = c.Name;
        Initial            = avatar.Initial;
        AvatarColor        = avatar.Color;
        PhotoId            = avatar.PhotoId;
        SecondInitial      = avatar.SecondInitial;
        SecondColor        = avatar.SecondColor;
        SecondPhotoId      = avatar.SecondPhotoId;
        Raise(string.Empty);
    }

    // The bubble gets the conversation, so it can tell which reaction is mine.
    private MessageBubbleViewModel NewBubble(ChatMessage m) => new(m, _store.Find(ConversationId));

    // A bubble that the chat sends or gets live animates in. Before the first page shows, the fade of the chat covers
    // it.
    private void MarkNewIfShown(MessageBubbleViewModel bubble)
    {
        if (_pageShown)
        {
            bubble.MarkNew();
        }
    }

    // Updates the senders, the quotes, the status line and the lock on each bubble. A page, an older page, a trim or a
    // change to the conversation calls it.
    private void RefreshBubbles()
    {
        ConversationInfo? c = _store.Find(ConversationId);
        bool encrypted      = _store.IsEncrypted(ConversationId);
        _bubblesFor         = c;

        RefreshGroups(c, 0, Messages.Count - 1);
        RefreshQuotes(c);
        RefreshStatus(encrypted, 0);
        RefreshPreviews(c);
        IsEncrypted = encrypted;
    }

    // Does the same after a live add or change of 1 bubble. Only that bubble and its neighbors group again. Only the
    // last 2 bubbles can get or lose the status line. The status pass starts at the bubble when it is higher, so a
    // bubble put in above them gets its lock. A bubble that no longer shows refreshes all the bubbles. A change to the
    // participants since the last full refresh also refreshes all the bubbles.
    private void RefreshAround(MessageBubbleViewModel bubble)
    {
        ConversationInfo? c = _store.Find(ConversationId);
        int at              = Messages.IndexOf(bubble);
        if (at < 0 || !SameForBubbles(_bubblesFor, c))
        {
            RefreshBubbles();
            return;
        }

        // The status moves from the last real bubble before to the newest bubble. It skips each system line between.
        bool encrypted = _store.IsEncrypted(ConversationId);
        int from       = Messages.Count - 2;
        while (from > 0 && Messages[from].IsSystemLine)
        {
            from--;
        }

        RefreshGroups(c, at - 1, at + 1);
        RefreshQuotes(c);
        RefreshStatus(encrypted, Math.Min(from, at));
        RefreshPreviews(c);
        IsEncrypted = encrypted;
    }

    // Tells whether 2 copies of the conversation show the bubbles the same. The group flag, the participants and the
    // outgoing ID must match. The participants carry the names, the colors and the flag for me.
    private static bool SameForBubbles(ConversationInfo? a, ConversationInfo? b) =>
        ReferenceEquals(a, b)
        || (a is not null && b is not null && a.IsGroup == b.IsGroup && a.DefaultOutgoingId == b.DefaultOutgoingId && a.Participants.SequenceEqual(b.Participants));

    // Asks 1 time for each link for the preview card of each bubble, only in an inbox chat. Spam, blocked, archived and
    // unknown chats get no preview, so the chat never fetches the link of a stranger. A link in a received message
    // waits for the setting.
    private void RefreshPreviews(ConversationInfo? c)
    {
        if (_previews is null || c is not { IsMainView: true })
        {
            return;
        }

        bool received = _showReceivedPreviews?.Invoke() ?? true;
        foreach (MessageBubbleViewModel b in Messages)
        {
            if ((b.FromMe || received) && b.LinkUrl is { } url && b.PreviewAskedFor != url)
            {
                b.PreviewAskedFor = url;
                _ = LoadPreviewAsync(_previews, b, url);
            }
        }
    }

    // GetAsync never throws. The post sets the card on the UI thread when the bubble still shows that link.
    private async Task LoadPreviewAsync(LinkPreviewer previews, MessageBubbleViewModel bubble, Uri url)
    {
        if (await previews.GetAsync(url, CancellationToken.None) is not { } preview)
        {
            return;
        }

        _post(() =>
        {
            if (!_disposed && bubble.LinkUrl == url)
            {
                bubble.SetPreview(preview);
            }
        });
    }

    // Groups the bubbles as Google Messages does: the same sender, me or 1 person, each a few minutes after the last.
    // In a group chat, a received group shows the name of its sender over its first bubble. It shows the avatar of its
    // sender beside its last bubble. Only the bubbles from 1 index to another change, clamped to the list.
    private void RefreshGroups(ConversationInfo? c, int from, int to)
    {
        bool isGroup  = c is { IsGroup: true };
        int end       = Math.Min(to, Messages.Count - 1);

        for (int i = Math.Max(0, from); i <= end; i++)
        {
            MessageBubbleViewModel b = Messages[i];
            bool first               = i == 0 || !SameGroup(Messages[i - 1], b);
            bool last                = i == Messages.Count - 1 || !SameGroup(b, Messages[i + 1]);
            ParticipantInfo? sender  = c?.FindParticipant(b.ParticipantId);
            string name              = ChatFormat.SenderName(sender);
            bool lane                = isGroup && !b.FromMe;

            // The name takes the color of the letter circle of the sender. The conversation tells which reaction is
            // mine.
            b.SetGroup(first, last, lane, lane && first && name.Length > 0 ? name : null, AvatarParts.ColorOf(sender), ChatFormat.Initial(name), b.ParticipantId);
            b.SetConversation(c);
        }

        // A system line, such as a group rename, breaks a group.
        static bool SameGroup(MessageBubbleViewModel a, MessageBubbleViewModel b) =>
            !a.IsSystemLine && !b.IsSystemLine && a.FromMe == b.FromMe && (a.FromMe || a.ParticipantId == b.ParticipantId) && b.SentAt - a.SentAt <= GroupGap;
    }

    // A quote comes from the bubbles that show, else from the store, else it reads "Original message" with no sender.
    // The store answers from memory or from the database. The chat asks the store 1 time for each bubble, only while
    // its quote is empty.
    private void RefreshQuotes(ConversationInfo? c)
    {
        foreach (MessageBubbleViewModel b in Messages.Where(b => b.HasQuote))
        {
            if (ById(b.ReplyToId) is { } shown)
            {
                b.SetQuote(ChatFormat.SenderName(c?.FindParticipant(shown.ParticipantId)), shown.Text, shown.Media);
            }
            else if (b.QuoteText.Length == 0)
            {
                b.SetQuote(string.Empty, ChatCopy.OriginalMessage);
                _ = LookUpQuoteAsync(b, b.ReplyToId);
            }
        }
    }

    // The database answers off the UI thread, so the chat posts the result.
    private async Task LookUpQuoteAsync(MessageBubbleViewModel bubble, string replyToId)
    {
        if (await _store.FindMessageAsync(ConversationId, replyToId) is not { } quoted)
        {
            return;
        }

        string text = ChatFormat.ShownText(quoted);
        _post(() =>
        {
            if (!_disposed && bubble.ReplyToId == replyToId)
            {
                bubble.SetQuote(ChatFormat.SenderName(_store.Find(ConversationId)?.FindParticipant(quoted.ParticipantId)), text, quoted.Media);
            }
        });
    }

    // The caption row goes only under the newest bubble of the chat, mine or received. On my bubble, it shows "Sent",
    // "Delivered" or "Read" as the icon of Google with that name. It shows the clock while the bubble sends. Each RCS
    // bubble of an encrypted chat has the lock, by its caption row or its clicked time. A bubble sent as SMS meanwhile
    // has no lock. Only the bubbles from an index to the end change.
    private void RefreshStatus(bool encrypted, int from)
    {
        MessageBubbleViewModel? newest = Messages.LastOrDefault(b => !b.IsSystemLine);

        for (int i = Math.Max(0, from); i < Messages.Count; i++)
        {
            MessageBubbleViewModel b = Messages[i];
            bool isNewest            = b == newest;
            b.SetStatus(isNewest, isNewest && b.FromMe ? ChatFormat.StatusText(b.Status) : string.Empty, encrypted && b.IsRcs);
        }
    }

    private void Set(ref bool field, bool value, string property)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        Raise(property);
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
