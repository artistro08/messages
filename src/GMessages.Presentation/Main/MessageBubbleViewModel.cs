using System.ComponentModel;
using System.Globalization;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// Shows 1 message bubble in the chat.
/// </summary>
/// <remarks>
/// A change raises <see cref="PropertyChanged"/> 1 time for each property whose value it moved. Computed properties,
/// such as <see cref="HasPreview"/> and <see cref="ShowsStatusRow"/>, also raise. Thus <c>x:Bind</c> reads only those
/// bindings again. Call each member on the UI thread.
/// </remarks>
public sealed class MessageBubbleViewModel : INotifyPropertyChanged
{
    // The list holds each public property with its getter. A change compares them before and after, and raises only
    // the properties that moved. The empty name made x:Bind read all of the about 60 bindings of a bubble again on
    // each change. A new public property must go here too, and a test checks it.
    internal static readonly (string Name, Func<MessageBubbleViewModel, object?> Get)[] Watched =
    [
        (nameof(Id), b => b.Id),
        (nameof(TmpId), b => b.TmpId),
        (nameof(PartKey), b => b.PartKey),
        (nameof(ParticipantId), b => b.ParticipantId),
        (nameof(Text), b => b.Text),
        (nameof(DisplayText), b => b.DisplayText),
        (nameof(BodyText), b => b.BodyText),
        (nameof(HasBodyText), b => b.HasBodyText),
        (nameof(IsEmojiOnly), b => b.IsEmojiOnly),
        (nameof(FromMe), b => b.FromMe),
        (nameof(IsSystemLine), b => b.IsSystemLine),
        (nameof(SenderName), b => b.SenderName),
        (nameof(SenderColor), b => b.SenderColor),
        (nameof(HasSenderName), b => b.HasSenderName),
        (nameof(IsFirstInGroup), b => b.IsFirstInGroup),
        (nameof(IsLastInGroup), b => b.IsLastInGroup),
        (nameof(HasAvatarLane), b => b.HasAvatarLane),
        (nameof(ShowsAvatar), b => b.ShowsAvatar),
        (nameof(AvatarInitial), b => b.AvatarInitial),
        (nameof(AvatarPhotoId), b => b.AvatarPhotoId),
        (nameof(SentAt), b => b.SentAt),
        (nameof(Time), b => b.Time),
        (nameof(IsTimeShown), b => b.IsTimeShown),
        (nameof(IsNewest), b => b.IsNewest),
        (nameof(IsCaptionHidden), b => b.IsCaptionHidden),
        (nameof(IsFailed), b => b.IsFailed),
        (nameof(IsSending), b => b.IsSending),
        (nameof(IsRcs), b => b.IsRcs),
        (nameof(CanAct), b => b.CanAct),
        (nameof(ItemTypeName), b => b.ItemTypeName),
        (nameof(StatusText), b => b.StatusText),
        (nameof(StatusIcon), b => b.StatusIcon),
        (nameof(ShowsStatusRow), b => b.ShowsStatusRow),
        (nameof(ShowsLock), b => b.ShowsLock),
        (nameof(ShowsSmsLabel), b => b.ShowsSmsLabel),
        (nameof(ShowsCaptionDot), b => b.ShowsCaptionDot),
        (nameof(ReactionsText), b => b.ReactionsText),
        (nameof(HasReactions), b => b.HasReactions),
        (nameof(MyReaction), b => b.MyReaction),
        (nameof(HasMyReaction), b => b.HasMyReaction),
        (nameof(IsGroupChat), b => b.IsGroupChat),
        (nameof(ReplyToId), b => b.ReplyToId),
        (nameof(QuoteSender), b => b.QuoteSender),
        (nameof(QuoteText), b => b.QuoteText),
        (nameof(HasQuote), b => b.HasQuote),
        (nameof(QuoteMedia), b => b.QuoteMedia),
        (nameof(HasQuoteMedia), b => b.HasQuoteMedia),
        (nameof(QuoteIsLabel), b => b.QuoteIsLabel),
        (nameof(MediaLabelIsLabel), b => b.MediaLabelIsLabel),
        (nameof(ReplyChipText), b => b.ReplyChipText),
        (nameof(LinkUrl), b => b.LinkUrl),
        (nameof(Preview), b => b.Preview),
        (nameof(HasPreview), b => b.HasPreview),
        (nameof(PreviewName), b => b.PreviewName),
        (nameof(Media), b => b.Media),
        (nameof(HasPhoto), b => b.HasPhoto),
        (nameof(HasFileCard), b => b.HasFileCard),
        (nameof(HasVideo), b => b.HasVideo),
        (nameof(FillsBubble), b => b.FillsBubble),
        (nameof(HasVoice), b => b.HasVoice),
        (nameof(HasMediaFile), b => b.HasMediaFile),
        (nameof(CanOpenMedia), b => b.CanOpenMedia),
        (nameof(HasCopyText), b => b.HasCopyText),
        (nameof(IsLocalMedia), b => b.IsLocalMedia),
        (nameof(IsUploading), b => b.IsUploading),
        (nameof(UploadPercent), b => b.UploadPercent),
        (nameof(NotSentText), b => b.NotSentText),
        (nameof(FileName), b => b.FileName),
        (nameof(Kind), b => b.Kind),
        (nameof(PhotoName), b => b.PhotoName),
        (nameof(VideoName), b => b.VideoName),
    ];

