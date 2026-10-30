using System.ComponentModel;
using System.Globalization;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// One message bubble in the chat.
/// </summary>
/// <remarks>
/// Every change raises <see cref="PropertyChanged"/> once with an empty property name, which tells <c>x:Bind</c> to
/// re-read every property. Call everything on the UI thread.
/// </remarks>
public sealed class MessageBubbleViewModel : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    // The Conversation, For Which Reaction Is Mine; Null When Not Known
    private ConversationInfo? _conversation;

    // The Message's Reactions, Kept So MyReaction Can Be Worked Out Again When The Conversation Arrives
    private IReadOnlyList<ReactionInfo> _reactions = [];

    /// <summary>
    /// Creates the bubble from a message.
    /// </summary>
    /// <param name="m">The message.</param>
    /// <param name="conversation">The conversation it belongs to, which says which reaction is mine; null when not known.</param>
    public MessageBubbleViewModel(ChatMessage m, ConversationInfo? conversation = null)
    {
        ArgumentNullException.ThrowIfNull(m);

        _conversation = conversation;
        Fill(m);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the message ID, or empty until the phone has reported the message.</summary>
    public string Id { get; private set; } = string.Empty;

    /// <summary>Gets the temporary ID the app made when sending, or empty.</summary>
    public string TmpId { get; private set; } = string.Empty;

    /// <summary>Gets the sender's participant ID.</summary>
    public string ParticipantId { get; private set; } = string.Empty;

    /// <summary>Gets the text, or the kind label ("Photo", "Video"...) for a message without text.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Gets the text as shown: <see cref="Text"/> with text-style emoji in color (see <see cref="ChatFormat.WithColorEmoji"/>).</summary>
    public string DisplayText { get; private set; } = string.Empty;

    /// <summary>Gets whether the owner sent the message.</summary>
    public bool FromMe { get; private set; }

    /// <summary>Gets the sender's name when this is the first bubble of a received run in a group, else null.</summary>
    public string? SenderName { get; private set; }

    /// <summary>Gets the color of the sender's name: the same as their letter circle, as opaque ARGB.</summary>
    public uint SenderColor { get; private set; } = AvatarParts.FallbackColor;

    /// <summary>Gets whether <see cref="SenderName"/> is set (for bindings, which skip their function when an argument is null).</summary>
    public bool HasSenderName => SenderName is not null;

    /// <summary>Gets whether the bubble starts its group (the same sender, a few minutes apart at most); set by the chat.</summary>
    public bool IsFirstInGroup { get; private set; } = true;

    /// <summary>Gets whether the bubble ends its group; set by the chat.</summary>
    public bool IsLastInGroup { get; private set; } = true;

    /// <summary>Gets whether the bubble keeps a lane on its left for the sender's avatar (received, in a group chat).</summary>
    public bool HasAvatarLane { get; private set; }

    /// <summary>Gets whether the sender's avatar shows beside the bubble (the last of a received group, in a group chat).</summary>
    public bool ShowsAvatar { get; private set; }

    /// <summary>Gets the avatar's letter while it shows, else empty.</summary>
    public string AvatarInitial { get; private set; } = string.Empty;

    /// <summary>Gets the participant whose photo covers the avatar while it shows, else empty.</summary>
    public string AvatarPhotoId { get; private set; } = string.Empty;

    /// <summary>Gets when the message was sent.</summary>
    public DateTimeOffset SentAt { get; private set; }

    /// <summary>Gets the time of day the message was sent.</summary>
    public string Time { get; private set; } = string.Empty;

    /// <summary>Gets whether the time shows under the bubble: hidden until the bubble is clicked (see <see cref="ToggleTime"/>).</summary>
    public bool IsTimeShown { get; private set; }

    /// <summary>Gets whether this is the chat's newest bubble, which shows the caption row until clicked; set by the chat.</summary>
    public bool IsNewest { get; private set; }

    /// <summary>Gets whether a click hid the newest bubble's caption row (see <see cref="ToggleTime"/>).</summary>
    public bool IsCaptionHidden { get; private set; }

    /// <summary>Gets whether the message was not sent.</summary>
    public bool IsFailed { get; private set; }

    /// <summary>Gets whether the message is still being sent.</summary>
    public bool IsSending { get; private set; }

    /// <summary>Gets whether the message went over RCS (chat) rather than SMS or MMS.</summary>
    public bool IsRcs { get; private set; }

    /// <summary>
    /// Gets whether the bubble takes react, reply and its menu from the hover strip: the phone has reported it, and it isn't a
    /// tombstone, still being sent or failed.
    /// </summary>
    public bool CanAct => Id.Length > 0 && !IsTombstone && !IsSending && !IsFailed;

    /// <summary>Gets the bubble's accessible item type: "Chat message" for RCS, "Text message" for SMS.</summary>
    public string ItemTypeName => IsRcs ? ChatCopy.ChatMessage : ChatCopy.TextMessage;

    /// <summary>Gets the status of my newest bubble ("Sent", "Delivered", "Read"), or empty; set by the chat. The icon's accessible name.</summary>
    public string StatusText { get; private set; } = string.Empty;

    /// <summary>Gets the delivery icon in the caption row: when the newest bubble is mine, the clock while sending, else its status.</summary>
    public MessageStatusIcon StatusIcon => !IsNewest || !FromMe ? MessageStatusIcon.None : IsSending ? MessageStatusIcon.Sending : StatusText.Length == 0 ? MessageStatusIcon.None : Status switch
    {
        MessageStatusType.OutgoingDelivered => MessageStatusIcon.Delivered,
        MessageStatusType.OutgoingDisplayed => MessageStatusIcon.Read,
        _                                   => MessageStatusIcon.Sent,
    };

    /// <summary>
    /// Gets whether the caption row shows under the bubble: only under the chat's newest bubble (mine or received), unless
    /// it failed to send (the retry link shows) or a click hid it. Mine shows the time, the delivery icon and the lock; a
    /// received one the time and the lock.
    /// </summary>
    public bool ShowsStatusRow => IsNewest && !IsFailed && !IsCaptionHidden;

    /// <summary>Gets whether the lock shows in the caption row: the newest bubble of an end-to-end encrypted chat.</summary>
    public bool ShowsLock { get; private set; }

    /// <summary>Gets whether the dot after the caption row's time shows: only when the delivery icon or the lock follows it.</summary>
    public bool ShowsCaptionDot => StatusIcon != MessageStatusIcon.None || ShowsLock;

    /// <summary>Gets the reaction chip text, such as "👍😂 3", or empty.</summary>
    public string ReactionsText { get; private set; } = string.Empty;

    /// <summary>Gets whether anyone reacted.</summary>
    public bool HasReactions => ReactionsText.Length > 0;

    /// <summary>Gets my reaction's emoji, or null when I didn't react.</summary>
    public string? MyReaction { get; private set; }

    /// <summary>Gets whether I reacted (for bindings, which skip their function when an argument is null).</summary>
    public bool HasMyReaction => MyReaction is not null;

    /// <summary>Gets the ID of the message this one replies to, or empty.</summary>
    public string ReplyToId { get; private set; } = string.Empty;

    /// <summary>Gets the quoted message's sender name; set by the chat.</summary>
    public string QuoteSender { get; private set; } = string.Empty;

    /// <summary>Gets the quoted message's text, or "Original message" when it isn't loaded; set by the chat.</summary>
    public string QuoteText { get; private set; } = string.Empty;

    /// <summary>Gets whether the message replies to another one.</summary>
    public bool HasQuote => ReplyToId.Length > 0;

    /// <summary>Gets the first web link in the text, or null.</summary>
    public Uri? LinkUrl { get; private set; }

    /// <summary>Gets the link's preview card, or null; set by the chat once fetched.</summary>
    public LinkPreview? Preview { get; private set; }

    /// <summary>Gets whether the preview card shows.</summary>
    public bool HasPreview => Preview is not null;

    // The Link The Chat Asked A Preview For, So It Asks Once Per Link
    internal Uri? PreviewAskedFor { get; set; }

    // The Phone's Delivery Status, For The Chat's Status Line
    internal MessageStatusType Status { get; private set; }

    // A Tombstone (Such As "Now Chatting With End-To-End Encryption"): Nothing To React Or Reply To
    private bool IsTombstone { get; set; }

    /// <summary>
    /// Copies new message details into the bubble (an echo or a status change).
    /// </summary>
    /// <remarks>An empty ID or temporary ID in <paramref name="m"/> keeps the one the bubble already has.</remarks>
    /// <param name="m">The message.</param>
    public void Update(ChatMessage m)
    {
        ArgumentNullException.ThrowIfNull(m);

        Fill(m);
        PropertyChanged?.Invoke(this, AllChanged);
    }

    /// <summary>
    /// Gives the text, followed by the "Not sent" note when the send failed.
    /// </summary>
    /// <remarks>A XAML list item reads its item's <c>ToString()</c> as its UI Automation name, so screen readers hear this.</remarks>
    /// <returns>The text to read out.</returns>
    public override string ToString() => IsFailed ? $"{Text}, {ChatCopy.NotSentRetry}" : Text;

    /// <summary>
    /// Shows the time under the bubble, or hides it again (a click on the bubble). On the newest bubble it hides its caption
    /// row instead, or shows it again.
    /// </summary>
    public void ToggleTime()
    {
        if (IsNewest)
        {
            IsCaptionHidden = !IsCaptionHidden;
        }
        else
        {
            IsTimeShown = !IsTimeShown;
        }

        PropertyChanged?.Invoke(this, AllChanged);
    }

    // A Bubble Made Before Its Conversation Was Cached Learns Which Reaction Is Mine
    internal void SetConversation(ConversationInfo? conversation)
    {
        if (conversation is null || ReferenceEquals(_conversation, conversation))
        {
            return;
        }

        _conversation = conversation;
        string? mine  = FindMyReaction();
        if (MyReaction == mine)
        {
            return;
        }

        MyReaction = mine;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    // The Newest Bubble Shows The Caption Row; One That Stops Being The Newest Forgets A Click That Hid It, And One That
    // Becomes The Newest Forgets A Click That Showed Its Time (Else Hiding The Caption Would Show The Footer Time)
    internal void SetStatus(bool isNewest, string text, bool showsLock)
    {
        bool hidden     = isNewest && IsCaptionHidden;
        bool time_shown = !isNewest && IsTimeShown;
        if (IsNewest == isNewest && StatusText == text && ShowsLock == showsLock && IsCaptionHidden == hidden && IsTimeShown == time_shown)
        {
            return;
        }

        IsNewest        = isNewest;
        IsCaptionHidden = hidden;
        IsTimeShown     = time_shown;
        StatusText      = text;
        ShowsLock       = showsLock;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    // Where The Bubble Sits In Its Group, Its Name Over The First And Its Avatar Beside The Last (Group Chats Only)
    internal void SetGroup(bool first, bool last, bool avatarLane, string? senderName, uint senderColor, string avatarInitial, string avatarPhotoId)
    {
        bool shows_avatar = avatarLane && last;
        string initial    = shows_avatar ? avatarInitial : string.Empty;
        string photo_id   = shows_avatar ? avatarPhotoId : string.Empty;
        if (IsFirstInGroup == first && IsLastInGroup == last && HasAvatarLane == avatarLane && SenderName == senderName && SenderColor == senderColor
            && AvatarInitial == initial && AvatarPhotoId == photo_id)
        {
            return;
        }

        IsFirstInGroup = first;
        IsLastInGroup  = last;
        HasAvatarLane  = avatarLane;
        ShowsAvatar    = shows_avatar;
        SenderName     = senderName;
        SenderColor    = senderColor;
        AvatarInitial  = initial;
        AvatarPhotoId  = photo_id;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    // The Quote Shows Text-Style Emoji In Color, Like The Bubble
    internal void SetQuote(string sender, string text)
    {
        string shown = ChatFormat.WithColorEmoji(text);
        if (QuoteSender == sender && QuoteText == shown)
        {
            return;
        }

        QuoteSender = sender;
        QuoteText   = shown;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    internal void SetPreview(LinkPreview preview)
    {
        if (ReferenceEquals(Preview, preview))
        {
            return;
        }

        Preview = preview;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    internal void MarkFailed()
    {
        IsFailed  = true;
        IsSending = false;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    internal void MarkSending(string tmpId)
    {
        TmpId     = tmpId;
        IsFailed  = false;
        IsSending = true;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    private void Fill(ChatMessage m)
    {
        Id            = m.Id.Length > 0 ? m.Id : Id;
        TmpId         = m.TmpId.Length > 0 ? m.TmpId : TmpId;
        ParticipantId = m.ParticipantId;
        Text          = m.Kind == MessageKind.Text ? m.Text : ChatFormat.KindLabel(m.Kind);
        DisplayText   = ChatFormat.WithColorEmoji(Text);
        FromMe        = m.FromMe;
        SentAt        = m.Time;
        Time          = m.Time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        IsFailed      = m.IsFailed;
        IsSending     = m.Status == MessageStatusType.OutgoingSending;
        Status        = m.Status;
        IsTombstone   = m.Kind == MessageKind.Tombstone;
        IsRcs         = m.IsRcs;
        ReactionsText = ChatFormat.ReactionSummary(m.Reactions);
        _reactions    = m.Reactions;
        MyReaction    = FindMyReaction();
        ReplyToId     = m.ReplyToId;
        LinkUrl       = m.Kind == MessageKind.Text ? LinkPreviewer.FirstLink(m.Text) : null;
        Preview       = Preview?.Url == LinkUrl ? Preview : null;
    }

    private string? FindMyReaction() => _reactions.FirstOrDefault(r => r.ParticipantIds.Any(IsMine))?.Emoji;

    // Mine: Any Participant Marked As Me, Or The ID The Conversation Sends As
    private bool IsMine(string participantId) =>
        participantId.Length > 0 && _conversation is { } c && (participantId == c.DefaultOutgoingId || c.FindParticipant(participantId)?.IsMe == true);
}
