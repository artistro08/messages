using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using GMessages.Presentation.Messaging;
using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// One open conversation: its bubbles, the draft, sending with retry, and live updates.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ChatStore"/> raises its events on the UI thread, so bubbles change there. The view model subscribes to
/// the store (which outlives the window), so call <see cref="Dispose"/> when the chat closes.
/// </para>
/// <para>
/// Opening a conversation shows the page <see cref="ChatStore"/> kept from last time at once, then the phone's page
/// is merged into it quietly. Only with nothing to show does <see cref="IsLoadingHistory"/> ask for the loading ring.
/// </para>
/// <para>
/// A send adds a bubble at once. If it fails for any reason (not connected, not answering, refused, a network error) the bubble is
/// marked failed and keeps its text so <see cref="RetryAsync"/> can resend it; nothing is thrown at the view.
/// </para>
/// </remarks>
public sealed partial class ChatViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>How long "typing" shows without a stop from the phone (the Go connector's timeout, handlegmessages.go).</summary>
    public static readonly TimeSpan TypingTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long after telling the other side I'm typing before telling them again.</summary>
    public static readonly TimeSpan TypingResend = TimeSpan.FromSeconds(10);

    /// <summary>The longest gap between two messages from the same sender that still groups their bubbles.</summary>
    public static readonly TimeSpan GroupGap = TimeSpan.FromMinutes(3);

    /// <summary>The most the chips may hold together, in bytes: 200 MiB.</summary>
    public const long MaxTotalAttachmentBytes = 200L * 1024 * 1024;

    /// <summary>The most older pages a reply quote's jump loads looking for the quoted message (25 messages each).</summary>
    public const int ReplyJumpPages = 10;

    // The Most Bubbles Kept Once The List Is Back At The Newest Message (See TrimToNewest)
    internal const int MaxLoaded = 200;

    private readonly ChatStore _store;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;

    // Fetches Link Preview Cards; Null Means None
    private readonly LinkPreviewer? _previews;

    // Whether A Received Message's Link May Be Fetched For A Preview (Settings; Read On Each Refresh); Null Means Always
    private readonly Func<bool>? _showReceivedPreviews;

    // Clears The Typing Dots When No Stop Arrives In Time
    private readonly ITimer _typingTimer;

    // Who Is Typing, And Until When; One Person At A Time
    // ponytail: a group shows one typer; track a set per participant if group typing needs to be exact
    private string _typingParticipant = string.Empty;
    private DateTimeOffset _typingUntil;
    private bool _isTyping;

    // When I Last Told The Other Side I'm Typing
    private DateTimeOffset? _typingSentAt;

    private bool _isEncrypted;
    private MessageBubbleViewModel? _replyingTo;

    // Set By Dispose, So A Callback Posted Before It Does Nothing After
    private bool _disposed;

    // Where The Last Older Page Ended; Null Means No More Pages
    private Cursor? _older;

    // Whether Older Bubbles Were Trimmed Away: A Live Update For One Then Waits For The Older Page (See OnMessageArrived)
    private bool _trimmed;

    // Whether A Too-Short List May Load The Next Older Page On Its Own: Once After The Newest Page, Then Only After An
    // Older Page That Added Bubbles. A Failed Load Stops It Until The Next Good Load (The Resync After Reconnecting)
    private bool _fillOlder;
    private bool _isLoading;
    private bool _isLoadingHistory;
    private bool _isLoadingOlder;

    // A Reload Asked For While A Load Was Running; Runs When That Load Ends
    private bool _reloadPending;

    // How Many Phone Pages Were Shown; A Saved Copy That Lands After One Is Stale And Dropped
    private int _phonePages;

    // A Page (Saved Or From The Phone) Has Shown: Bubbles Added Live From Now On Animate In
    private bool _pageShown;

    private string _draft = string.Empty;

    // The Too-Big Note Over The Chips, Or Empty; And The Next Chip's Number
    private string _attachError = string.Empty;
    private int _nextAttachmentId;

    // Each Attachment Bubble Sent From Here: Its Bytes Until The Upload Is Done, Then The Upload, Which A Retry Sends
    // Again. Dropped When The Chat Closes
    private readonly Dictionary<MessageBubbleViewModel, OutgoingMedia> _outgoing = [];

    // The Bubbles By Message ID And By Temporary ID, Kept In Step With Messages (OnMessagesChanged, Index). A Lookup Checks
    // The Bubble Still Has The Key
    private readonly Dictionary<string, MessageBubbleViewModel> _byId    = [];
    private readonly Dictionary<string, MessageBubbleViewModel> _byTmpId = [];

    // The Conversation The Last Full Refresh Grouped The Bubbles For; A Live Change Refreshes Everything When Its
    // Participants Differ Since (See SameForBubbles)
    private ConversationInfo? _bubblesFor;

    /// <summary>
    /// Creates an empty chat and starts listening to the store.
    /// </summary>
    /// <param name="store">Loads and sends messages and raises live updates.</param>
    /// <param name="conversationId">The conversation to show.</param>
    /// <param name="time">Gives the current time.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <param name="previews">Fetches link preview cards for an inbox chat's bubbles; null for none.</param>
    /// <param name="showReceivedPreviews">
    /// Says whether links in received messages get a preview (a stranger's link is then fetched); null for always. Links
    /// in the user's own messages always do.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="previews"/> or <paramref name="showReceivedPreviews"/> is null.</exception>
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

        // A Chip Added Or Removed: Send May Turn On Or Off, And The Draft Changed
        Attachments.CollectionChanged += (_, _) =>
        {
            IsDraftEdited = true;
            Raise(nameof(HasAttachments));
            Raise(nameof(CanSend));
        };

        // A Recording Waiting To Be Sent Turns Send On; The Pane Follows Its Steps
        Voice.PropertyChanged += (_, _) =>
        {
            Raise(nameof(Voice));
            Raise(nameof(CanSend));
        };
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets when the chat was picked (a <see cref="Stopwatch"/> timestamp), for the open-timing log lines.</summary>
    public long OpenedAt { get; } = Stopwatch.GetTimestamp();

    /// <summary>
    /// Logs one step of opening the chat with the milliseconds since it was picked and how many bubbles show (no
    /// content, no IDs).
    /// </summary>
    /// <param name="step">The step, such as "first bubbles drawn".</param>
    public void LogOpenStep(string step)
    {
        long elapsedMs = (long)Stopwatch.GetElapsedTime(OpenedAt).TotalMilliseconds;
        _store.LogChatOpenStep(step, elapsedMs, Messages.Count);
    }

    /// <summary>Gets the conversation ID.</summary>
    public string ConversationId { get; }

    /// <summary>Gets the previewer that fetches link cards, which the chat also uses to fetch a pasted web picture; null for none.</summary>
    public LinkPreviewer? Previews => _previews;

    /// <summary>Gets the conversation name.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Gets the avatar letter: the conversation's, or a group's first other person's.</summary>
    public string Initial { get; private set; } = string.Empty;

    /// <summary>Gets the avatar color as opaque ARGB.</summary>
    public uint AvatarColor { get; private set; } = AvatarParts.FallbackColor;

    /// <summary>Gets the participant whose photo covers the avatar (the first other person), or empty.</summary>
    public string PhotoId { get; private set; } = string.Empty;

    /// <summary>Gets the back circle's letter: the second other person of a group, else empty.</summary>
    public string SecondInitial { get; private set; } = string.Empty;

    /// <summary>Gets the back circle's color as opaque ARGB; 0 (no back circle) unless a group with two others.</summary>
    public uint SecondColor { get; private set; }

    /// <summary>Gets the participant whose photo covers the back circle, or empty.</summary>
    public string SecondPhotoId { get; private set; } = string.Empty;

    /// <summary>Gets the conversation (for its menu and Details), or null until the store knows it.</summary>
    public ConversationInfo? Conversation { get; private set; }

    /// <summary>Gets whether the conversation is muted on the phone.</summary>
    public bool IsMuted { get; private set; }

    /// <summary>
    /// Gets whether the header's mute button shows: as the menu's Mute or Unmute, not in the spam and blocked folders.
    /// </summary>
    public bool CanMute => Conversation is { Status: not (ConversationStatus.SpamFolder or ConversationStatus.BlockedFolder) };

    /// <summary>Gets the bubbles, oldest first.</summary>
    public ObservableCollection<MessageBubbleViewModel> Messages { get; } = [];

    /// <summary>Gets or sets the text being typed.</summary>
    /// <remarks>Typing in an RCS chat tells the other side, at most once every <see cref="TypingResend"/>.</remarks>
    public string Draft
    {
        get => _draft;
        set
        {
            IsDraftEdited |= (value ?? string.Empty) != _draft;
            _draft         = value ?? string.Empty;
            Raise(nameof(Draft));
            Raise(nameof(CanSend));

            // Typing Out: RCS Only, Not More Often Than Every 10 Seconds
            DateTimeOffset now = _time.GetUtcNow();
            if (_draft.Length > 0 && _store.Find(ConversationId) is { IsRcs: true } && (_typingSentAt is null || now - _typingSentAt >= TypingResend))
            {
                _typingSentAt = now;
                _ = SendTypingAsync();
            }
        }
    }

    /// <summary>
    /// Puts back a draft kept from the last time the chat was open, and its files as chips, without telling the other
    /// side I'm typing.
    /// </summary>
    /// <remarks>New chips number on from the kept ones, so every chip's AutomationId stays unique.</remarks>
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
    /// Gets whether the text or the files changed since the chat opened (or its draft was put back), so leaving it saves
    /// the draft; an untouched box keeps the saved one, even one that loaded after the chat opened.
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

    /// <summary>Gets whether a reply is being written (the reply chip shows).</summary>
    public bool IsReplying => _replyingTo is not null;

    /// <summary>Gets whether the other person is typing (the typing dots show).</summary>
    public bool IsTyping
    {
        get => _isTyping;
        private set => Set(ref _isTyping, value, nameof(IsTyping));
    }

    /// <summary>Gets whether the conversation is end-to-end encrypted (the lock shows).</summary>
    public bool IsEncrypted
    {
        get => _isEncrypted;
        private set => Set(ref _isEncrypted, value, nameof(IsEncrypted));
    }

    /// <summary>Gets whether there is something to send: text (not empty or only spaces), an attachment or a recording.</summary>
    public bool CanSend => !string.IsNullOrWhiteSpace(_draft) || Attachments.Count > 0 || Voice.IsPreview;

    /// <summary>Gets the voice message being recorded, or recorded and waiting for Send (the preview chip).</summary>
    public VoiceDraft Voice { get; } = new();

    /// <summary>Gets the photos and files picked to send with the next Send, in order (the chips).</summary>
    public ObservableCollection<PendingAttachment> Attachments { get; } = [];

    /// <summary>Gets whether any attachment waits to be sent (the chips show).</summary>
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

    /// <summary>Gets or sets whether the window is focused with this chat open, so incoming messages count as read (set by <see cref="MainViewModel"/>).</summary>
    /// <remarks>
    /// Coming back to the window reads what came in while it was away: the chat stayed open, so nothing else would (it
    /// read only once the chat was opened again). A chat still loading reads itself when its page is in.
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
    /// Gets whether a list too short to scroll should load the next older page on its own.
    /// </summary>
    /// <remarks>
    /// True once after the newest page loads, then only after an older page that added bubbles, while older pages are
    /// left. A failed load (phone not connected or not answering, a network error) stops it until the next successful newest page, so
    /// the view never spins on a phone that can't answer.
    /// </remarks>
    public bool CanFillOlder => _fillOlder && _older is not null;

    /// <summary>Gets whether a page is loading.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value, nameof(IsLoading));
    }

    /// <summary>Gets whether the newest page is loading with no bubbles to show yet (no cached copy): the large ring.</summary>
    public bool IsLoadingHistory
    {
        get => _isLoadingHistory;
        private set => Set(ref _isLoadingHistory, value, nameof(IsLoadingHistory));
    }

    /// <summary>Gets whether an older page is loading above the bubbles: the small ring at the top. A refresh of the cached copy shows no ring (owner's call).</summary>
    public bool IsLoadingOlder
    {
        get => _isLoadingOlder;
        private set => Set(ref _isLoadingOlder, value, nameof(IsLoadingOlder));
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // A Bubble Sent From Here: Added At Once, Animating In
    private MessageBubbleViewModel AddPending(ChatMessage pending)
    {
        var bubble = NewBubble(pending);
        MarkNewIfShown(bubble);
        Messages.Add(bubble);
        RefreshAround(bubble);
        return bubble;
    }

    // An Attachment's Pending Message: Its Bytes Ride As Inline Data, So The Bubble Shows (And Plays) Them Before Any
    // Upload
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

    // The Attachment A Bubble Sends: Kept From Its Send Here, Or (A Failed Bubble From Before The Chat Opened) The
    // Uploaded Media Its Echo Carried; Null For Text
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

    // An Attachment Sent From Here: Its Bytes Until The Upload Is Done (Then Freed), And The Upload
    private sealed class OutgoingMedia(byte[] bytes, string name, string mime)
    {
        public byte[] Bytes { get; set; } = bytes;

        public string Name { get; } = name;

        public string Mime { get; } = mime;

        public MediaContent? Uploaded { get; set; }
    }

    // Reports On The UI Thread Through The Chat's Post, Not On The Upload's Thread; Only When The Whole Percent Moves,
    // So A Big Upload Doesn't Flood The UI Thread
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

    // Match By ID, Then By The Temporary ID Of A Bubble Sent From Here
    private MessageBubbleViewModel? FindBubble(ChatMessage m) =>
        ById(m.Id) ?? (m.TmpId.Length > 0 && _byTmpId.TryGetValue(m.TmpId, out MessageBubbleViewModel? b) && b.TmpId == m.TmpId ? b : null);

    // The Bubble Shown For A Message ID, Or Null
    private MessageBubbleViewModel? ById(string id) =>
        id.Length > 0 && _byId.TryGetValue(id, out MessageBubbleViewModel? b) && b.Id == id ? b : null;

    // Keeps The ID Lookups In Step With The Bubbles Shown
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

    // A Bubble Under Its IDs; Call Again When An Echo Gives It Its ID. The First Bubble With An ID Keeps It
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

    // Shows The Newest Page In Place Of The Bubbles, Reusing (And Updating) The Ones Already Shown So The List Doesn't
    // Redraw Them. Unsent Bubbles, And Ones That Arrived While The Page Was On Its Way (Newer Than Anything In It),
    // Stay At The Bottom Unless The Page Holds Them; Older Pages Above Are Dropped
    private void ShowPage(IReadOnlyList<ChatMessage> page)
    {
        _pageShown = true;
        List<MessageBubbleViewModel> wanted  = [];
        HashSet<MessageBubbleViewModel> keep = [];
        foreach (ChatMessage m in page.Where(m => m.Kind != MessageKind.Tombstone))
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

        // Drop What The Page No Longer Holds
        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Messages[i]))
            {
                Messages.RemoveAt(i);
            }
        }

        // Then Put Each Bubble In Its Place. Usually It Is Already There, So Only One Out Of Place Is Looked For (A
        // Position Map Would Go Stale With Every Insert Or Move)
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

        // Avatar: Stacked People For A Group (R12), The Same As The List Row
        AvatarParts avatar = AvatarParts.Of(c);
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

    // A Bubble That Knows The Conversation, So It Can Tell Which Reaction Is Mine
    private MessageBubbleViewModel NewBubble(ChatMessage m) => new(m, _store.Find(ConversationId));

    // A Bubble Added Live (Sent, Or Arrived) Animates In, But Not Before The Chat's First Page Has Shown (The Chat's Own
    // Fade Covers That)
    private void MarkNewIfShown(MessageBubbleViewModel bubble)
    {
        if (_pageShown)
        {
            bubble.MarkNew();
        }
    }

    // Brings Everything That Depends On The Bubbles Up To Date After A Page, An Older Page, A Trim Or A Conversation
    // Change: Senders, Quotes, The Status Line And The Lock, On Every Bubble
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

    // The Same After One Bubble Was Added Or Changed Live: Only It And Its Neighbors Regroup, And Only The Last Two
    // Bubbles Can Gain Or Lose The Status Line. A Bubble No Longer Shown, Or A Conversation Whose People Changed Since
    // The Last Full Refresh, Refreshes Everything
    private void RefreshAround(MessageBubbleViewModel bubble)
    {
        ConversationInfo? c = _store.Find(ConversationId);
        int at              = Messages.IndexOf(bubble);
        if (at < 0 || !SameForBubbles(_bubblesFor, c))
        {
            RefreshBubbles();
            return;
        }

        bool encrypted = _store.IsEncrypted(ConversationId);
        RefreshGroups(c, at - 1, at + 1);
        RefreshQuotes(c);
        RefreshStatus(encrypted, Messages.Count - 2);
        RefreshPreviews(c);
        IsEncrypted = encrypted;
    }

    // Whether Two Copies Of The Conversation Show The Bubbles Alike: Group Or Not, Its People (Names, Colors, Which Is Me)
    // And Who I Send As
    private static bool SameForBubbles(ConversationInfo? a, ConversationInfo? b) =>
        ReferenceEquals(a, b)
        || (a is not null && b is not null && a.IsGroup == b.IsGroup && a.DefaultOutgoingId == b.DefaultOutgoingId && a.Participants.SequenceEqual(b.Participants));

    // Asks Once Per Link For Each Bubble's Preview Card, Only In An Inbox Chat: Never For Spam, Blocked Or Archived
    // Chats, Or One Not Known Yet, So A Stranger's Link Is Never Fetched. A Received Message's Link Waits For The Setting
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

    // GetAsync Never Throws; The Card Is Set On The UI Thread, If The Bubble Still Shows That Link
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

    // Groups The Bubbles As Google Messages Does: The Same Sender (Me, Or One Person), Each A Few Minutes After The Last.
    // In A Group Chat A Received Group Is Named Over Its First Bubble And Shows Its Sender's Avatar Beside Its Last. Only
    // The Bubbles From One Index To Another (Clamped To The List)
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

            // The Name Takes The Color Of The Sender's Letter Circle; The Conversation Says Which Reaction Is Mine
            b.SetGroup(first, last, lane, lane && first && name.Length > 0 ? name : null, AvatarParts.ColorOf(sender), ChatFormat.Initial(name), b.ParticipantId);
            b.SetConversation(c);
        }

        static bool SameGroup(MessageBubbleViewModel a, MessageBubbleViewModel b) =>
            a.FromMe == b.FromMe && (a.FromMe || a.ParticipantId == b.ParticipantId) && b.SentAt - a.SentAt <= GroupGap;
    }

    // Quotes Come From The Bubbles Shown, Else The Store (Memory Or Database), Else "Original message" With No Sender.
    // The Store Is Asked Once Per Bubble: Only While Its Quote Is Still Empty
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

    // Fills A Quote From The Store When It Has The Message; The Database Answers Off The UI Thread, So The Result Is Posted
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

    // The Caption Row Goes Under The Chat's Newest Bubble Only, Mine Or Received, With The Lock In An Encrypted Chat. When
    // It Is Mine: "Sent", "Delivered" Or "Read" (Google's Icon, Named So), Or The Clock While Sending. Only The Bubbles
    // From An Index On
    private void RefreshStatus(bool encrypted, int from)
    {
        MessageBubbleViewModel? newest = Messages.Count > 0 ? Messages[^1] : null;

        for (int i = Math.Max(0, from); i < Messages.Count; i++)
        {
            MessageBubbleViewModel b = Messages[i];
            bool isNewest            = b == newest;
            b.SetStatus(isNewest, isNewest && b.FromMe ? ChatFormat.StatusText(b.Status) : string.Empty, isNewest && encrypted);
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