    private static readonly PropertyChangedEventArgs[] WatchedArgs = [.. Watched.Select(w => new PropertyChangedEventArgs(w.Name))];

    // The conversation tells which reaction is mine. Null means that it is not known.
    private ConversationInfo? _conversation;

    // The bubble keeps the reactions, so MyReaction can change when the conversation arrives.
    private IReadOnlyList<ReactionInfo> _reactions = [];

    // The bubble keeps the first link as sent, with its place in the text. The card replaces this link.
    private TextLink? _link;

    // Fill keeps the last message here. The full-size photo request needs it.
    private ChatMessage _message = null!;

    // MarkNew keeps its Environment.TickCount64 value here for TakeNew. The value 0 means no mark.
    private long _newAt;

    // A new bubble animates in only when it shows this soon after the add. A later scroll into view does not animate
    // it.
    private const long NewWindowMs = 1000;

    // A photo bubble has this height limit, and a speck of a photo grows to this size. Both are in effective pixels.
    private const double MaxPhotoHeight = 360;
    private const double MinPhotoSide   = 48;

    // A photo with no dimensions reserves a square with this side.
    private const double UnknownPhotoSide = 240;

    /// <summary>
    /// Creates the bubble from a message.
    /// </summary>
    /// <param name="m">The message.</param>
    /// <param name="conversation">
    /// The conversation of the message, which tells which reaction is mine, or null when not known.
    /// </param>
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

