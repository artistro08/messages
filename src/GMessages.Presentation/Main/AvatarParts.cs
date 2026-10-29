using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Main;

/// <summary>
/// What a conversation's avatar shows: the front circle, and the back circle of a group.
/// </summary>
/// <remarks>
/// Shared by the list row and the chat header so they always match. Ruling R12: a group stacks its people, the first
/// other participant's letter, color and photo in front and the second's behind. A one-on-one chat (and a group with
/// nobody else in it) shows the conversation's own letter and color, with the other person's photo when there is one.
/// </remarks>
/// <param name="Initial">The front circle's letter.</param>
/// <param name="Color">The front circle's color as opaque ARGB.</param>
/// <param name="PhotoId">The participant whose photo covers the front circle, or empty.</param>
/// <param name="SecondInitial">The back circle's letter, or empty.</param>
/// <param name="SecondColor">The back circle's color as opaque ARGB; 0 means no back circle.</param>
/// <param name="SecondPhotoId">The participant whose photo covers the back circle, or empty.</param>
internal readonly record struct AvatarParts(string Initial, uint Color, string PhotoId, string SecondInitial, uint SecondColor, string SecondPhotoId)
{
    // Gray Used When The Phone Gives No Avatar Color
    internal const uint FallbackColor = 0xFF5F6368;

    /// <summary>
    /// Works out the avatar of a conversation.
    /// </summary>
    /// <param name="c">The conversation.</param>
    /// <returns>The avatar's parts.</returns>
    public static AvatarParts Of(ConversationInfo c)
    {
        List<ParticipantInfo> others = [.. c.Participants.Where(p => !p.IsMe).Take(2)];

        // One On One, Or A Group With Nobody Else: The Conversation Itself
        if (!c.IsGroup || others.Count == 0)
        {
            return new AvatarParts(ChatFormat.Initial(c.Name), ChatFormat.ColorFromHex(c.AvatarHexColor, FallbackColor), others.FirstOrDefault()?.Id ?? string.Empty, string.Empty, 0, string.Empty);
        }

        // Group: The First Two Other People, Stacked
        ParticipantInfo front   = others[0];
        ParticipantInfo? second = others.Count > 1 ? others[1] : null;
        return new AvatarParts(
            ChatFormat.Initial(ChatFormat.SenderName(front)),
            ChatFormat.ColorFromHex(front.AvatarHexColor, FallbackColor),
            front.Id,
            second is null ? string.Empty : ChatFormat.Initial(ChatFormat.SenderName(second)),
            second is null ? 0 : ChatFormat.ColorFromHex(second.AvatarHexColor, FallbackColor),
            second?.Id ?? string.Empty);
    }
}
