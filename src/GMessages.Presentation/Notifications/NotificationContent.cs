using System.Globalization;
using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Notifications;

/// <summary>
/// The words and IDs of one toast.
/// </summary>
/// <param name="Tag">The message ID.</param>
/// <param name="Group">The conversation ID.</param>
/// <param name="Title">The conversation name.</param>
/// <param name="Body">
/// The text, or the attachment label, on one line and shortened to <see cref="MaxBodyLength"/>. In groups it starts with
/// the sender's name.
/// </param>
/// <param name="AvatarParticipantId">The sender's participant ID, for the avatar picture.</param>
public sealed record NotificationContent(string Tag, string Group, string Title, string Body, string? AvatarParticipantId)
{
    /// <summary>The longest toast body, in text elements (what a reader sees as one character), ellipsis excluded.</summary>
    public const int MaxBodyLength = 100;

    // A Word Boundary Further Back Than This Is Ignored, And The Body Is Cut Mid-Word
    private const int WordSearchLength = 20;

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
        return new NotificationContent(m.Id, c.Id, c.Name, Shorten(ChatFormat.WithColorEmoji(body)), m.ParticipantId);
    }

    /// <summary>
    /// Puts a toast body on one line and shortens it with an ellipsis when it is long.
    /// </summary>
    /// <remarks>
    /// Line breaks become spaces. A body over <see cref="MaxBodyLength"/> text elements is cut at the last space at or
    /// before that length, or at exactly that length when there is no space in the last 20. Trailing spaces and
    /// punctuation go, then "…" (U+2026) is added. Counting text elements means an emoji, a ZWJ sequence or a letter
    /// with combining marks is never split. The full text stays in the app; only the toast is shortened.
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

        // Cut At The Last Space In The Last 20 Text Elements (The Element Right After Counts: It Ends A Whole Word)
        string kept  = info.SubstringByTextElements(0, MaxBodyLength);
        int earliest = info.SubstringByTextElements(0, MaxBodyLength - WordSearchLength).Length;
        int space    = char.IsWhiteSpace(info.SubstringByTextElements(MaxBodyLength, 1)[0]) ? kept.Length : kept.LastIndexOf(' ');
        int end      = space >= earliest ? space : kept.Length;

        // Trim Trailing Spaces And Punctuation (Single Characters, So No Text Element Is Split)
        while (end > 0 && (char.IsWhiteSpace(kept[end - 1]) || char.IsPunctuation(kept[end - 1])))
        {
            end--;
        }

        return kept[..end] + "…";
    }
}