    /// <summary>Gets the temporary ID that the app made for the send, or empty.</summary>
    public string TmpId { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the end of the AutomationIds of the bubble: the message ID, or the temporary ID until the phone reports the
    /// message. It raises only when it changes, so the view builds the IDs again only then.
    /// </summary>
    public string PartKey => Id.Length > 0 ? Id : TmpId;

    /// <summary>Gets the participant ID of the sender.</summary>
    public string ParticipantId { get; private set; } = string.Empty;

    /// <summary>Gets the text, or the kind label, such as "Photo" or "Video", for a message with no text.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the text as it shows: <see cref="Text"/> with text-style emoji in color. See
    /// <see cref="ChatFormat.WithColorEmoji"/>.
    /// </summary>
    public string DisplayText { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the text in the bubble: <see cref="DisplayText"/>, except while the preview card of the link shows. Then
    /// the card takes the place of the address of the link, by owner ruling R5-12.
    /// </summary>
    /// <remarks>
    /// Only that address goes, with the space that it leaves, so no double spaces or blank lines stay. Other links
    /// stay. The text is empty when the message was only the link. The <see cref="Text"/> of the message stays whole.
    /// The Copy of the bubble copies it, and screen readers read it through <see cref="ToString"/>.
    /// </remarks>
    public string BodyText { get; private set; } = string.Empty;

    /// <summary>Gets whether the bubble shows any text. A link alone shows only its card.</summary>
    public bool HasBodyText => BodyText.Length > 0;

    /// <summary>
    /// Gets whether the message is only emoji with no quote, as <see cref="ChatFormat.IsEmojiOnly"/> tells. The bubble
    /// then shows them big, with no fill or edge.
    /// </summary>
    /// <remarks><see cref="Fill"/> computes it 1 time for each message.</remarks>
    public bool IsEmojiOnly { get; private set; }

    /// <summary>Gets whether the owner sent the message.</summary>
    public bool FromMe { get; private set; }

    /// <summary>Gets the sender name on the first bubble of a received run in a group, else null.</summary>
    public string? SenderName { get; private set; }

    /// <summary>Gets the color of the sender name as opaque ARGB. It is the color of their letter circle.</summary>
    public uint SenderColor { get; private set; } = AvatarParts.FallbackColor;

    /// <summary>
    /// Gets whether <see cref="SenderName"/> is set. A binding skips its function when an argument is null.
    /// </summary>
    public bool HasSenderName => SenderName is not null;

    /// <summary>
    /// Gets whether the bubble starts its group. A group has 1 sender, with a few minutes between bubbles at most. The
    /// chat sets it.
    /// </summary>
    public bool IsFirstInGroup { get; private set; } = true;

    /// <summary>Gets whether the bubble ends its group. The chat sets it.</summary>
    public bool IsLastInGroup { get; private set; } = true;

    /// <summary>
    /// Gets whether the bubble keeps a lane on its left for the avatar of the sender. A received bubble in a group chat
    /// keeps it.
    /// </summary>
    public bool HasAvatarLane { get; private set; }

    /// <summary>
    /// Gets whether the avatar of the sender shows beside the bubble. The last bubble of a received group in a group
    /// chat shows it.
    /// </summary>
    public bool ShowsAvatar { get; private set; }

    /// <summary>Gets the avatar's letter while it shows, else empty.</summary>
    public string AvatarInitial { get; private set; } = string.Empty;

    /// <summary>Gets the participant whose photo covers the avatar while it shows, else empty.</summary>
    public string AvatarPhotoId { get; private set; } = string.Empty;

    /// <summary>Gets when the message was sent.</summary>
    public DateTimeOffset SentAt { get; private set; }

    /// <summary>Gets the time of day the message was sent.</summary>
    public string Time { get; private set; } = string.Empty;

    /// <summary>
    /// Gets whether the time shows under the bubble. It stays hidden until a click on the bubble. See
    /// <see cref="ToggleTime"/>.
    /// </summary>
    public bool IsTimeShown { get; private set; }

    /// <summary>
    /// Gets whether this is the newest bubble of the chat, which shows the caption row until a click. The chat sets it.
    /// </summary>
    public bool IsNewest { get; private set; }

    /// <summary>Gets whether a click hid the caption row of the newest bubble. See <see cref="ToggleTime"/>.</summary>
    public bool IsCaptionHidden { get; private set; }

    /// <summary>Gets whether the send of the message failed.</summary>
    public bool IsFailed { get; private set; }

    /// <summary>Gets whether the send of the message is still in progress.</summary>
    public bool IsSending { get; private set; }

    /// <summary>Gets whether the message went over RCS chat, not SMS or MMS.</summary>
    public bool IsRcs { get; private set; }

    /// <summary>
    /// Gets whether the hover strip gives the bubble react, reply and its menu. The phone must have reported the
    /// message, and the bubble must not be a tombstone, a send in progress or failed.
    /// </summary>
    public bool CanAct => Id.Length > 0 && !IsSystemLine && !IsSending && !IsFailed;

    /// <summary>Gets the accessible item type of the bubble: "Chat message" for RCS, "Text message" for SMS.</summary>
    public string ItemTypeName => IsRcs ? ChatCopy.ChatMessage : ChatCopy.TextMessage;

    /// <summary>
    /// Gets the status of my newest bubble, such as "Sent", "Delivered" or "Read", or empty. The chat sets it, and the
    /// icon uses it as its accessible name.
    /// </summary>
    public string StatusText { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the delivery icon in the caption row when the newest bubble is mine: the clock during the send, else its
    /// status. A bubble sent as SMS gets no icon, and <see cref="ShowsSmsLabel"/> shows in its place.
    /// </summary>
    public MessageStatusIcon StatusIcon => !IsNewest || !FromMe ? MessageStatusIcon.None : IsSending ? MessageStatusIcon.Sending : StatusText.Length == 0 || !IsRcs ? MessageStatusIcon.None : Status switch
    {
        MessageStatusType.OutgoingDelivered => MessageStatusIcon.Delivered,
        MessageStatusType.OutgoingDisplayed => MessageStatusIcon.Read,
        _                                   => MessageStatusIcon.Sent,
    };

    /// <summary>
    /// Gets whether the caption row shows under the bubble. It shows only under the newest bubble of the chat, mine or
    /// received.
    /// </summary>
    /// <remarks>
    /// A failed send shows the retry link in its place, and a click can hide it. My bubble shows the time, the delivery
    /// icon and the lock. A received bubble shows the time and the lock.
    /// </remarks>
    public bool ShowsStatusRow => IsNewest && !IsFailed && !IsCaptionHidden;

    /// <summary>
    /// Gets whether the lock shows by the caption row or the clicked time. Each RCS bubble of an end-to-end encrypted
    /// chat shows it.
    /// </summary>
    public bool ShowsLock { get; private set; }

    /// <summary>
    /// Gets whether "SMS" shows in place of the delivery icon. My newest bubble shows it when it went as SMS or MMS,
    /// not RCS.
    /// </summary>
    public bool ShowsSmsLabel => IsNewest && FromMe && !IsRcs && !IsSending && StatusText.Length > 0;

    /// <summary>
    /// Gets whether the dot after the time in the caption row shows. It shows only when the delivery icon, "SMS" or the
    /// lock follows it.
    /// </summary>
    public bool ShowsCaptionDot => StatusIcon != MessageStatusIcon.None || ShowsSmsLabel || ShowsLock;

    /// <summary>Gets the reaction chip text, such as "👍😂 3", or empty.</summary>
    public string ReactionsText { get; private set; } = string.Empty;

    /// <summary>Gets whether anyone reacted.</summary>
    public bool HasReactions => ReactionsText.Length > 0;

    /// <summary>Gets the emoji of my reaction, or null when I did not react.</summary>
    public string? MyReaction { get; private set; }

    /// <summary>Gets whether I reacted. A binding skips its function when an argument is null.</summary>
    public bool HasMyReaction => MyReaction is not null;

    /// <summary>Gets whether the bubble is in a group chat, where its reaction pill lists who reacted.</summary>
    public bool IsGroupChat => _conversation?.IsGroup == true;

    /// <summary>Gets the ID of the message that this message replies to, or empty.</summary>
    public string ReplyToId { get; private set; } = string.Empty;

    /// <summary>Gets the sender name of the quoted message. The chat sets it.</summary>
    public string QuoteSender { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the text of the quoted message, or "Original message" when it is not loaded. The chat sets it.
    /// </summary>
    public string QuoteText { get; private set; } = string.Empty;

    /// <summary>Gets whether the message replies to another message.</summary>
    public bool HasQuote => ReplyToId.Length > 0;

    /// <summary>
    /// Gets the photo, video, voice message or file of the quoted message, or null. The chat sets it.
    /// </summary>
    /// <remarks>
    /// The quote then shows its thumbnail or a file icon, and its <see cref="ChatFormat.MediaLabel"/> in
    /// <see cref="QuoteText"/>.
    /// </remarks>
    public MediaPart? QuoteMedia { get; private set; }

    /// <summary>Gets whether the quote shows the thumbnail or the icon of the quoted media.</summary>
    public bool HasQuoteMedia => QuoteMedia is not null;

    /// <summary>
    /// Gets whether the text of the quote is a label in italic, such as "Image", "Video" or "GIF", not a file name.
    /// </summary>
    public bool QuoteIsLabel => QuoteMedia is not null && IsLabel(QuoteMedia);

    /// <summary>Gets whether the media label of this message is a label in italic, not a file name.</summary>
    public bool MediaLabelIsLabel => Media is { } media && IsLabel(media);

    /// <summary>Gets the text of the reply chip: the media label for a photo, video or file, else the text.</summary>
    public string ReplyChipText => Media is { } media ? ChatFormat.MediaLabel(media) : DisplayText;

    /// <summary>Gets the first web link in the text, or null.</summary>
    public Uri? LinkUrl { get; private set; }

    /// <summary>Gets the preview card of the link, or null. The chat sets it after the fetch.</summary>
    public LinkPreview? Preview { get; private set; }

    /// <summary>Gets whether the preview card shows.</summary>
    public bool HasPreview => Preview is not null;

    /// <summary>
    /// Gets the accessible name of the preview card: the page title, then the host of the link. It is empty with no
    /// card.
    /// </summary>
    /// <remarks>
    /// The address is not in the text while the card shows. Thus Narrator still tells where the link goes.
    /// </remarks>
    public string PreviewName => Preview is null || LinkUrl is null ? string.Empty
        : Preview.Title.Length > 0 ? $"{Preview.Title}, {LinkUrl.Host}" : LinkUrl.Host;

    /// <summary>Gets the photo, video, voice message or file the message carries, or null.</summary>
    public MediaPart? Media { get; private set; }

    /// <summary>Gets whether the bubble shows a photo, received or sent.</summary>
    public bool HasPhoto { get; private set; }

    /// <summary>Gets whether the bubble shows a card for a file that is not a photo, video or voice message.</summary>
    public bool HasFileCard { get; private set; }

    /// <summary>
    /// Gets whether the bubble shows a video: its first frame with a play button. The video plays in place.
    /// </summary>
    public bool HasVideo { get; private set; }

    /// <summary>
    /// Gets whether a photo or a video fills the bubble from edge to edge, with no padding. The caption then has an
    /// inset under it.
    /// </summary>
    public bool FillsBubble => HasPhoto || HasVideo;

    /// <summary>
    /// Gets whether the bubble shows the player of a voice message, with play, the waveform and the length.
    /// </summary>
    public bool HasVoice { get; private set; }

    /// <summary>
    /// Gets whether the bubble shows a photo, a video, a card or a voice message, so its menu has Open and Save as.
    /// </summary>
    public bool HasMediaFile => HasPhoto || HasVideo || HasFileCard || HasVoice;

    /// <summary>
    /// Gets whether the photo or card can open in its default app, so its menu has Open.
    /// </summary>
    /// <remarks>
    /// A program, script or installer never opens, so its menu has only Save as. See <see cref="MediaStore.CanOpen"/>.
    /// </remarks>
    public bool CanOpenMedia => HasMediaFile && Media is { } media && MediaStore.CanOpen(media.Name, media.Mime);

    /// <summary>
    /// Gets whether the bubble menu has Copy. A photo, video or file sent with no caption has no Copy, because its
    /// <see cref="Text"/> is only the kind label.
    /// </summary>
    public bool HasCopyText { get; private set; }

    /// <summary>
    /// Tells whether a focused bubble draws its own ring around its back. The owner asked for it on 2026-10-30.
    /// </summary>
    /// <remarks>
    /// Only focus from the keyboard or from code rings the bubble: Ctrl + j, the arrow keys or Tab. A click does not.
    /// With high contrast on, the bubble draws no ring. The system focus visual of the list shows in its place.
    /// </remarks>
    /// <param name="focused">True while the row of the bubble has focus.</param>
    /// <param name="byPointer">True when the focus came from a click or touch.</param>
    /// <param name="highContrast">True while the high contrast of Windows is on.</param>
    /// <returns>True when the ring shows.</returns>
    public static bool ShowsFocusRing(bool focused, bool byPointer, bool highContrast) => focused && !byPointer && !highContrast;

    /// <summary>
    /// Gets whether the bubble holds a file of mine with no upload yet. It holds until the echo from the phone brings
    /// the media ID.
    /// </summary>
    /// <remarks>
    /// The bubble shows the bytes of the attachment, and <see cref="ChatViewModel.SendAsync"/> sends them from here.
    /// </remarks>
    public bool IsLocalMedia => FromMe && Media is { MediaId.Length: 0, ThumbnailId.Length: 0, InlineData.Length: > 0 };

    /// <summary>Gets whether the upload bar shows: a file of mine that still sends from here.</summary>
    public bool IsUploading => IsSending && IsLocalMedia;

    /// <summary>Gets the uploaded part of the attachment in whole percent, from 0 to 100.</summary>
    public double UploadPercent { get; private set; }

    /// <summary>
    /// Gets the text of the retry link after a failed send. A photo or a file has its own text, and a text message has
    /// another.
    /// </summary>
    public string NotSentText => HasMediaFile ? ChatCopy.AttachSendFailed : ChatCopy.NotSentRetry;

    // Gives the caption that went with the media as the user typed it, or empty.
    internal string Caption => Media is null ? string.Empty : _message.Text;

    /// <summary>
    /// Gets the file name of the card: the name from the sender, or else the kind label, such as "Video" or "File".
    /// </summary>
    public string FileName => Media is { Name.Length: > 0 } media ? media.Name : ChatFormat.KindLabel(Kind);

    /// <summary>Gets the kind of message, such as Text, Photo, Video or File.</summary>
    public MessageKind Kind { get; private set; }

    /// <summary>Gets the accessible name of the photo: its caption, else "Photo".</summary>
    public string PhotoName => HasBodyText ? Text : ChatCopy.Photo;

    /// <summary>Gets the accessible name of the video frame and the viewer: the caption, else "Video".</summary>
    public string VideoName => HasBodyText ? Text : ChatCopy.Video;

    // The chat asks 1 time for each link, so the bubble keeps the link that got a preview request.
    internal Uri? PreviewAskedFor { get; set; }

    // The status line of the chat reads the delivery status from the phone.
    internal MessageStatusType Status { get; private set; }

    /// <summary>
    /// Gets whether the bubble is a system line, such as the name change of a group. The line is small, centered gray
    /// text with no bubble, and takes no reaction or reply.
    /// </summary>
    public bool IsSystemLine { get; private set; }

    /// <summary>
    /// Copies new details of the message into the bubble, such as an echo or a status change.
    /// </summary>
    /// <remarks>An empty ID or temporary ID in <paramref name="m"/> keeps the ID that the bubble has already.</remarks>
    /// <param name="m">The message.</param>
    public void Update(ChatMessage m)
    {
        ArgumentNullException.ThrowIfNull(m);

        object?[]? before = Snapshot();
        Fill(m);
        RaiseChanges(before);
    }

    /// <summary>
    /// Gives the size that a photo bubble reserves before its image loads, so nothing moves when the image arrives.
    /// </summary>
    /// <remarks>
    /// The size is the <c>Dimensions</c> of the photo, scaled down to fit the width of the bubble and 360 px of height.
    /// The scale keeps the aspect. A photo never shows bigger than its own pixels, except a speck with both sides under
    /// 48 px. A speck grows to 48 px, so the user can see it and click it. Unknown dimensions reserve a 240 px square,
    /// or less when the bubble is narrower. The image then fills the square, cropped.
    /// </remarks>
    /// <param name="width">The width of the photo in pixels, or 0 when unknown.</param>
    /// <param name="height">The height of the photo in pixels, or 0 when unknown.</param>
    /// <param name="maxWidth">The largest width of the photo in the bubble, in effective pixels.</param>
    /// <returns>The width and height to reserve, in whole effective pixels.</returns>
    public static (double Width, double Height) PhotoSize(int width, int height, double maxWidth)
    {
        maxWidth = Math.Max(1, maxWidth);
        if (width <= 0 || height <= 0)
        {
            double side = Math.Floor(Math.Min(UnknownPhotoSide, maxWidth));
            return (side, side);
        }

        // A photo is never bigger than its own pixels. A sharp small photo is better than a blurry big photo, by owner
        // ruling. Only a speck under 48 px both ways grows to 48.
        double scale = Math.Min(Math.Min(maxWidth / width, MaxPhotoHeight / height), 1);
        if (Math.Max(width, height) < MinPhotoSide)
        {
            scale = MinPhotoSide / Math.Max(width, height);
        }

        return (Math.Max(1, Math.Floor(width * scale)), Math.Max(1, Math.Floor(height * scale)));
    }

    /// <summary>
    /// Gives the decode width of the full image of a photo, so the image fills its bubble sharply.
    /// </summary>
    /// <remarks>
    /// The bubble crops the image to fill its box with UniformToFill. An image can be wider in shape than the box.
    /// Unknown dimensions reserve a square, and a phone can report the sides of a rotated photo swapped. A decode to
    /// the width of the box then stretched the image to the height of the box, which caused the blur. Thus the decode
    /// is wide enough to cover the height of the box too. It is never wider than the pixels of the image, because a
    /// larger decode only blurs.
    /// </remarks>
    /// <param name="pixelWidth">
    /// The width of the image in pixels as it shows, after its orientation, or 0 when unknown.
    /// </param>
    /// <param name="pixelHeight">The height of the image in pixels, or 0 when unknown.</param>
    /// <param name="boxWidth">The width of the photo box, in effective pixels.</param>
    /// <param name="boxHeight">The height of the photo box, in effective pixels.</param>
    /// <returns>The decode width in effective pixels, or the width of the box when the image size is unknown.</returns>
    public static int FillDecodeWidth(int pixelWidth, int pixelHeight, double boxWidth, double boxHeight)
    {
        int box = (int)Math.Ceiling(Math.Max(1, boxWidth));
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return box;
        }

        double cover = Math.Max(boxWidth, boxHeight * pixelWidth / pixelHeight);
        return (int)Math.Max(1, Math.Min(pixelWidth, Math.Ceiling(cover)));
    }

    /// <summary>
    /// Tells 1 time whether the bubble animates in. It is true for a bubble that the chat added live and that shows
    /// within 1 second of the add.
    /// </summary>
    /// <remarks>
    /// The chat adds a bubble live when it sends it, or when it arrives in the open chat. The flag clears, so a
    /// recycled container never plays the animation again.
    /// </remarks>
    /// <returns>True the first time that a new bubble shows.</returns>
    public bool TakeNew()
    {
        bool isNew  = _newAt != 0 && Environment.TickCount64 - _newAt <= NewWindowMs;
        _newAt      = 0;
        return isNew;
    }

    /// <summary>
    /// Asks the phone for the full-size photo when only its thumbnail came. See
    /// <see cref="MediaStore.RequestFullSizeAsync"/>.
    /// </summary>
    /// <param name="media">The media store.</param>
    /// <param name="again">True to ask again after an earlier request, as a retry when the photo never came.</param>
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
    /// <remarks>
    /// A XAML list item reads the <c>ToString()</c> of its item as its UI Automation name. Thus screen readers hear
    /// this text.
    /// </remarks>
    /// <returns>The text for screen readers.</returns>
    public override string ToString() => IsFailed ? $"{Text}, {NotSentText}" : Text;

    /// <summary>
    /// Shows or hides the time under the bubble when the user clicks it. On the newest bubble, it shows or hides the
    /// caption row in its place.
    /// </summary>
    public void ToggleTime()
    {
        object?[]? before = Snapshot();
        if (IsNewest)
        {
            IsCaptionHidden = !IsCaptionHidden;
        }
        else
        {
            IsTimeShown = !IsTimeShown;
        }

        RaiseChanges(before);
    }

    /// <summary>
    /// Lists who reacted, for the reaction pill of a group chat. The list has 1 row for each person and each emoji.
    /// </summary>
    /// <remarks>
    /// The emoji come in the order of <see cref="ChatFormat.RankedEmoji"/>, and the people in the order of the phone.
    /// My own reaction reads <see cref="ChatCopy.You"/>. The participants of the conversation give the names of the
    /// others: the full name, else the first name, else the number. The list leaves out a person with none of those,
    /// or a person not in the conversation. Thus a raw participant ID never shows.
    /// </remarks>
    /// <returns>The rows, or empty when nobody reacted.</returns>
    public IReadOnlyList<(string Emoji, string Name)> Reactors()
    {
        List<(string Emoji, string Name)> rows = [];
        foreach (string emoji in ChatFormat.RankedEmoji(_reactions))
        {
            foreach (string participantId in _reactions.Where(r => r.Emoji == emoji).SelectMany(r => r.ParticipantIds))
            {
                string name = IsMine(participantId) ? ChatCopy.You : NameOf(_conversation?.FindParticipant(participantId));
                if (name.Length > 0)
                {
                    rows.Add((emoji, name));
                }
            }
        }

        return rows;
    }

    private static string NameOf(ParticipantInfo? p) =>
        ChatFormat.SenderName(p) is { Length: > 0 } name ? name : p?.Number ?? string.Empty;

    // A bubble made before the store had its conversation learns which reaction is mine.
    internal void SetConversation(ConversationInfo? conversation)
    {
        if (conversation is null || ReferenceEquals(_conversation, conversation))
        {
            return;
        }

        object?[]? before = Snapshot();
        _conversation     = conversation;
        MyReaction        = FindMyReaction();
        RaiseChanges(before);
    }

    // The newest bubble shows the caption row. A bubble that is no longer the newest forgets a click that hid the row.
    // A bubble that becomes the newest forgets a click that showed its time. Else a hide of the caption shows the
    // footer time.
    internal void SetStatus(bool isNewest, string text, bool showsLock)
    {
        bool hidden     = isNewest && IsCaptionHidden;
        bool timeShown  = !isNewest && IsTimeShown;
        if (IsNewest == isNewest && StatusText == text && ShowsLock == showsLock && IsCaptionHidden == hidden && IsTimeShown == timeShown)
        {
            return;
        }

        object?[]? before = Snapshot();
        IsNewest          = isNewest;
        IsCaptionHidden   = hidden;
        IsTimeShown       = timeShown;
        StatusText        = text;
        ShowsLock         = showsLock;
        RaiseChanges(before);
    }

    // Sets the place of the bubble in its group. In a group chat, the name shows over the first bubble and the avatar
    // beside the last.
    internal void SetGroup(bool first, bool last, bool avatarLane, string? senderName, uint senderColor, string avatarInitial, string avatarPhotoId)
    {
        bool showsAvatar = avatarLane && last;
        string initial   = showsAvatar ? avatarInitial : string.Empty;
        string photoId   = showsAvatar ? avatarPhotoId : string.Empty;
        if (IsFirstInGroup == first && IsLastInGroup == last && HasAvatarLane == avatarLane && SenderName == senderName && SenderColor == senderColor
            && AvatarInitial == initial && AvatarPhotoId == photoId)
        {
            return;
        }

        object?[]? before = Snapshot();
        IsFirstInGroup    = first;
        IsLastInGroup     = last;
        HasAvatarLane     = avatarLane;
        ShowsAvatar       = showsAvatar;
        SenderName        = senderName;
        SenderColor       = senderColor;
        AvatarInitial     = initial;
        AvatarPhotoId     = photoId;
        RaiseChanges(before);
    }

    // A label shows in italic. The file name from the sender is not a label.
    private static bool IsLabel(MediaPart media) => media is not { Kind: MessageKind.File, Name.Length: > 0 };

    // The quote shows text-style emoji in color, as the bubble does. Quoted media shows its label in place of the text,
    // with its thumbnail. The same media again changes nothing, also when an update brings it as a new part.
    internal void SetQuote(string sender, string text, MediaPart? media = null)
    {
        string shown = media is null ? ChatFormat.WithColorEmoji(text) : ChatFormat.MediaLabel(media);
        bool same    = media is null ? QuoteMedia is null
            : QuoteMedia is { } q && q.MediaId == media.MediaId && q.ThumbnailId == media.ThumbnailId && q.Kind == media.Kind && q.Name == media.Name;
        if (QuoteSender == sender && QuoteText == shown && same)
        {
            return;
        }

        object?[]? before = Snapshot();
        QuoteSender       = sender;
        QuoteText         = shown;
        QuoteMedia        = media;
        RaiseChanges(before);
    }

    internal void SetPreview(LinkPreview preview)
    {
        if (ReferenceEquals(Preview, preview))
        {
            return;
        }

        object?[]? before = Snapshot();
        Preview           = preview;
        BodyText          = BodyOf(Text, DisplayText, _link, preview);
        RaiseChanges(before);
    }

    // The chat added the bubble live, so the bubble animates in when it first shows.
    internal void MarkNew() => _newAt = Environment.TickCount64;

    internal void MarkFailed()
    {
        object?[]? before = Snapshot();
        IsFailed          = true;
        IsSending         = false;
        RaiseChanges(before);
    }

    internal void MarkSending(string tmpId)
    {
        object?[]? before = Snapshot();
        TmpId             = tmpId;
        IsFailed          = false;
        IsSending         = true;
        UploadPercent     = 0;
        RaiseChanges(before);
    }

    // Gives the watched values before a change. With no view that listens, as for a bubble off screen, it gives null
    // and reads nothing.
    private object?[]? Snapshot()
    {
        if (PropertyChanged is null)
        {
            return null;
        }

        var values = new object?[Watched.Length];
        for (int i = 0; i < Watched.Length; i++)
        {
            values[i] = Watched[i].Get(this);
        }

        return values;
    }

    private void RaiseChanges(object?[]? before)
    {
        if (before is null)
        {
            return;
        }

        for (int i = 0; i < Watched.Length; i++)
        {
            if (!Equals(before[i], Watched[i].Get(this)))
            {
                PropertyChanged?.Invoke(this, WatchedArgs[i]);
            }
        }
    }

    // The property raises only when the whole percent changes, so a big file does not flood the view.
    internal void SetUploadShare(double share)
    {
        double percent = Math.Floor(Math.Clamp(share, 0, 1) * 100);
        if (percent == UploadPercent)
        {
            return;
        }

        UploadPercent = percent;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UploadPercent)));
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
        IsSystemLine  = m.Kind == MessageKind.Tombstone;
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
        Media         = KeepSize(m.Media, Media);
        HasPhoto      = m.Media is not null && m.Kind == MessageKind.Photo;
        HasVideo      = m.Media is not null && m.Kind == MessageKind.Video;
        HasFileCard   = m.Media is not null && m.Kind == MessageKind.File;
        HasVoice      = m.Media is not null && m.Kind == MessageKind.Audio;
        HasCopyText   = m.Media is null || m.Text.Length > 0;
        IsEmojiOnly   = m.Kind == MessageKind.Text && !HasQuote && ChatFormat.IsEmojiOnly(Text);

        // A photo, card or voice message shows only its caption under it, not the kind label.
        BodyText      = HasMediaFile && m.Text.Length == 0 ? string.Empty : BodyOf(Text, DisplayText, _link, Preview);
    }

