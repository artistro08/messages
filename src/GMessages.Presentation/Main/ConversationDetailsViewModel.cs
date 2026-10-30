using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Main;

/// <summary>
/// One person in the Details dialog: their avatar, name and number.
/// </summary>
/// <param name="Name">The full name, else the number.</param>
/// <param name="Number">The number as the phone shows it, else the raw number; empty when it is already the name.</param>
/// <param name="Initial">The avatar letter, or empty for the person icon.</param>
/// <param name="Color">The avatar color as opaque ARGB.</param>
/// <param name="PhotoId">The participant whose photo covers the avatar.</param>
public sealed record PersonRow(string Name, string Number, string Initial, uint Color, string PhotoId);

/// <summary>
/// What a conversation's Details dialog shows: its people, whether it is muted and whether it can be blocked.
/// </summary>
/// <remarks>
/// A snapshot of the conversation when the dialog opens. The avatars follow <see cref="AvatarParts"/>: a one-on-one
/// chat's person shows the conversation's letter and color, as the header does; a group's people show their own.
/// </remarks>
public sealed class ConversationDetailsViewModel
{
    /// <summary>
    /// Reads the dialog's state from a conversation.
    /// </summary>
    /// <param name="c">The conversation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="c"/> is null.</exception>
    public ConversationDetailsViewModel(ConversationInfo c)
    {
        ArgumentNullException.ThrowIfNull(c);

        AvatarParts header                       = AvatarParts.Of(c);
        IReadOnlyList<ConversationMenuItem> menu = ConversationMenu.For(c, inHeader: true);

        People   = [.. c.Participants.Where(p => !p.IsMe).Select(p => RowOf(p, c.IsGroup ? null : header))];
        IsMuted  = c.IsMuted;
        CanMute  = menu.Contains(ConversationMenuItem.Mute) || menu.Contains(ConversationMenuItem.Unmute);
        CanBlock = menu.Contains(ConversationMenuItem.BlockAndReport);
    }

    /// <summary>Gets everyone in the conversation but the phone's owner, in the phone's order.</summary>
    public IReadOnlyList<PersonRow> People { get; }

    /// <summary>Gets whether the conversation is muted.</summary>
    public bool IsMuted { get; }

    /// <summary>Gets whether the Mute toggle shows (the menu's rule: not in spam or blocked).</summary>
    public bool CanMute { get; }

    /// <summary>Gets whether Block &amp; report spam is offered (the menu's rule: one-on-one, not spam or blocked).</summary>
    public bool CanBlock { get; }

    // A Person's Row; A One-On-One Chat's Person Takes The Header's Letter And Color
    private static PersonRow RowOf(ParticipantInfo p, AvatarParts? oneOnOne)
    {
        string number = p.FormattedNumber ?? p.Number;
        string name   = string.IsNullOrEmpty(p.FullName) ? number : p.FullName;

        // No Saved Name: The Number Shows Once, As The Name
        return new PersonRow(
            name,
            name == number ? string.Empty : number,
            oneOnOne?.Initial ?? ChatFormat.Initial(ChatFormat.SenderName(p)),
            oneOnOne?.Color ?? AvatarParts.ColorOf(p),
            p.Id);
    }
}
