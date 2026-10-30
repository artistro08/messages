using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Notifications;

/// <summary>
/// The words and IDs of one toast.
/// </summary>
/// <param name="Tag">The message ID.</param>
/// <param name="Group">The conversation ID.</param>
/// <param name="Title">The conversation name.</param>
/// <param name="Body">The text, or the attachment label. In groups it starts with the sender's name.</param>
/// <param name="AvatarParticipantId">The sender's participant ID, for the avatar picture.</param>
public sealed record NotificationContent(string Tag, string Group, string Title, string Body, string? AvatarParticipantId)
{
    /// <summary>
    /// Builds the toast content for a message.
    /// </summary>
    /// <remarks>The sender is named by full name, else first name, else phone number.</remarks>
    /// <param name="m">The message.</param>
    /// <param name="c">Its conversation.</param>
    /// <returns>The toast content.</returns>
    public static NotificationContent Compose(ChatMessage m, ConversationInfo c)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(c);

        string body = ChatFormat.ShownText(m);

        // Name The Sender In Groups
        if (c.IsGroup)
        {
            string name = ChatFormat.SenderName(c.FindParticipant(m.ParticipantId));

            if (name.Length > 0)
            {
                body = $"{name}: {body}";
            }
        }

        // Text-Style Emoji In Color, As In The Chat
        return new NotificationContent(m.Id, c.Id, c.Name, ChatFormat.WithColorEmoji(body), m.ParticipantId);
    }
}
