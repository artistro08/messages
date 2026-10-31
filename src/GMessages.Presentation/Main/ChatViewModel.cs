using System.Collections.ObjectModel;
using System.ComponentModel;
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
public sealed class ChatViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>How long "typing" shows without a stop from the phone (the Go connector's timeout, handlegmessages.go).</summary>
    public static readonly TimeSpan TypingTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long after telling the other side I'm typing before telling them again.</summary>
    public static readonly TimeSpan TypingResend = TimeSpan.FromSeconds(10);

    /// <summary>The longest gap between two messages from the same sender that still groups their bubbles.</summary>
    public static readonly TimeSpan GroupGap = TimeSpan.FromMinutes(3);

    /// <summary>The most the chips may hold together, in bytes: 200 MiB.</summary>
    public const long MaxTotalAttachmentBytes = 200L * 1024 * 1024;

    private readonly ChatStore _store;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;

    // Fetches Link Preview Cards; Null Means None
    private readonly LinkPreviewer? _previews;

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

    /// <summary>
    /// Creates an empty chat and starts listening to the store.
    /// </summary>
    /// <param name="store">Loads and sends messages and raises live updates.</param>
    /// <param name="conversationId">The conversation to show.</param>
    /// <param name="time">Gives the current time.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <param name="previews">Fetches link preview cards for an inbox chat's bubbles; null for none.</param>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="previews"/> is null.</exception>
    public ChatViewModel(ChatStore store, string conversationId, TimeProvider time, Action<Action> post, LinkPreviewer? previews = null)
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

        _typingTimer = time.CreateTimer(_ => _post(OnTypingTimeout), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _isEncrypted = store.IsEncrypted(conversationId);

        _store.MessageArrived      += OnMessageArrived;
        _store.Resynced            += OnResynced;
        _store.TypingChanged       += OnTypingChanged;
        _store.ConversationChanged += OnConversationChanged;
        ApplyConversation(_store.Find(conversationId));

        // A Chip Added Or Removed: Send May Turn On Or Off
        Attachments.CollectionChanged += (_, _) =>
        {
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

    /// <summary>Gets the conversation ID.</summary>
    public string ConversationId { get; }

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

    /// <summary>Gets the bubbles, oldest first.</summary>
    public ObservableCollection<MessageBubbleViewModel> Messages { get; } = [];

    /// <summary>Gets or sets the text being typed.</summary>
    /// <remarks>Typing in an RCS chat tells the other side, at most once every <see cref="TypingResend"/>.</remarks>
    public string Draft
    {
        get => _draft;
        set
        {
            _draft = value ?? string.Empty;
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
    /// Puts back a draft kept from the last time the chat was open, without telling the other side I'm typing.
    /// </summary>
    /// <param name="draft">The kept draft.</param>
    public void RestoreDraft(string draft)
    {
        _draft = draft ?? string.Empty;
        Raise(nameof(Draft));
        Raise(nameof(CanSend));
    }

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
    public bool IsActive { get; set; }

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
    // LOADING
    // =========================================================================

    /// <summary>
    /// Loads the newest page, merging it into the bubbles (unsent ones stay), and marks the conversation read when it was unread.
    /// </summary>
    /// <remarks>Call on the UI thread. With no bubbles yet, the cached page shows alongside the phone fetch: at once from memory, or from the database (read off the UI thread) unless the phone answered first. While a page is loading the call is remembered and runs once that load ends. When the load fails (phone not connected or not answering, a network error) the bubbles stay as they were.</remarks>
    /// <returns>A task that completes when the page is shown.</returns>
    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            _reloadPending = true;
            return;
        }

        IsLoading        = true;
        IsLoadingHistory = Messages.Count == 0;

        // Opened Before (Also Before A Restart): The Saved Copy Shows Alongside The Phone Fetch. A Copy In Memory Shows At
        // Once; The Database Is Read Off The UI Thread And Shows Only If The Phone's Page Hasn't Come First
        if (Messages.Count == 0)
        {
            _ = ShowSavedPageAsync(_store.CachedMessagesAsync(ConversationId), _phonePages);
        }

        try
        {
            // The Conversation May Not Be Cached Yet (Opened Right After Start Chat)
            ConversationInfo? conversation = _store.Find(ConversationId) ?? await _store.GetAsync(ConversationId, CancellationToken.None);
            ApplyConversation(conversation);

            (IReadOnlyList<ChatMessage> page, Cursor? older) = await _store.LoadMessagesAsync(ConversationId, null, CancellationToken.None);

            _older = older;
            _phonePages++;
            ShowPage(page);
            _fillOlder = true;

            // Opening An Unread Chat Reads It
            if (conversation is { Unread: true } && conversation.LatestMessageId.Length > 0)
            {
                await _store.MarkReadAsync(ConversationId, conversation.LatestMessageId, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not Connected, Not Answering Or A Network Error: The Resync After Reconnecting Reloads
            _store.LogFailure("load messages", ex);
            _fillOlder = false;
        }
        finally
        {
            IsLoading        = false;
            IsLoadingHistory = false;
        }

        await RunPendingReloadAsync();
    }

    // Shows The Saved Copy Unless A Phone Page Was Shown Since It Was Asked For; Over It The Big Ring Goes Away With No
    // Ring In Its Place. A Copy In Memory Completes At Once, So It Shows Before LoadAsync Awaits Anything
    private async Task ShowSavedPageAsync(Task<IReadOnlyList<ChatMessage>?> saved, int phonePages)
    {
        if (await saved is not { } page || _phonePages != phonePages)
        {
            return;
        }

        ShowPage(page);
        IsLoadingHistory = false;
    }

    /// <summary>
    /// Loads the page of older messages above the bubbles.
    /// </summary>
    /// <remarks>Call on the UI thread. Does nothing while a page is loading or when there are no older messages.</remarks>
    /// <returns>A task that completes when the page is shown.</returns>
    public async Task LoadOlderAsync()
    {
        if (IsLoading || _older is null)
        {
            return;
        }

        IsLoading      = true;
        IsLoadingOlder = true;
        try
        {
            (IReadOnlyList<ChatMessage> page, Cursor? older) = await _store.LoadMessagesAsync(ConversationId, _older, CancellationToken.None);

            _older = older;

            // The Page Is Oldest First, So Each Bubble Goes In Above The Ones Already Placed
            int at = 0;
            foreach (ChatMessage m in page.Where(m => m.Kind != MessageKind.Tombstone))
            {
                Messages.Insert(at++, NewBubble(m));
            }

            RefreshBubbles();
            _fillOlder = at > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not Connected, Not Answering Or A Network Error: Scrolling Again Tries Again, Filling Waits For The Next Good Load
            _store.LogFailure("load older messages", ex);
            _fillOlder = false;
        }
        finally
        {
            IsLoading      = false;
            IsLoadingOlder = false;
        }

        await RunPendingReloadAsync();
    }

    // A Resync Arrived Mid-Load: The Page Above May Be Stale, So Load Again
    private async Task RunPendingReloadAsync()
    {
        if (_reloadPending)
        {
            _reloadPending = false;
            await LoadAsync();
        }
    }

    // =========================================================================
    // SENDING
    // =========================================================================

    /// <summary>
    /// Sends the draft and the attachments: a bubble appears at once for each, and the draft and chips clear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A send that fails for any reason marks its bubble failed instead of throwing. Does nothing when <see cref="CanSend"/> is false.
    /// A reply to an RCS message carries its ID and the bubble shows the quote; a reply to SMS or MMS goes as plain
    /// text, as the Go connector sends it (<c>handlematrix.go</c>). The reply clears once sent.
    /// </para>
    /// <para>
    /// A recording waiting in the preview chip goes first, as <c>Voice message.m4a</c> (<c>audio/mp4</c>), its temp file
    /// deleted. Attachments go first, one at a time in order, each its own bubble showing its upload. Off RCS the text rides on
    /// the last one as its caption; RCS refuses captions on media, so there the text follows as its own message. A reply
    /// rides on the text, or on the first attachment when there is no text message. A failed attachment fails only its
    /// own bubble, which retries on its own.
    /// </para>
    /// <para>
    /// The picked files are read only now, each into one array. A file that can no longer be read stays as its chip,
    /// with <see cref="ChatCopy.AttachSendFailed"/> over the chips. An attachment's bytes are let go once its upload is
    /// done; only a failure before that keeps them for the retry.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes when the phone answered or the bubbles failed.</returns>
    public async Task SendAsync()
    {
        if (!CanSend)
        {
            return;
        }

        string text                         = string.IsNullOrWhiteSpace(Draft) ? string.Empty : Draft;
        List<PendingAttachment> attachments = [.. Attachments];
        bool is_rcs                         = _store.Find(ConversationId)?.IsRcs == true;
        string? reply_to                    = ReplyingTo is { IsRcs: true, Id.Length: > 0 } quoted ? quoted.Id : null;

        // Cleared Before Reading, So A Second Send Can't Send The Same Files Again
        Draft = string.Empty;
        Attachments.Clear();
        AttachError = string.Empty;
        CancelReply();

        // Read The Picked Files Now; One That Can't Be Read Goes Back As Its Chip
        List<(string Name, string Mime, byte[] Bytes, int Width, int Height)> ready = [];
        foreach (PendingAttachment attachment in attachments)
        {
            try
            {
                ready.Add((attachment.Name, attachment.Mime, await attachment.ReadAsync(), attachment.Width, attachment.Height));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _store.LogFailure("read attachment", ex);
                Attachments.Add(attachment);
                AttachError = ChatCopy.AttachSendFailed;
            }
        }

        // The Recording Goes First, As An Attachment (Its Temp File Is Deleted Now)
        if (Voice.Take() is { } voice)
        {
            ready.Insert(0, (VoiceDraft.FileName, VoiceDraft.Mime, voice, 0, 0));
        }

        bool caption_on_media = ready.Count > 0 && !is_rcs && text.Length > 0;
        bool text_apart       = text.Length > 0 && !caption_on_media;

        // Optimistic Bubbles: The Attachments (Showing Their Own Bytes), Then The Text. A Reply With No Text Message
        // Rides On The First Attachment
        List<MessageBubbleViewModel> bubbles = [];
        for (int i = 0; i < ready.Count; i++)
        {
            var a                               = ready[i];
            string caption                      = caption_on_media && i == ready.Count - 1 ? text : string.Empty;
            string? media_reply                 = i == 0 && !text_apart ? reply_to : null;
            MessageBubbleViewModel media_bubble = AddPending(MediaMessage(a.Name, a.Mime, a.Bytes, a.Width, a.Height, caption, media_reply, is_rcs));
            _outgoing[media_bubble]             = new OutgoingMedia(a.Bytes, a.Name, a.Mime);
            bubbles.Add(media_bubble);
        }

        if (text_apart)
        {
            bubbles.Add(AddPending(new ChatMessage(string.Empty, GmClient.NewTmpId(), ConversationId, string.Empty, _time.GetUtcNow(), true, MessageKind.Text, text, MessageStatusType.OutgoingSending)
            {
                ReplyToId = reply_to ?? string.Empty,
                IsRcs     = is_rcs,
            }));
        }

        RefreshBubbles();

        // One At A Time, So They Arrive In Order
        foreach (MessageBubbleViewModel bubble in bubbles)
        {
            await SendBubbleAsync(bubble);
        }
    }

    /// <summary>
    /// Adds a picked or pasted photo or file as a chip, unless <see cref="CanAttach"/> refuses its size.
    /// </summary>
    /// <remarks>
    /// The file is not read now: <paramref name="open"/> opens it for the chip's picture, and Send reads it. A refused
    /// file shows <see cref="AttachError"/> until the next pick (<see cref="ClearAttachError"/>), a removed chip or Send.
    /// </remarks>
    /// <param name="name">The file name the other side sees.</param>
    /// <param name="mime">The mime type.</param>
    /// <param name="size">The file's size in bytes.</param>
    /// <param name="open">Opens the file for reading.</param>
    /// <param name="width">A photo's width in pixels, or 0 when unknown.</param>
    /// <param name="height">A photo's height in pixels, or 0 when unknown.</param>
    /// <param name="thumbnail">Opens the file's thumbnail (a video's frame) for the chip, or null for none.</param>
    /// <returns>True when the chip was added.</returns>
    public bool Attach(string name, string mime, long size, Func<Task<Stream>> open, int width = 0, int height = 0, Func<Task<Stream>>? thumbnail = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(mime);
        ArgumentNullException.ThrowIfNull(open);

        if (!CanAttach(size))
        {
            return false;
        }

        Attachments.Add(new PendingAttachment(++_nextAttachmentId, name, mime, size, width, height, open, thumbnail));
        return true;
    }

    /// <summary>
    /// Adds bytes already in memory as a chip (see the overload that opens a file).
    /// </summary>
    /// <param name="name">The file name the other side sees.</param>
    /// <param name="mime">The mime type.</param>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="width">A photo's width in pixels, or 0 when unknown.</param>
    /// <param name="height">A photo's height in pixels, or 0 when unknown.</param>
    /// <returns>True when the chip was added.</returns>
    public bool Attach(string name, string mime, byte[] bytes, int width = 0, int height = 0)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return Attach(name, mime, bytes.LongLength, () => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)), width, height);
    }

    /// <summary>
    /// Tells whether a file of this size may be attached; when not, <see cref="AttachError"/> says it is too big.
    /// </summary>
    /// <param name="size">The file's size in bytes.</param>
    /// <returns>
    /// False when the file is over <see cref="ChatStore.MaxAttachmentBytes"/>, or would take the chips past
    /// <see cref="MaxTotalAttachmentBytes"/> together.
    /// </returns>
    public bool CanAttach(long size)
    {
        if (size > ChatStore.MaxAttachmentBytes)
        {
            AttachError = ChatCopy.AttachTooBig;
            return false;
        }

        if (Attachments.Sum(a => a.Size) + size > MaxTotalAttachmentBytes)
        {
            AttachError = ChatCopy.AttachTotalTooBig;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Hides the too-big note (a new pick starts).
    /// </summary>
    public void ClearAttachError() => AttachError = string.Empty;

    /// <summary>
    /// Says over the chips that a recording ended with Done couldn't be finished (<see cref="ChatCopy.RecordingFailed"/>),
    /// so it is never lost without a word; the note goes like the too-big note, or when the next recording starts.
    /// </summary>
    public void NoteRecordingFailed() => AttachError = ChatCopy.RecordingFailed;

    /// <summary>
    /// Drops a chip (its remove button).
    /// </summary>
    /// <param name="attachment">The attachment.</param>
    public void RemoveAttachment(PendingAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        Attachments.Remove(attachment);
        AttachError = string.Empty;
    }

    /// <summary>
    /// Starts a reply to a bubble: the reply chip shows until the next send or <see cref="CancelReply"/>.
    /// </summary>
    /// <param name="bubble">The bubble to reply to.</param>
    public void StartReply(MessageBubbleViewModel bubble)
    {
        ArgumentNullException.ThrowIfNull(bubble);

        ReplyingTo = bubble;
    }

    /// <summary>
    /// Drops the reply being written (the reply chip's close button).
    /// </summary>
    public void CancelReply() => ReplyingTo = null;

    /// <summary>
    /// Reacts to a bubble: adds the emoji, switches my other emoji to it, or removes it when it is already mine.
    /// </summary>
    /// <remarks>The bubble changes when the phone's echo arrives, not before. A failure is logged by type and swallowed.</remarks>
    /// <param name="bubble">The bubble reacted to.</param>
    /// <param name="emoji">The emoji.</param>
    /// <returns>A task that completes when the phone answered.</returns>
    public async Task ReactAsync(MessageBubbleViewModel bubble, string emoji)
    {
        ArgumentNullException.ThrowIfNull(bubble);
        ArgumentException.ThrowIfNullOrEmpty(emoji);

        // Not Reported By The Phone Yet: Nothing To React To
        if (bubble.Id.Length == 0)
        {
            return;
        }

        SendReactionRequest.Types.Action action = bubble.MyReaction switch
        {
            null                        => SendReactionRequest.Types.Action.Add,
            var mine when mine == emoji => SendReactionRequest.Types.Action.Remove,
            _                           => SendReactionRequest.Types.Action.Switch,
        };

        try
        {
            await _store.SendReactionAsync(ConversationId, bubble.Id, emoji, action, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _store.LogFailure("react", ex);
        }
    }

    /// <summary>
    /// Sends a failed bubble again with a new temporary ID: its text, or its attachment with its caption.
    /// </summary>
    /// <remarks>
    /// An attachment sends the same upload again (its media ID and key), and uploads again only when the first try
    /// failed before its upload was done. A bubble from before the chat opened sends the media its echo carried.
    /// </remarks>
    /// <param name="failed">The bubble that failed.</param>
    /// <returns>A task that completes when the phone answered or the bubble failed again.</returns>
    public async Task RetryAsync(MessageBubbleViewModel failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        if (!failed.IsFailed || !Messages.Contains(failed))
        {
            return;
        }

        failed.MarkSending(GmClient.NewTmpId());
        RefreshBubbles();
        await SendBubbleAsync(failed);
    }

    // Sends A Bubble's Attachment (Uploading It, With Its Caption), Or Its Text (As A Reply When It Quotes One); Any
    // Failure (Phone, Refusal, Network, Timeout, Decode, Too Big) Fails The Bubble Instead Of Escaping
    private async Task SendBubbleAsync(MessageBubbleViewModel bubble)
    {
        try
        {
            if (OutgoingOf(bubble) is { } outgoing)
            {
                // Upload Once; A Retry Sends The Same Upload Again
                if (outgoing.Uploaded is null)
                {
                    var progress      = new PostedProgress(share => _post(() => bubble.SetUploadShare(share)));
                    outgoing.Uploaded = await _store.UploadAttachmentAsync(outgoing.Bytes, outgoing.Name, outgoing.Mime, progress, CancellationToken.None);
                    outgoing.Bytes    = [];
                }

                string caption      = bubble.Caption;
                string? media_reply = bubble.ReplyToId.Length > 0 ? bubble.ReplyToId : null;
                await _store.SendUploadedMediaAsync(ConversationId, outgoing.Uploaded, caption.Length > 0 ? caption : null, bubble.TmpId, media_reply, CancellationToken.None);
                return;
            }

            string? reply_to = bubble.ReplyToId.Length > 0 ? bubble.ReplyToId : null;
            await _store.SendTextAsync(ConversationId, bubble.Text, bubble.TmpId, reply_to, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _store.LogFailure("send", ex);
            bubble.MarkFailed();
            RefreshBubbles();
        }
    }

    // Tells The Other Side I'm Typing; A Failure Is Only Logged
    private async Task SendTypingAsync()
    {
        try
        {
            await _store.SendTypingAsync(ConversationId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _store.LogFailure("send typing", ex);
        }
    }

    // =========================================================================
    // LIVE UPDATES
    // =========================================================================

    private void OnMessageArrived(object? sender, MessageArrivedEventArgs e)
    {
        ChatMessage m = e.Message;
        if (m.ConversationId != ConversationId)
        {
            return;
        }

        // A System Line Shows No Bubble, But Can Switch Encryption On Or Off (The Header's Lock And The Status Row's)
        if (m.Kind == MessageKind.Tombstone)
        {
            IsEncrypted = _store.IsEncrypted(ConversationId);
            RefreshStatus(IsEncrypted);
            return;
        }

        if (FindBubble(m) is { } bubble)
        {
            bubble.Update(m);
        }
        else
        {
            var added = NewBubble(m);
            MarkNewIfShown(added);
            Messages.Add(added);
        }

        RefreshBubbles();

        // Their Message Ends Their Typing
        if (!m.FromMe && m.ParticipantId == _typingParticipant)
        {
            StopTyping();
        }

        // Reading Along: Tell The Phone
        if (IsActive && !m.FromMe && m.Id.Length > 0)
        {
            _ = MarkReadAsync(m.Id);
        }
    }

    // Posted, So It Can Run After The Chat Closed: Then It Does Nothing
    private void OnResynced(object? sender, EventArgs e) => _post(() =>
    {
        if (!_disposed)
        {
            _ = LoadAsync();
        }
    });

    // This Conversation Changed (A New Message, Muted, Moved): Keep It Current, Raising Only A Mute Change, Since Every
    // Message Lands Here Too
    private void OnConversationChanged(object? sender, ConversationInfo c)
    {
        if (c.Id != ConversationId)
        {
            return;
        }

        Conversation = c;
        if (IsMuted != c.IsMuted)
        {
            IsMuted = c.IsMuted;
            Raise(nameof(IsMuted));
        }
    }

    // Typing In: A Start Shows The Dots For Up To 15 Seconds, A Stop Hides Them At Once
    private void OnTypingChanged(object? sender, TypingEventArgs e)
    {
        if (e.ConversationId != ConversationId)
        {
            return;
        }

        if (!e.IsTyping)
        {
            StopTyping();
            return;
        }

        _typingParticipant = e.ParticipantId;
        _typingUntil       = _time.GetUtcNow() + TypingTimeout;
        IsTyping           = true;
        _typingTimer.Change(TypingTimeout, Timeout.InfiniteTimeSpan);
    }

    // The Timer Fired; A Start That Came Since Moved The Deadline
    private void OnTypingTimeout()
    {
        // The Post Can Run After The Chat Closed: The Timer Is Disposed Then
        if (!_disposed && _time.GetUtcNow() >= _typingUntil)
        {
            StopTyping();
        }
    }

    private void StopTyping()
    {
        _typingParticipant = string.Empty;
        IsTyping           = false;
        _typingTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private async Task MarkReadAsync(string messageId)
    {
        try
        {
            await _store.MarkReadAsync(ConversationId, messageId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The Phone Learns It The Next Time The Chat Opens Unread
            _store.LogFailure("mark read", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _store.MessageArrived      -= OnMessageArrived;
        _store.Resynced            -= OnResynced;
        _store.TypingChanged       -= OnTypingChanged;
        _store.ConversationChanged -= OnConversationChanged;
        _typingTimer.Dispose();
        _outgoing.Clear();
        Voice.Cancel();
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
        return bubble;
    }

    // An Attachment's Pending Message: Its Bytes Ride As Inline Data, So The Bubble Shows (And Plays) Them Before Any
    // Upload
    private ChatMessage MediaMessage(string name, string mime, byte[] bytes, int width, int height, string caption, string? replyTo, bool isRcs)
    {
        MessageKind kind = MediaPart.KindOf(MediaFormatsMap.FromMime(mime));
        var media        = new MediaPart(string.Empty, [], string.Empty, [], bytes, name, bytes.LongLength, width, height, kind, mime);

        return new ChatMessage(string.Empty, GmClient.NewTmpId(), ConversationId, string.Empty, _time.GetUtcNow(), true, kind, caption, MessageStatusType.OutgoingSending)
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
        Messages.FirstOrDefault(b => m.Id.Length > 0 && b.Id == m.Id)
        ?? Messages.FirstOrDefault(b => m.TmpId.Length > 0 && b.TmpId == m.TmpId);

    // Shows The Newest Page In Place Of The Bubbles, Reusing (And Updating) The Ones Already Shown So The List Doesn't
    // Redraw Them. Unsent Bubbles, And Ones That Arrived While The Page Was On Its Way (Newer Than Anything In It),
    // Stay At The Bottom Unless The Page Holds Them; Older Pages Above Are Dropped
    private void ShowPage(IReadOnlyList<ChatMessage> page)
    {
        _pageShown = true;
        List<MessageBubbleViewModel> wanted = [];
        foreach (ChatMessage m in page.Where(m => m.Kind != MessageKind.Tombstone))
        {
            MessageBubbleViewModel? bubble = FindBubble(m);
            if (bubble is null || wanted.Contains(bubble))
            {
                bubble = NewBubble(m);
            }
            else
            {
                bubble.Update(m);
            }

            wanted.Add(bubble);
        }

        DateTimeOffset? newest = page.Count > 0 ? page.Max(m => m.Time) : null;
        wanted.AddRange([.. Messages
            .Where(b => !wanted.Contains(b) && (b.Id.Length == 0 || b.SentAt > newest))
            .OrderBy(b => b.SentAt)]);

        // Drop What The Page No Longer Holds, Then Put Each Bubble In Its Place
        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Messages[i]))
            {
                Messages.RemoveAt(i);
            }
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            int at = Messages.IndexOf(wanted[i]);
            if (at < 0)
            {
                Messages.Insert(i, wanted[i]);
            }
            else if (at != i)
            {
                Messages.Move(at, i);
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

    // Brings Everything That Depends On The Bubbles Up To Date After Any Change: Senders, Quotes, The Status Line And The Lock
    private void RefreshBubbles()
    {
        ConversationInfo? c = _store.Find(ConversationId);

        RefreshGroups(c);
        RefreshQuotes(c);
        RefreshStatus(_store.IsEncrypted(ConversationId));
        RefreshPreviews(c);
        IsEncrypted = _store.IsEncrypted(ConversationId);
    }

    // Asks Once Per Link For Each Bubble's Preview Card, Only In An Inbox Chat: Never For Spam, Blocked Or Archived
    // Chats, Or One Not Known Yet, So A Stranger's Link Is Never Fetched
    private void RefreshPreviews(ConversationInfo? c)
    {
        if (_previews is null || c is not { IsMainView: true })
        {
            return;
        }

        foreach (MessageBubbleViewModel b in Messages)
        {
            if (b.LinkUrl is { } url && b.PreviewAskedFor != url)
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
    // In A Group Chat A Received Group Is Named Over Its First Bubble And Shows Its Sender's Avatar Beside Its Last
    private void RefreshGroups(ConversationInfo? c)
    {
        bool is_group = c is { IsGroup: true };

        for (int i = 0; i < Messages.Count; i++)
        {
            MessageBubbleViewModel b = Messages[i];
            bool first               = i == 0 || !SameGroup(Messages[i - 1], b);
            bool last                = i == Messages.Count - 1 || !SameGroup(b, Messages[i + 1]);
            ParticipantInfo? sender  = c?.FindParticipant(b.ParticipantId);
            string name              = ChatFormat.SenderName(sender);
            bool lane                = is_group && !b.FromMe;

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
            if (Messages.FirstOrDefault(q => q.Id == b.ReplyToId) is { } shown)
            {
                b.SetQuote(ChatFormat.SenderName(c?.FindParticipant(shown.ParticipantId)), shown.Text);
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
                bubble.SetQuote(ChatFormat.SenderName(_store.Find(ConversationId)?.FindParticipant(quoted.ParticipantId)), text);
            }
        });
    }

    // The Caption Row Goes Under The Chat's Newest Bubble Only, Mine Or Received, With The Lock In An Encrypted Chat. When
    // It Is Mine: "Sent", "Delivered" Or "Read" (Google's Icon, Named So), Or The Clock While Sending
    private void RefreshStatus(bool encrypted)
    {
        MessageBubbleViewModel? newest = Messages.Count > 0 ? Messages[^1] : null;

        foreach (MessageBubbleViewModel b in Messages)
        {
            bool is_newest = b == newest;
            b.SetStatus(is_newest, is_newest && b.FromMe ? ChatFormat.StatusText(b.Status) : string.Empty, is_newest && encrypted);
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
