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
    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    // The tooltip lists this number of the newest unread messages at most.
    private const int MaxHoverLines = 10;

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
    /// conversation is read.
    /// </summary>
    public string UnreadBadge { get; private set; } = string.Empty;

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
        UnreadBadge     = ChatFormat.UnreadBadge(c.Unread, unreadTexts.Count, c.Snippet);
        HasUnreadList   = c.Unread && unreadTexts.Count > 1;
        string body     = HasUnreadList ? string.Join('\n', unreadTexts.TakeLast(MaxHoverLines).Select(ChatFormat.WithColorEmoji)) : Snippet;
        HoverText       = body.Length > 0 ? Name + "\n" + body : Name;
        IsPinned        = c.Pinned;
        IsMuted         = c.IsMuted;
        LastMessageTime = c.LastMessageTime;

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
