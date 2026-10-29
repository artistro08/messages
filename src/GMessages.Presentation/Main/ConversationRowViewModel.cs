using System.ComponentModel;
using System.Globalization;
using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Main;

/// <summary>
/// One row of the conversation list: name, snippet, time, unread dot, pin and avatar details.
/// </summary>
/// <remarks>
/// Every <see cref="Update"/> raises <see cref="PropertyChanged"/> once with an empty property name, which tells
/// <c>x:Bind</c> to re-read every property. Call it on the UI thread.
/// </remarks>
public sealed class ConversationRowViewModel : INotifyPropertyChanged
{
    // Gray Used When The Phone Gives No Avatar Color
    private const uint FallbackColor = 0xFF5F6368;

    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    /// <summary>
    /// Creates the row from a conversation.
    /// </summary>
    /// <param name="c">The conversation.</param>
    /// <param name="now">The current time, for the list time.</param>
    public ConversationRowViewModel(ConversationInfo c, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(c);

        Id = c.Id;
        Fill(c, now);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the conversation ID.</summary>
    public string Id { get; }

    /// <summary>Gets the conversation name.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Gets the newest message's text, with "You: " for the owner's own messages.</summary>
    public string Snippet { get; private set; } = string.Empty;

    /// <summary>Gets the formatted time of the newest message.</summary>
    public string Time { get; private set; } = string.Empty;

    /// <summary>Gets whether there are unread messages.</summary>
    public bool IsUnread { get; private set; }

    /// <summary>Gets whether the conversation is pinned.</summary>
    public bool IsPinned { get; private set; }

    /// <summary>Gets whether the conversation is a group chat.</summary>
    public bool IsGroup { get; private set; }

    /// <summary>Gets the avatar letter.</summary>
    public string Initial { get; private set; } = string.Empty;

    /// <summary>Gets the avatar color as opaque ARGB.</summary>
    public uint AvatarColor { get; private set; }

    /// <summary>Gets the IDs whose photos make the avatar: one other person, or up to two for a group.</summary>
    public IReadOnlyList<string> ParticipantIds { get; private set; } = [];

    /// <summary>Gets when the newest message arrived (the sort key).</summary>
    public DateTimeOffset LastMessageTime { get; private set; }

    /// <summary>
    /// Copies new conversation details into the row.
    /// </summary>
    /// <param name="c">The conversation.</param>
    /// <param name="now">The current time, for the list time.</param>
    public void Update(ConversationInfo c, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(c);

        Fill(c, now);
        PropertyChanged?.Invoke(this, AllChanged);
    }

    private void Fill(ConversationInfo c, DateTimeOffset now)
    {
        Name            = c.Name;
        Snippet         = ChatFormat.Snippet(c);
        Time            = ChatFormat.ListTime(c.LastMessageTime, now, CultureInfo.CurrentCulture);
        IsUnread        = c.Unread;
        IsPinned        = c.Pinned;
        IsGroup         = c.IsGroup;
        Initial         = ChatFormat.Initial(c.Name);
        AvatarColor     = ChatFormat.ColorFromHex(c.AvatarHexColor, FallbackColor);
        LastMessageTime = c.LastMessageTime;
        ParticipantIds  = [.. c.Participants.Where(p => !p.IsMe).Take(c.IsGroup ? 2 : 1).Select(p => p.Id)];
    }
}
