using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Main;

/// <summary>
/// Shows 1 person in the Details dialog: the avatar, the name and the number.
/// </summary>
/// <param name="Name">The full name, or the number when there is no name.</param>
/// <param name="Number">
/// The number as the phone formats it, or the raw number. It is empty when the name already shows the number.
/// </param>
/// <param name="Initial">The avatar letter, or empty for the person icon.</param>
/// <param name="Color">The avatar color as opaque ARGB.</param>
/// <param name="PhotoId">The participant whose photo covers the avatar.</param>
public sealed record PersonRow(string Name, string Number, string Initial, uint Color, string PhotoId);

/// <summary>
/// Holds what the Details dialog of a conversation shows: the people, the mute state and the block option.
/// </summary>
/// <remarks>
/// The view model is a snapshot of the conversation when the dialog opens. The avatars follow
/// <see cref="AvatarParts"/>. The person of a one-on-one chat shows the letter and color of the conversation, as the
/// header does. The people of a group show their own.
/// </remarks>
public sealed class ConversationDetailsViewModel
{
    /// <summary>
    /// Reads the state of the dialog from a conversation.
    /// </summary>
    /// <param name="c">The conversation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="c"/> is null.</exception>
    public ConversationDetailsViewModel(ConversationInfo c)
    {
        ArgumentNullException.ThrowIfNull(c);

        AvatarParts header                       = AvatarParts.Of(c);
        IReadOnlyList<ConversationMenuItem> menu = ConversationMenu.For(c, inHeader: true);

        // The dialog shows only the people that the phone shows. A conversation also lists hidden people. Google
        // Messages never shows old copies of the number of the owner, "Unknown sender" or people who left a group.
        People   = [.. c.Participants.Where(p => !p.IsMe && p.IsVisible).Select(p => RowOf(p, c.IsGroup ? null : header))];
        IsMuted  = c.IsMuted;
        CanMute  = menu.Contains(ConversationMenuItem.Mute) || menu.Contains(ConversationMenuItem.Unmute);
        CanBlock = menu.Contains(ConversationMenuItem.BlockAndReport);
    }

    /// <summary>
    /// Gets the people that the phone shows in the conversation, in the order of the phone. The owner is not one of
    /// them.
    /// </summary>
    public IReadOnlyList<PersonRow> People { get; }

    /// <summary>Gets whether the conversation is muted.</summary>
    public bool IsMuted { get; }

    /// <summary>
    /// Gets whether the Mute toggle shows. The menu rule applies: the toggle does not show in spam or blocked.
    /// </summary>
    public bool CanMute { get; }

    /// <summary>
    /// Gets whether the dialog offers Block &amp; report spam. The menu rule applies: the dialog offers it only for a
    /// one-on-one chat outside spam and blocked.
    /// </summary>
    public bool CanBlock { get; }

    private static PersonRow RowOf(ParticipantInfo p, AvatarParts? oneOnOne)
    {
        string number = p.FormattedNumber ?? p.Number;
        string name   = string.IsNullOrEmpty(p.FullName) ? number : p.FullName;

        // With no saved name, the number shows only as the name.
        return new PersonRow(
            name,
            name == number ? string.Empty : number,
            oneOnOne?.Initial ?? ChatFormat.Initial(ChatFormat.SenderName(p)),
            oneOnOne?.Color ?? AvatarParts.ColorOf(p),
            p.Id);
    }
}
