using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Main;

/// <summary>
/// Holds what the avatar of a conversation shows: the front circle, and the back circle of a group.
/// </summary>
/// <remarks>
/// The list row and the chat header share it, so they always match. By ruling R12, a group stacks its people. The
/// letter, color and photo of the first other participant go in front, and those of the second go behind. A one-on-one
/// chat shows the letter and color of the conversation, with the photo of the other person when it exists. A group
/// with nobody else in it does the same.
/// </remarks>
/// <param name="Initial">The letter of the front circle.</param>
/// <param name="Color">The color of the front circle as opaque ARGB.</param>
/// <param name="PhotoId">The participant whose photo covers the front circle, or empty.</param>
/// <param name="SecondInitial">The letter of the back circle, or empty.</param>
/// <param name="SecondColor">The color of the back circle as opaque ARGB. The value 0 means no back circle.</param>
/// <param name="SecondPhotoId">The participant whose photo covers the back circle, or empty.</param>
internal readonly record struct AvatarParts(string Initial, uint Color, string PhotoId, string SecondInitial, uint SecondColor, string SecondPhotoId)
{
    // The avatar is this gray when the phone gives no avatar color.
    internal const uint FallbackColor = 0xFF5F6368;

    /// <summary>
    /// Gives the color of the letter circle of a person. Their name above group bubbles has the same color.
    /// </summary>
    /// <param name="p">The person, or null when unknown.</param>
    /// <returns>The color as opaque ARGB, or the fallback gray when the phone gives none.</returns>
    public static uint ColorOf(ParticipantInfo? p) => ChatFormat.ColorFromHex(p?.AvatarHexColor ?? string.Empty, FallbackColor);

    /// <summary>
    /// Computes the avatar of a conversation.
    /// </summary>
    /// <param name="c">The conversation.</param>
    /// <returns>The parts of the avatar.</returns>
    public static AvatarParts Of(ConversationInfo c)
    {
        List<ParticipantInfo> others = [.. c.Participants.Where(p => !p.IsMe).Take(2)];

        // A one-on-one chat, or a group with nobody else, shows the conversation itself.
        if (!c.IsGroup || others.Count == 0)
        {
            return new AvatarParts(ChatFormat.Initial(c.Name), ChatFormat.ColorFromHex(c.AvatarHexColor, FallbackColor), others.FirstOrDefault()?.Id ?? string.Empty, string.Empty, 0, string.Empty);
        }

        ParticipantInfo front   = others[0];
        ParticipantInfo? second = others.Count > 1 ? others[1] : null;
        return new AvatarParts(
            ChatFormat.Initial(ChatFormat.SenderName(front)),
            ColorOf(front),
            front.Id,
            second is null ? string.Empty : ChatFormat.Initial(ChatFormat.SenderName(second)),
            second is null ? 0 : ColorOf(second),
            second?.Id ?? string.Empty);
    }
}
