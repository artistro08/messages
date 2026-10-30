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

    // The First Link As Sent And Where It Sits In The Text: The Card's Link, Which The Card Replaces
    private TextLink? _link;

    // The Message As Last Filled, For The Full-Size Photo Request
    private ChatMessage _message = null!;

    // When The Chat Marked The Bubble New (Environment.TickCount64), Or 0; See TakeNew
    private long _newAt;

    // A New Bubble Animates In Only When It Shows This Soon After It Was Added (Not Later, Scrolled Into View)
    private const long NewWindowMs = 1000;

    // The Photo Bubble's Height Cap, And The Size A Tiny Photo Grows To, In Effective Pixels
    private const double MaxPhotoHeight = 360;
    private const double MinPhotoSide   = 120;

    // A Photo Without Dimensions Reserves A Square Of This Side
    private const double UnknownPhotoSide = 240;

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

    /// <summary>
    /// Gets the text in the bubble: <see cref="DisplayText"/>, except that while the link's preview card shows (owner ruling
    /// R5-12) the card takes the place of the link's address. Only that address goes, with the space it leaves (no double
    /// spaces or blank lines); other links stay. Empty when the message was only the link.
    /// </summary>
    /// <remarks>
    /// The message's own <see cref="Text"/> (copied by the bubble's Copy, read by screen readers through
    /// <see cref="ToString"/>) stays whole.
    /// </remarks>
    public string BodyText { get; private set; } = string.Empty;

    /// <summary>Gets whether the bubble shows any text (a link alone shows only its card).</summary>
    public bool HasBodyText => BodyText.Length > 0;

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

    /// <summary>Gets whether the bubble is in a group chat, where its reaction pill lists who reacted.</summary>
    public bool IsGroupChat => _conversation?.IsGroup == true;

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

    /// <summary>
    /// Gets the preview card's accessible name: the page title, then the link's host (the address isn't in the text
    /// while the card shows, so Narrator still hears where it goes). Empty without a card.
    /// </summary>
    public string PreviewName => Preview is null || LinkUrl is null ? string.Empty
        : Preview.Title.Length > 0 ? $"{Preview.Title}, {LinkUrl.Host}" : LinkUrl.Host;

    /// <summary>Gets the photo, video, voice message or file the message carries, or null.</summary>
    public MediaPart? Media { get; private set; }

    /// <summary>Gets whether the bubble shows a photo (a received or sent image).</summary>
    public bool HasPhoto { get; private set; }

    /// <summary>Gets whether the bubble shows a card for a video or a file.</summary>
    public bool HasFileCard { get; private set; }

    /// <summary>Gets whether the bubble shows a photo or a card, so its menu has Open and Save as.</summary>
    public bool HasMediaFile => HasPhoto || HasFileCard;

    /// <summary>
    /// Gets whether the photo or card may open in its default app, so its menu has Open. A program, script or installer
    /// (see <see cref="MediaStore.CanOpen"/>) never opens: its menu has only Save as.
    /// </summary>
    public bool CanOpenMedia => HasMediaFile && Media is { } media && MediaStore.CanOpen(media.Name, media.Mime);

    /// <summary>
    /// Gets whether the bubble menu has Copy: not for a photo, video or file sent without a caption, whose
    /// <see cref="Text"/> is only the kind label.
    /// </summary>
    public bool HasCopyText { get; private set; }

    /// <summary>Gets the card's file name: the sender's, or the kind label ("Video", "File") without one.</summary>
    public string FileName => Media is { Name.Length: > 0 } media ? media.Name : ChatFormat.KindLabel(Kind);

    /// <summary>Gets the size in bytes of the file the card shows, or 0 when unknown.</summary>
    public long FileSize => Media?.Size ?? 0;

    /// <summary>Gets the kind of message (Text, Photo, Video, File...).</summary>
    public MessageKind Kind { get; private set; }

    /// <summary>Gets whether the photo is still on its way from the phone (no media ID, thumbnail or inline data yet).</summary>
    public bool IsMediaPending => Media?.IsPending == true;

    /// <summary>Gets the photo's accessible name: its caption, else "Photo".</summary>
    public string PhotoName => HasBodyText ? Text : ChatCopy.Photo;

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
    /// Gives the size a photo bubble reserves before its image loads, so nothing moves when it arrives.
    /// </summary>
    /// <remarks>
    /// The photo's own <c>Dimensions</c>, scaled down to fit the bubble's width and 360 px of height with its aspect
    /// kept. A tiny photo (both sides under 120 px) grows until its longer side is 120 px. Unknown dimensions reserve a
    /// 240 px square (narrower when the bubble is), which the image then fills, cropped.
    /// </remarks>
    /// <param name="width">The photo's width in pixels, or 0 when unknown.</param>
    /// <param name="height">The photo's height in pixels, or 0 when unknown.</param>
    /// <param name="maxWidth">The widest the photo may be in the bubble, in effective pixels.</param>
    /// <returns>The width and height to reserve, in whole effective pixels.</returns>
    public static (double Width, double Height) PhotoSize(int width, int height, double maxWidth)
    {
        maxWidth = Math.Max(1, maxWidth);
        if (width <= 0 || height <= 0)
        {
            double side = Math.Floor(Math.Min(UnknownPhotoSide, maxWidth));
            return (side, side);
        }

        double scale = Math.Min(maxWidth / width, MaxPhotoHeight / height);
        if (Math.Max(width, height) < MinPhotoSide)
        {
            scale = Math.Min(scale, MinPhotoSide / Math.Max(width, height));
        }
        else
        {
            scale = Math.Min(scale, 1);
        }

        return (Math.Max(1, Math.Floor(width * scale)), Math.Max(1, Math.Floor(height * scale)));
    }

    /// <summary>
    /// Says whether the bubble should animate in, once: true for a bubble the chat added live (sent, or arrived while the
    /// chat was open) that shows within a second of being added. The flag clears, so a recycled container never replays it.
    /// </summary>
    /// <returns>True the first time a new bubble shows.</returns>
    public bool TakeNew()
    {
        bool is_new = _newAt != 0 && Environment.TickCount64 - _newAt <= NewWindowMs;
        _newAt      = 0;
        return is_new;
    }

    /// <summary>
    /// Asks the phone for the full-size photo when only its thumbnail came (see <see cref="MediaStore.RequestFullSizeAsync"/>).
    /// </summary>
    /// <param name="media">The media store.</param>
    /// <param name="again">True to ask again though asked before (a retry after the photo never came).</param>
    /// <returns>A task that completes when the phone accepted the request, or at once when nothing is asked.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="media"/> is null.</exception>
    /// <exception cref="MessagingUnavailableException">Not connected.</exception>
    public Task RequestFullSizeAsync(MediaStore media, bool again = false)
    {
        ArgumentNullException.ThrowIfNull(media);

        return media.RequestFullSizeAsync(_message, again);
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

    /// <summary>
    /// Lists who reacted, for a group chat's reaction pill: one row per person per emoji, the emoji in the order the pill
    /// ranks them (<see cref="ChatFormat.RankedEmoji"/>), people in the order the phone lists them.
    /// </summary>
    /// <remarks>
    /// My own reaction reads <see cref="ChatCopy.You"/>. Others are named from the conversation's participants: the full
    /// name, else the first name, else the number. Someone with none of those, or not in the conversation, is left out,
    /// so a raw participant ID never shows.
    /// </remarks>
    /// <returns>The rows; empty when nobody reacted.</returns>
    public IReadOnlyList<(string Emoji, string Name)> Reactors()
    {
        List<(string Emoji, string Name)> rows = [];
        foreach (string emoji in ChatFormat.RankedEmoji(_reactions))
        {
            foreach (string participant_id in _reactions.Where(r => r.Emoji == emoji).SelectMany(r => r.ParticipantIds))
            {
                string name = IsMine(participant_id) ? ChatCopy.You : NameOf(_conversation?.FindParticipant(participant_id));
                if (name.Length > 0)
                {
                    rows.Add((emoji, name));
                }
            }
        }

        return rows;
    }

    // A Participant's Name: Full, First, Then The Number; Empty For None (Or An Unknown Participant)
    private static string NameOf(ParticipantInfo? p) =>
        p is null ? string.Empty
        : p.FullName.Length > 0 ? p.FullName
        : p.FirstName.Length > 0 ? p.FirstName
        : !string.IsNullOrEmpty(p.FormattedNumber) ? p.FormattedNumber
        : p.Number;

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

        Preview  = preview;
        BodyText = BodyOf(Text, DisplayText, _link, preview);
        PropertyChanged?.Invoke(this, AllChanged);
    }

    // Added Live (Sent, Or Arrived In The Open Chat): Animates In When It First Shows
    internal void MarkNew() => _newAt = Environment.TickCount64;

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
        Text          = ChatFormat.ShownText(m);
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
        _link         = m.Kind == MessageKind.Text ? LinkPreviewer.FirstTextLink(m.Text) : null;
        LinkUrl       = _link?.Url;
        Preview       = Preview?.Url == LinkUrl ? Preview : null;
        _message      = m;
        Kind          = m.Kind;
        Media         = m.Media;
        HasPhoto      = m.Media is not null && m.Kind == MessageKind.Photo;
        HasFileCard   = m.Media is not null && m.Kind is MessageKind.Video or MessageKind.File;
        HasCopyText   = m.Media is null || m.Text.Length > 0;

        // A Photo Or Card Shows Only Its Caption Under It, Not The Kind Label
        BodyText      = HasMediaFile && m.Text.Length == 0 ? string.Empty : BodyOf(Text, DisplayText, _link, Preview);
    }

    // The Text Shown In The Bubble: Without The Card's Link While The Card Shows. The Space On Either Side Of The Address
    // Goes With It; What Was On Each Side Joins With One Space, Or One Line Break When Either Side Was Its Own Line, And
    // Nothing Before Punctuation ("see https://x.com, ok" Reads "see, ok")
    private static string BodyOf(string text, string displayText, TextLink? link, LinkPreview? preview)
    {
        if (preview is null || link is not { } l)
        {
            return displayText;
        }

        string before = text[..l.Start].TrimEnd(' ', '\t');
        string after  = text[(l.Start + l.Length)..].TrimStart(' ', '\t');
        bool line_end = before.EndsWith('\n');
        bool line_new = after.StartsWith('\n') || after.StartsWith("\r\n", StringComparison.Ordinal);

        // A Line That Was Only The Link Goes With Its Line Break
        if (line_end && line_new)
        {
            after = after[(after[0] == '\r' ? 2 : 1)..];
        }

        string joint = before.Length == 0 || after.Length == 0 || line_end || line_new || char.IsPunctuation(after[0]) ? string.Empty : " ";
        return ChatFormat.WithColorEmoji((before + joint + after).Trim());
    }

    private string? FindMyReaction() => _reactions.FirstOrDefault(r => r.ParticipantIds.Any(IsMine))?.Emoji;

    // Mine: Any Participant Marked As Me, Or The ID The Conversation Sends As
    private bool IsMine(string participantId) =>
        participantId.Length > 0 && _conversation is { } c && (participantId == c.DefaultOutgoingId || c.FindParticipant(participantId)?.IsMe == true);
}
