using System.Globalization;
using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Notifications;

/// <summary>
/// Holds the text and the IDs of one toast.
/// </summary>
/// <param name="Tag">The message ID.</param>
/// <param name="Group">The conversation ID.</param>
/// <param name="Title">The conversation name.</param>
/// <param name="Body">
/// The message text or the attachment label, on one line and at most <see cref="MaxBodyLength"/> long. In a group, the
/// body starts with the name of the sender.
/// </param>
/// <param name="AvatarParticipantId">The participant ID of the sender, for the avatar picture.</param>
public sealed record NotificationContent(string Tag, string Group, string Title, string Body, string? AvatarParticipantId)
{
    private const int MaxBodyLength = 100;

    /// <summary>
    /// Gets whether the app copied the one-time code of the message to the clipboard. The toast then says "Code
    /// copied" under the text.
    /// </summary>
    public bool CodeCopied { get; init; }

    private const int WordSearchLength = 20;

    /// <summary>
    /// Builds the toast content for a message.
    /// </summary>
    /// <remarks>
    /// In a group, the body names the sender by the full name, else the first name, else the phone number.
    /// </remarks>
    /// <param name="m">The message.</param>
    /// <param name="c">The conversation of the message.</param>
    /// <returns>The toast content.</returns>
    public static NotificationContent Compose(ChatMessage m, ConversationInfo c)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(c);

        string body = ChatFormat.ShownText(m);

        if (c.IsGroup)
        {
            string name = ChatFormat.SenderName(c.FindParticipant(m.ParticipantId));

            if (name.Length > 0)
            {
                body = $"{name}: {body}";
            }
        }

        // The toast shows text-style emoji in color, as the chat does.
        return new NotificationContent(m.Id, c.Id, c.Name, Shorten(ChatFormat.WithColorEmoji(body)), m.ParticipantId);
    }

    /// <summary>
    /// Puts a toast body on one line and shortens it with an ellipsis when it is long.
    /// </summary>
    /// <remarks>
    /// The method changes each line break to a space. It cuts a body longer than <see cref="MaxBodyLength"/> text
    /// elements at the last space or tab in its last 20 text elements. A space right after the limit also counts,
    /// because it ends a whole word. With no space, the cut is at <see cref="MaxBodyLength"/>. The method then removes
    /// trailing spaces and punctuation and adds "…" (U+2026). The count uses text elements, so the cut never splits an
    /// emoji, a ZWJ sequence or a letter with combining marks. The app keeps the full text. Only the toast is short.
    /// </remarks>
    /// <param name="body">The body.</param>
    /// <returns>The body on one line, at most <see cref="MaxBodyLength"/> text elements plus the ellipsis.</returns>
    public static string Shorten(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        string line = body.ReplaceLineEndings(" ");
        var info    = new StringInfo(line);
        if (info.LengthInTextElements <= MaxBodyLength)
        {
            return line;
        }

        string kept  = info.SubstringByTextElements(0, MaxBodyLength);
        int earliest = info.SubstringByTextElements(0, MaxBodyLength - WordSearchLength).Length;
        int space    = char.IsWhiteSpace(info.SubstringByTextElements(MaxBodyLength, 1)[0]) ? kept.Length : kept.LastIndexOfAny([' ', '\t']);
        int end      = space >= earliest ? space : kept.Length;

        // A space or a punctuation mark is a single character, so the loop never splits a text element.
        while (end > 0 && (char.IsWhiteSpace(kept[end - 1]) || char.IsPunctuation(kept[end - 1])))
        {
            end--;
        }

        return kept[..end] + "…";
    }
}
