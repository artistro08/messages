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
    private readonly ConversationInfo? _conversation;

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

    /// <summary>Gets whether the owner sent the message.</summary>
    public bool FromMe { get; private set; }

    /// <summary>Gets the sender's name when this is the first bubble of a received run in a group, else null.</summary>
    public string? SenderName { get; private set; }

    /// <summary>Gets the color of the sender's name: the same as their letter circle, as opaque ARGB.</summary>
    public uint SenderColor { get; private set; } = AvatarParts.FallbackColor;

    /// <summary>Gets whether <see cref="SenderName"/> is set (for bindings, which skip their function when an argument is null).</summary>
    public bool HasSenderName => SenderName is not null;

    /// <summary>Gets when the message was sent.</summary>
    public DateTimeOffset SentAt { get; private set; }

    /// <summary>Gets the time of day the message was sent.</summary>
    public string Time { get; private set; } = string.Empty;

    /// <summary>Gets whether the time shows under the bubble: hidden until the bubble is clicked (see <see cref="ToggleTime"/>).</summary>
    public bool IsTimeShown { get; private set; }

    /// <summary>Gets whether the message was not sent.</summary>
    public bool IsFailed { get; private set; }

    /// <summary>Gets whether the message is still being sent.</summary>
    public bool IsSending { get; private set; }

    /// <summary>Gets whether the message went over RCS (chat) rather than SMS or MMS.</summary>
    public bool IsRcs { get; private set; }

    /// <summary>Gets the bubble's accessible item type: "Chat message" for RCS, "Text message" for SMS.</summary>
    public string ItemTypeName => IsRcs ? ChatCopy.ChatMessage : ChatCopy.TextMessage;

    /// <summary>Gets the status line under the bubble ("Sent", "Delivered", "Read"), or empty; set by the chat.</summary>
    public string StatusText { get; private set; } = string.Empty;

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

    // The Phone's Delivery Status, For The Chat's Status Line
    internal MessageStatusType Status { get; private set; }

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
    /// Shows the time under the bubble, or hides it again (a click on the bubble).
    /// </summary>
    public void ToggleTime()
    {
        IsTimeShown = !IsTimeShown;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    internal void SetSender(string? name, uint color)
    {
        if (SenderName == name && SenderColor == color)
        {
            return;
        }

        SenderName  = name;
        SenderColor = color;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    internal void SetStatusText(string text)
    {
        if (StatusText == text)
        {
            return;
        }

        StatusText = text;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    internal void SetQuote(string sender, string text)
    {
        if (QuoteSender == sender && QuoteText == text)
        {
            return;
        }

        QuoteSender = sender;
        QuoteText   = text;
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
        FromMe        = m.FromMe;
        SentAt        = m.Time;
        Time          = m.Time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        IsFailed      = m.IsFailed;
        IsSending     = m.Status == MessageStatusType.OutgoingSending;
        Status        = m.Status;
        IsRcs         = m.IsRcs;
        ReactionsText = ChatFormat.ReactionSummary(m.Reactions);
        MyReaction    = m.Reactions.FirstOrDefault(r => r.ParticipantIds.Any(IsMine))?.Emoji;
        ReplyToId     = m.ReplyToId;
        LinkUrl       = m.Kind == MessageKind.Text ? FirstLink(m.Text) : null;
    }

    // Mine: Any Participant Marked As Me, Or The ID The Conversation Sends As
    private bool IsMine(string participantId) =>
        participantId.Length > 0 && _conversation is { } c && (participantId == c.DefaultOutgoingId || c.FindParticipant(participantId)?.IsMe == true);

    // The First Word That Is An Absolute http Or https Link
    private static Uri? FirstLink(string text) => text
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => Uri.TryCreate(word, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null)
        .FirstOrDefault(uri => uri is not null);
}
