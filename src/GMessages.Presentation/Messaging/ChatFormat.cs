using System.Globalization;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Turns conversation data into the short text the chat list shows.
/// </summary>
public static class ChatFormat
{
    /// <summary>
    /// The snippet line of a conversation, with "You: " in front of the owner's own messages.
    /// </summary>
    /// <param name="c">The conversation.</param>
    /// <returns>The text to show.</returns>
    public static string Snippet(ConversationInfo c)
    {
        ArgumentNullException.ThrowIfNull(c);

        return c.SnippetFromMe ? ChatCopy.YouPrefix + c.Snippet : c.Snippet;
    }

    /// <summary>
    /// The list time, in the current culture. See <see cref="ListTime(DateTimeOffset, DateTimeOffset, CultureInfo)"/>.
    /// </summary>
    /// <param name="time">When the message arrived.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The text to show.</returns>
    public static string ListTime(DateTimeOffset time, DateTimeOffset now) => ListTime(time, now, CultureInfo.CurrentCulture);

    /// <summary>
    /// The list time: the time today, the weekday within the last 7 days, month and day this year, else a short date.
    /// </summary>
    /// <remarks><paramref name="time"/> is converted to <paramref name="now"/>'s offset before the day is compared.</remarks>
    /// <param name="time">When the message arrived.</param>
    /// <param name="now">The current time.</param>
    /// <param name="culture">The culture to format with.</param>
    /// <returns>The text to show.</returns>
    public static string ListTime(DateTimeOffset time, DateTimeOffset now, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        DateTime local = time.ToOffset(now.Offset).DateTime;
        int days_ago   = (now.Date - local.Date).Days;

        if (days_ago <= 0)
        {
            return local.ToString("t", culture);
        }

        if (days_ago < 7)
        {
            return local.ToString("ddd", culture);
        }

        if (local.Year == now.Year)
        {
            return local.ToString("MMM d", culture);
        }

        // Two-Digit Year Short Date
        string pattern = culture.DateTimeFormat.ShortDatePattern.Replace("yyyy", "yy", StringComparison.Ordinal);
        return local.ToString(pattern, culture);
    }

    /// <summary>
    /// The first visible character of a name, for avatars.
    /// </summary>
    /// <remarks>
    /// A name with no letter in it is no name at all: the phone shows a phone number or a short code (such as "59538")
    /// for a contact nobody saved. Those get an empty initial, and the avatar shows the generic person icon instead.
    /// </remarks>
    /// <param name="name">The name.</param>
    /// <returns>The first text element in upper case, or empty when the name has no letters.</returns>
    public static string Initial(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();

        return trimmed.Any(char.IsLetter) ? StringInfo.GetNextTextElement(trimmed).ToUpperInvariant() : string.Empty;
    }

    /// <summary>
    /// Reads a "#rrggbb" or "rrggbb" color as opaque ARGB.
    /// </summary>
    /// <param name="hex">The hex color.</param>
    /// <param name="fallback">What to return when the text is not a color.</param>
    /// <returns>0xFFRRGGBB, or <paramref name="fallback"/>.</returns>
    public static uint ColorFromHex(string hex, uint fallback)
    {
        string digits = (hex ?? string.Empty).TrimStart('#');

        if (digits.Length != 6 || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
        {
            return fallback;
        }

        return 0xFF000000u | rgb;
    }

    /// <summary>
    /// The label that stands in for a message that has no text.
    /// </summary>
    /// <param name="kind">What the message shows.</param>
    /// <returns>"Photo", "Video", "Voice message", "File" or "Unsupported message".</returns>
    public static string KindLabel(MessageKind kind) => kind switch
    {
        MessageKind.Photo => ChatCopy.Photo,
        MessageKind.Video => ChatCopy.Video,
        MessageKind.Audio => ChatCopy.VoiceMessage,
        MessageKind.File  => ChatCopy.File,
        _                 => ChatCopy.UnsupportedMessage,
    };

    /// <summary>
    /// How a participant is named: full name, else first name, else phone number.
    /// </summary>
    /// <param name="p">The participant, or null.</param>
    /// <returns>The name, or an empty string when there is none.</returns>
    public static string SenderName(ParticipantInfo? p) =>
        new[] { p?.FullName, p?.FirstName, p?.FormattedNumber }.FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? string.Empty;
}
