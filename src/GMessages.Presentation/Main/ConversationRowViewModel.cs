using System.ComponentModel;
using System.Globalization;
using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Main;

/// <summary>
/// Shows 1 row of the conversation list: the name, the snippet, the time, the unread state, the pin and the avatar.
/// </summary>
/// <remarks>
/// Each <see cref="Update"/> raises <see cref="PropertyChanged"/> 1 time with an empty property name. That name tells
/// <c>x:Bind</c> to read every property again. Call it on the UI thread.
/// </remarks>
public sealed class ConversationRowViewModel : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs AllChanged      = new(string.Empty);
    private static readonly PropertyChangedEventArgs CardTimeChanged = new(nameof(CardTime));

    // The tooltip lists this number of the newest unread messages at most.
    private const int MaxHoverLines = 10;

    // A row from a page fades in only when it shows this soon after the page. A later scroll into view does not fade
    // it.
    private const long FadeWindowMs = 1000;

    // MarkPaged keeps its Environment.TickCount64 value here for TakeFadeIn. The value 0 means no mark.
    private long _pagedAt;

    // ShowCards sets these. The pill text stays here, so a flip back to the pill needs no new data.
    private bool _cards;
    private string _badge = string.Empty;

    /// <summary>
    /// Creates the row from a conversation.
    /// </summary>
    /// <param name="c">The conversation.</param>
    /// <param name="now">The current time, for the list time.</param>
    /// <param name="unreadTexts">
    /// The texts of the live unread messages from <see cref="ChatStore.LiveUnreadTexts"/>, or null for none.
    /// </param>
    /// <param name="draft">
    /// The line of the unsent draft from <see cref="ChatFormat.DraftLine"/>, or empty for none.
    /// </param>
    public ConversationRowViewModel(ConversationInfo c, DateTimeOffset now, IReadOnlyList<string>? unreadTexts = null, string draft = "")
    {
        ArgumentNullException.ThrowIfNull(c);

        Id = c.Id;
        Fill(c, now, unreadTexts ?? [], draft);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the conversation ID.</summary>
    public string Id { get; }

    /// <summary>Gets the conversation name.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the text of the newest message, with "You: " for a message from the owner. While a draft exists, the
    /// snippet is "Draft: " and the unsent draft.
    /// </summary>
    public string Snippet { get; private set; } = string.Empty;

    /// <summary>Gets whether the snippet shows an unsent draft. The view draws it in italics.</summary>
    public bool IsDraft { get; private set; }

    /// <summary>Gets the formatted time of the newest message.</summary>
    public string Time { get; private set; } = string.Empty;

    /// <summary>Gets whether the conversation has unread messages.</summary>
    public bool IsUnread { get; private set; }

    /// <summary>
    /// Gets whether more than 1 message arrived live while the conversation is unread. The tooltip then lists them.
    /// </summary>
    public bool HasUnreadList { get; private set; }

    /// <summary>
    /// Gets the tooltip of the row: the name, and then the body. With more than 1 live unread message, the body lists
    /// the newest 10 on separate lines. Otherwise the body is the whole snippet.
    /// </summary>
    public string HoverText { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the unread badge of the pinned tile from <see cref="ChatFormat.UnreadBadge"/>, or empty while the
    /// conversation is read or the tile shows cards.
    /// </summary>
    public string UnreadBadge => _cards ? string.Empty : _badge;

    /// <summary>Gets whether the pinned tile shows the message card instead of the picture.</summary>
    public bool ShowsCard => _cards && IsUnread;

    /// <summary>Gets whether the tiles show cards, so the tile is tall enough for 1.</summary>
    public bool CardsOn => _cards;

    /// <summary>
    /// Gets the text of the card: the newest message from <see cref="ChatFormat.Snippet"/>. A draft does not replace
    /// it.
    /// </summary>
    public string CardText { get; private set; } = string.Empty;

    /// <summary>Gets the time of the card from <see cref="ChatFormat.AgoTime"/>, such as "4 min".</summary>
    public string CardTime { get; private set; } = string.Empty;

    /// <summary>Gets the number of unread messages for the card. It is at least 1.</summary>
    public string UnreadCount { get; private set; } = string.Empty;

    /// <summary>Gets whether the conversation is pinned.</summary>
    public bool IsPinned { get; private set; }

    /// <summary>Gets whether the conversation is muted.</summary>
    public bool IsMuted { get; private set; }

    /// <summary>
    /// Gets the avatar letter: the letter of the conversation, or of the first other person in a group.
    /// </summary>
    public string Initial { get; private set; } = string.Empty;

    /// <summary>Gets the avatar color as opaque ARGB.</summary>
    public uint AvatarColor { get; private set; }

    /// <summary>Gets the participant whose photo covers the front circle, or empty.</summary>
    public string PhotoId { get; private set; } = string.Empty;

    /// <summary>Gets the letter of the back circle: the second other person of a group, or empty.</summary>
    public string SecondInitial { get; private set; } = string.Empty;

    /// <summary>Gets the color of the back circle as opaque ARGB, or 0 when there is no back circle.</summary>
    public uint SecondColor { get; private set; }

    /// <summary>Gets the participant whose photo covers the back circle, or empty.</summary>
    public string SecondPhotoId { get; private set; } = string.Empty;

    /// <summary>Gets the time of the newest message. The list sorts by it.</summary>
    public DateTimeOffset LastMessageTime { get; private set; }

    /// <summary>
    /// Copies new conversation details into the row.
    /// </summary>
    /// <param name="c">The conversation.</param>
    /// <param name="now">The current time, for the list time.</param>
    /// <param name="unreadTexts">
    /// The texts of the live unread messages from <see cref="ChatStore.LiveUnreadTexts"/>, or null for none.
    /// </param>
    /// <param name="draft">
    /// The line of the unsent draft from <see cref="ChatFormat.DraftLine"/>, or empty for none.
    /// </param>
    public void Update(ConversationInfo c, DateTimeOffset now, IReadOnlyList<string>? unreadTexts = null, string draft = "")
    {
        ArgumentNullException.ThrowIfNull(c);

        Fill(c, now, unreadTexts ?? [], draft);
        PropertyChanged?.Invoke(this, AllChanged);
    }

    /// <summary>
    /// Tells 1 time whether the row fades in. It is true for a row that a page added and that shows within 1 second of
    /// the page.
    /// </summary>
    /// <remarks>The flag clears, so a recycled container never fades the row again.</remarks>
    /// <returns>True the first time that a new row from a page shows.</returns>
    public bool TakeFadeIn()
    {
        bool fade = _pagedAt != 0 && Environment.TickCount64 - _pagedAt <= FadeWindowMs;
        _pagedAt  = 0;
        return fade;
    }

    /// <summary>
    /// Sets whether the pinned tile shows a message card while unread, or the pill on the picture.
    /// </summary>
    /// <param name="cards">True for the card.</param>
    internal void ShowCards(bool cards)
    {
        if (_cards == cards)
        {
            return;
        }

        _cards = cards;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    /// <summary>
    /// Makes <see cref="CardTime"/> current. A card shows "4 min", so the text goes old as time passes.
    /// </summary>
    /// <param name="now">The current time.</param>
    internal void RefreshCardTime(DateTimeOffset now)
    {
        string time = ChatFormat.AgoTime(LastMessageTime, now, CultureInfo.CurrentCulture);
        if (time == CardTime)
        {
            return;
        }

        CardTime = time;
        PropertyChanged?.Invoke(this, CardTimeChanged);
    }

    /// <summary>Marks the row as added by a page, for <see cref="TakeFadeIn"/>.</summary>
    internal void MarkPaged() => _pagedAt = Environment.TickCount64;

    /// <summary>
    /// Gives the accessible name of the row: the conversation name, and "Unread" while the conversation has unread
    /// messages.
    /// </summary>
    /// <remarks>
    /// A list item with no name of its own reads this name through UI Automation. Narrator and the UI tests read it.
    /// </remarks>
    /// <returns>The name.</returns>
    public override string ToString() => IsUnread ? $"{Name}, Unread" : Name;

    private void Fill(ConversationInfo c, DateTimeOffset now, IReadOnlyList<string> unreadTexts, string draft)
    {
        Name            = c.Name;
        IsDraft         = draft.Length > 0;
        Snippet         = IsDraft ? ChatFormat.WithColorEmoji(ChatCopy.DraftPrefix + draft) : ChatFormat.Snippet(c);
        Time            = ChatFormat.ListTime(c.LastMessageTime, now, CultureInfo.CurrentCulture);
        IsUnread        = c.Unread;
        _badge          = ChatFormat.UnreadBadge(c.Unread, unreadTexts.Count, c.Snippet);
        CardText        = ChatFormat.Snippet(c);
        UnreadCount     = Math.Max(1, unreadTexts.Count).ToString(CultureInfo.CurrentCulture);
        HasUnreadList   = c.Unread && unreadTexts.Count > 1;
        string body     = HasUnreadList ? string.Join('\n', unreadTexts.TakeLast(MaxHoverLines).Select(ChatFormat.WithColorEmoji)) : Snippet;
        HoverText       = body.Length > 0 ? Name + "\n" + body : Name;
        IsPinned        = c.Pinned;
        IsMuted         = c.IsMuted;
        LastMessageTime = c.LastMessageTime;
        CardTime        = ChatFormat.AgoTime(c.LastMessageTime, now, CultureInfo.CurrentCulture);

        // By ruling R12, a group stacks 2 people in the avatar, as the chat header does.
        AvatarParts avatar = AvatarParts.Of(c);
        Initial            = avatar.Initial;
        AvatarColor        = avatar.Color;
        PhotoId            = avatar.PhotoId;
        SecondInitial      = avatar.SecondInitial;
        SecondColor        = avatar.SecondColor;
        SecondPhotoId      = avatar.SecondPhotoId;
    }
}