    // An upload carries no dimensions, so its echo keeps the dimensions of the picked photo. Thus the sent photo keeps
    // its shape.
    private static MediaPart? KeepSize(MediaPart? incoming, MediaPart? known) =>
        incoming is { Width: 0, Height: 0 } && known is { Width: > 0, Height: > 0 }
            ? incoming with { Width = known.Width, Height = known.Height }
            : incoming;

    // Gives the text of the bubble, with no card link while the card shows. The space on each side of the address also
    // goes. The 2 sides join with 1 space, or with 1 line break when a side was its own line. Before punctuation, they
    // join with nothing: "see https://x.com, ok" reads "see, ok".
    private static string BodyOf(string text, string displayText, TextLink? link, LinkPreview? preview)
    {
        if (preview is null || link is not { } l)
        {
            return displayText;
        }

        string before = text[..l.Start].TrimEnd(' ', '\t');
        string after  = text[(l.Start + l.Length)..].TrimStart(' ', '\t');
        bool lineEnd  = before.EndsWith('\n');
        bool lineNew  = after.StartsWith('\n') || after.StartsWith("\r\n", StringComparison.Ordinal);

        // A line that was only the link goes with its line break.
        if (lineEnd && lineNew)
        {
            after = after[(after[0] == '\r' ? 2 : 1)..];
        }

        string joint = before.Length == 0 || after.Length == 0 || lineEnd || lineNew || char.IsPunctuation(after[0]) ? string.Empty : " ";
        return ChatFormat.WithColorEmoji((before + joint + after).Trim());
    }

    private string? FindMyReaction() => _reactions.FirstOrDefault(r => r.ParticipantIds.Any(IsMine))?.Emoji;

    // Mine is the default outgoing ID of the conversation, or a participant marked as me. The store uses the same rule.
    private bool IsMine(string participantId) =>
        participantId.Length > 0 && _conversation is { } c && (participantId == c.DefaultOutgoingId || c.FindParticipant(participantId)?.IsMe == true);
}
