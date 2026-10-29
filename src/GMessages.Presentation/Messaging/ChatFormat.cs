using System.Globalization;
using GMessages.Protocol.Proto;

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
    /// A phone number, a short code (such as "59538") or an email address is no name at all: it's what the phone shows
    /// for someone nobody saved. Those get an empty initial, and the avatar shows the generic person icon instead.
    /// Anything else, an emoji-only name or one starting with a digit ("2Pac") included, keeps its first character.
    /// </remarks>
    /// <param name="name">The name.</param>
    /// <returns>The first text element in upper case, or empty for no name.</returns>
    public static string Initial(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();

        return IsNoName(trimmed) ? string.Empty : StringInfo.GetNextTextElement(trimmed).ToUpperInvariant();
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

    // TODO: LibGM has no encryption field on Conversation; the lock follows the newest protocol tombstone seen, so a chat whose tombstone is older than every stored page shows no lock until it is scrolled to.
    /// <summary>
    /// What a system line says about end-to-end encryption.
    /// </summary>
    /// <remarks>
    /// The protocol tombstones that switch a chat into or out of encryption. The newest one seen decides whether the
    /// chat shows the lock.
    /// </remarks>
    /// <param name="status">The message status.</param>
    /// <returns>True when the chat is now encrypted, false when it no longer is, null when the status says nothing.</returns>
    public static bool? EncryptionOf(MessageStatusType status) => status switch
    {
        MessageStatusType.TombstoneEncryptedOneOnOneRcsCreated
            or MessageStatusType.TombstoneProtocolSwitchToEncryptedRcs
            or MessageStatusType.TombstoneProtocolSwitchToEncryptedRcsInfo
            or MessageStatusType.MessageStatusTombstoneEncryptedGroupCreated
            or MessageStatusType.MessageStatusTombstoneGroupProtocolSwitchRcsToE2Ee
            or MessageStatusType.MessageStatusTombstoneProtocolSwitchTextToE2Ee
            or MessageStatusType.MessageStatusTombstoneProtocolSwitchRcsToE2Ee => true,

        MessageStatusType.TombstoneProtocolSwitchToText
            or MessageStatusType.TombstoneProtocolSwitchToRcs
            or MessageStatusType.TombstoneOneOnOneSmsCreated
            or MessageStatusType.TombstoneOneOnOneRcsCreated
            or MessageStatusType.TombstoneRcsGroupCreated
            or MessageStatusType.TombstoneMmsGroupCreated
            or MessageStatusType.MessageStatusTombstoneGroupProtocolSwitchE2EeToRcs
            or MessageStatusType.MessageStatusTombstoneProtocolSwitchE2EeToText
            or MessageStatusType.MessageStatusTombstoneProtocolSwitchE2EeToRcs => false,

        _ => null,
    };

    /// <summary>
    /// The status line under the owner's newest message.
    /// </summary>
    /// <param name="status">The message status.</param>
    /// <returns>"Sent", "Delivered", "Read", or empty for any other status.</returns>
    public static string StatusText(MessageStatusType status) => status switch
    {
        MessageStatusType.OutgoingComplete  => ChatCopy.Sent,
        MessageStatusType.OutgoingDelivered => ChatCopy.Delivered,
        MessageStatusType.OutgoingDisplayed => ChatCopy.Read,
        _                                   => string.Empty,
    };

    /// <summary>
    /// The reaction chip under a bubble, the way Google Messages shows it.
    /// </summary>
    /// <remarks>Up to 3 emoji, most used first (ties keep first-seen order), then the total count when above 1.</remarks>
    /// <param name="reactions">The message's reactions.</param>
    /// <returns>Text such as "👍" or "👍😂 3"; empty when nobody reacted.</returns>
    public static string ReactionSummary(IReadOnlyList<ReactionInfo> reactions)
    {
        ArgumentNullException.ThrowIfNull(reactions);

        // Count Each Emoji, Then Keep The Top 3 (OrderByDescending Is Stable)
        var counts = reactions
            .GroupBy(r => r.Emoji)
            .Select(g => (Emoji: g.Key, Count: g.Sum(r => r.ParticipantIds.Count)))
            .Where(e => e.Count > 0)
            .ToList();
        int total  = counts.Sum(e => e.Count);
        string top = string.Concat(counts.OrderByDescending(e => e.Count).Take(3).Select(e => e.Emoji));

        return total > 1 ? $"{top} {total.ToString(CultureInfo.CurrentCulture)}" : top;
    }

    // Empty, A Number Or Short Code (Digits And Dialing Marks Only), Or An Email Address (One Word With An @)
    private static bool IsNoName(string name) =>
        name.Length == 0
        || name.All(c => char.IsAsciiDigit(c) || c is '+' or '-' or '(' or ')' or '.' or ' ' or '#' or '*')
        || (name.Contains('@', StringComparison.Ordinal) && !name.Any(char.IsWhiteSpace));
}
