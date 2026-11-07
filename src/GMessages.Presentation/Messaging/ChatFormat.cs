using System.Globalization;
using System.Text;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Turns conversation data into the short text that the chat list, the bubbles and the toasts show.
/// </summary>
public static class ChatFormat
{
    // The emoji that show in text style by default, as inclusive ranges. They have Emoji=Yes without
    // Emoji_Presentation in emoji-data.txt of Unicode 16.0. The table leaves out the keycap bases on purpose: the
    // digits, # and *. It also leaves out (c), (r) and TM. People usually mean these characters as text.
    private static readonly (int First, int Last)[] TextDefaultEmoji =
    [
        (0x203C, 0x203C), (0x2049, 0x2049), (0x2139, 0x2139), (0x2194, 0x2199), (0x21A9, 0x21AA), (0x2328, 0x2328),
        (0x23CF, 0x23CF), (0x23ED, 0x23EF), (0x23F1, 0x23F2), (0x23F8, 0x23FA), (0x24C2, 0x24C2), (0x25AA, 0x25AB),
        (0x25B6, 0x25B6), (0x25C0, 0x25C0), (0x25FB, 0x25FC), (0x2600, 0x2604), (0x260E, 0x260E), (0x2611, 0x2611),
        (0x2618, 0x2618), (0x261D, 0x261D), (0x2620, 0x2620), (0x2622, 0x2623), (0x2626, 0x2626), (0x262A, 0x262A),
        (0x262E, 0x262F), (0x2638, 0x263A), (0x2640, 0x2640), (0x2642, 0x2642), (0x265F, 0x2660), (0x2663, 0x2663),
        (0x2665, 0x2666), (0x2668, 0x2668), (0x267B, 0x267B), (0x267E, 0x267E), (0x2692, 0x2692), (0x2694, 0x2697),
        (0x2699, 0x2699), (0x269B, 0x269C), (0x26A0, 0x26A0), (0x26A7, 0x26A7), (0x26B0, 0x26B1), (0x26C8, 0x26C8),
        (0x26CF, 0x26CF), (0x26D1, 0x26D1), (0x26D3, 0x26D3), (0x26E9, 0x26E9), (0x26F0, 0x26F1), (0x26F4, 0x26F4),
        (0x26F7, 0x26F9), (0x2702, 0x2702), (0x2708, 0x2709), (0x270C, 0x270D), (0x270F, 0x270F), (0x2712, 0x2712),
        (0x2714, 0x2714), (0x2716, 0x2716), (0x271D, 0x271D), (0x2721, 0x2721), (0x2733, 0x2734), (0x2744, 0x2744),
        (0x2747, 0x2747), (0x2763, 0x2764), (0x27A1, 0x27A1), (0x2934, 0x2935), (0x2B05, 0x2B07), (0x3030, 0x3030),
        (0x303D, 0x303D), (0x3297, 0x3297), (0x3299, 0x3299), (0x1F170, 0x1F171), (0x1F17E, 0x1F17F), (0x1F202, 0x1F202),
        (0x1F237, 0x1F237), (0x1F321, 0x1F321), (0x1F324, 0x1F32C), (0x1F336, 0x1F336), (0x1F37D, 0x1F37D),
        (0x1F396, 0x1F397), (0x1F399, 0x1F39B), (0x1F39E, 0x1F39F), (0x1F3CB, 0x1F3CE), (0x1F3D4, 0x1F3DF),
        (0x1F3F3, 0x1F3F3), (0x1F3F5, 0x1F3F5), (0x1F3F7, 0x1F3F7), (0x1F43F, 0x1F43F), (0x1F441, 0x1F441),
        (0x1F4FD, 0x1F4FD), (0x1F549, 0x1F54A), (0x1F56F, 0x1F570), (0x1F573, 0x1F579), (0x1F587, 0x1F587),
        (0x1F58A, 0x1F58D), (0x1F590, 0x1F590), (0x1F5A5, 0x1F5A5), (0x1F5A8, 0x1F5A8), (0x1F5B1, 0x1F5B2),
        (0x1F5BC, 0x1F5BC), (0x1F5C2, 0x1F5C4), (0x1F5D1, 0x1F5D3), (0x1F5DC, 0x1F5DE), (0x1F5E1, 0x1F5E1),
        (0x1F5E3, 0x1F5E3), (0x1F5E8, 0x1F5E8), (0x1F5EF, 0x1F5EF), (0x1F5F3, 0x1F5F3), (0x1F5FA, 0x1F5FA),
        (0x1F6CB, 0x1F6CB), (0x1F6CD, 0x1F6CF), (0x1F6E0, 0x1F6E5), (0x1F6E9, 0x1F6E9), (0x1F6F0, 0x1F6F0),
        (0x1F6F3, 0x1F6F3),
    ];

    // The emoji that show in color by default, as inclusive ranges. They have Emoji_Presentation in emoji-data.txt of
    // Unicode 16.0. This table and TextDefaultEmoji together hold every emoji.
    private static readonly (int First, int Last)[] ColorDefaultEmoji =
    [
        (0x231A, 0x231B), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3), (0x25FD, 0x25FE), (0x2614, 0x2615),
        (0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693), (0x26A1, 0x26A1), (0x26AA, 0x26AB), (0x26BD, 0x26BE),
        (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA), (0x26F2, 0x26F3), (0x26F5, 0x26F5),
        (0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728), (0x274C, 0x274C),
        (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797), (0x27B0, 0x27B0), (0x27BF, 0x27BF),
        (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E),
        (0x1F191, 0x1F19A), (0x1F1E6, 0x1F1FF), (0x1F201, 0x1F201), (0x1F21A, 0x1F21A), (0x1F22F, 0x1F22F),
        (0x1F232, 0x1F236), (0x1F238, 0x1F23A), (0x1F250, 0x1F251), (0x1F300, 0x1F320), (0x1F32D, 0x1F335),
        (0x1F337, 0x1F37C), (0x1F37E, 0x1F393), (0x1F3A0, 0x1F3CA), (0x1F3CF, 0x1F3D3), (0x1F3E0, 0x1F3F0),
        (0x1F3F4, 0x1F3F4), (0x1F3F8, 0x1F43E), (0x1F440, 0x1F440), (0x1F442, 0x1F4FC), (0x1F4FF, 0x1F53D),
        (0x1F54B, 0x1F54E), (0x1F550, 0x1F567), (0x1F57A, 0x1F57A), (0x1F595, 0x1F596), (0x1F5A4, 0x1F5A4),
        (0x1F5FB, 0x1F64F), (0x1F680, 0x1F6C5), (0x1F6CC, 0x1F6CC), (0x1F6D0, 0x1F6D2), (0x1F6D5, 0x1F6D7),
        (0x1F6DC, 0x1F6DF), (0x1F6EB, 0x1F6EC), (0x1F6F4, 0x1F6FC), (0x1F7E0, 0x1F7EB), (0x1F7F0, 0x1F7F0),
        (0x1F90C, 0x1F93A), (0x1F93C, 0x1F945), (0x1F947, 0x1F9FF), (0x1FA70, 0x1FA7C), (0x1FA80, 0x1FA89),
        (0x1FA8F, 0x1FAC6), (0x1FACE, 0x1FADC), (0x1FADF, 0x1FAE9), (0x1FAF0, 0x1FAF8),
    ];

    /// <summary>
    /// Gives the unread badge of a pinned tile. It is the newest message when that message is the only unread one and
    /// has 1 or 2 words. Else it is the number of unread messages.
    /// </summary>
    /// <remarks>
    /// The owner chose this rule. An unread conversation with no live count counts as 1. Its messages came while the
    /// app was not running. A message with no text, such as media with no caption, shows its count.
    /// </remarks>
    /// <param name="unread">Whether the conversation is unread.</param>
    /// <param name="count">The live unread count from <see cref="ChatStore.LiveUnreadTexts"/>.</param>
    /// <param name="text">The text of the newest message, or empty.</param>
    /// <returns>The badge text, or empty for a read conversation.</returns>
    public static string UnreadBadge(bool unread, int count, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!unread)
        {
            return string.Empty;
        }

        count          = Math.Max(1, count);
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return count == 1 && words.Length is 1 or 2
            ? WithColorEmoji(string.Join(' ', words))
            : count.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Gives the snippet line of a conversation, as Google Messages shows it. The messages of the owner get "You: " in
    /// front. In a group, the messages of others get the first name of the sender in front, such as "Rachel: ".
    /// </summary>
    /// <remarks>
    /// Text-style emoji show in color, as <see cref="WithColorEmoji"/> makes them. The phone sends no text for a photo,
    /// video, voice message or file with no caption. The media label from <see cref="ConversationInfo.SnippetMedia"/>
    /// then takes the place of the text.
    /// </remarks>
    /// <param name="c">The conversation.</param>
    /// <returns>The text to show.</returns>
    public static string Snippet(ConversationInfo c)
    {
        ArgumentNullException.ThrowIfNull(c);

        string sender = c.IsGroup && !c.SnippetFromMe ? FirstName(c.SnippetSender) : string.Empty;
        string prefix = c.SnippetFromMe ? ChatCopy.YouPrefix : sender.Length > 0 ? sender + ": " : string.Empty;
        string body   = c.Snippet.Length > 0 ? c.Snippet : c.SnippetMedia;
        return WithColorEmoji(prefix + body);
    }

    /// <summary>
    /// Gives 1 unread message as the tooltip of the chat list shows it. As in <see cref="Snippet"/>, a group line gets
    /// the first name of the sender in front, such as "Rachel: ". Media with no caption shows its media label.
    /// </summary>
    /// <param name="c">The conversation of the message.</param>
    /// <param name="m">The received message.</param>
    /// <returns>The line, with the emoji not yet in color.</returns>
    public static string UnreadLine(ConversationInfo c, ChatMessage m)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(m);

        string sender = c.IsGroup ? FirstName(SenderName(c.Participants.FirstOrDefault(p => p.Id == m.ParticipantId))) : string.Empty;
        string body   = m is { Media: { } media, Text.Length: 0 } ? MediaLabel(media) : ShownText(m);
        return sender.Length > 0 ? sender + ": " + body : body;
    }

    /// <summary>
    /// Gives what the list row of a chat says about its unsent draft. It is the text, else the kinds of the files 1
    /// after the other. A kind is "Photo", or the name of a file. The row puts "Draft: " in front.
    /// </summary>
    /// <param name="text">The draft text.</param>
    /// <param name="files">The files picked to send with it.</param>
    /// <returns>The line, or empty when there is no draft.</returns>
    public static string DraftLine(string text, IReadOnlyList<PendingAttachment> files)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(files);

        // Blank text counts as none. Line breaks become spaces, so the row of 1 line shows the words.
        string line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length > 0
            ? line
            : string.Join(", ", files.Select(f => f.Kind == MessageKind.File ? f.Name : KindLabel(f.Kind)));
    }

    /// <summary>
    /// Gives the first word of a name. A number, short code or email address has no first name, so it comes back whole.
    /// </summary>
    /// <param name="name">The name as the phone gives it.</param>
    /// <returns>The first name, or empty for no name.</returns>
    public static string FirstName(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();

        return IsNoName(trimmed) ? trimmed : trimmed.Split(' ', 2)[0];
    }

    /// <summary>
    /// Shows in color the emoji that show in text style by default, such as U+263A, U+2764, U+2600 and U+270C. The
    /// method adds VS16, which is U+FE0F, after them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without VS16, those symbols draw as plain black glyphs from the text font. The phone draws them in color with
    /// Noto Color Emoji in all cases. The method does not change a symbol that already has VS16 or takes a skin tone.
    /// It also does not change a symbol that asks for text style with VS15, which is U+FE0E. The digits, # and *, (c),
    /// (r) and TM also stay as they are.
    /// </para>
    /// <para>The result is only for display. The text that the app sends or copies stays as typed.</para>
    /// </remarks>
    /// <param name="text">The text.</param>
    /// <returns>The text with VS16 added where needed.</returns>
    public static string WithColorEmoji(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // The method copies the text only at the first VS16 that it adds. Most text comes back unchanged.
        StringBuilder? built = null;
        int i = 0;
        while (i < text.Length)
        {
            int length = char.IsSurrogatePair(text, i) ? 2 : 1;
            int code   = length == 2 ? char.ConvertToUtf32(text, i) : text[i];
            built?.Append(text, i, length);
            i += length;

            if (IsTextDefaultEmoji(code) && !TakesNoVs16(text, i))
            {
                built ??= new StringBuilder(text, 0, i, text.Length + 8);
                built.Append('\uFE0F');
            }
        }

        return built?.ToString() ?? text;
    }

    /// <summary>
    /// Tells whether a message is only emoji, with spaces between them allowed. The chat shows such a message big, with
    /// no bubble around it.
    /// </summary>
    /// <remarks>
    /// Each shown character must draw as a color emoji. A shown character is a text element, so a flag or a skin-toned
    /// or joined emoji counts as 1. The element must start with a Unicode emoji and not ask for text style with VS15.
    /// The Unicode emoji include those that <see cref="WithColorEmoji"/> shows in color with VS16. A keycap also
    /// counts: a digit, # or * with U+20E3. Plain symbols such as ★, ✓ and box drawing keep the bubble. Half a flag
    /// and boxed letters also keep it.
    /// </remarks>
    /// <param name="text">The text of the message.</param>
    /// <returns>True when the text has at least 1 emoji and nothing else but spaces.</returns>
    public static bool IsEmojiOnly(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (!string.IsNullOrWhiteSpace(element) && !IsEmoji(element))
            {
                return false;
            }
        }

        return true;
    }

    // Tells whether 1 text element draws as an emoji.
    private static bool IsEmoji(string element)
    {
        // VS15 asks for text style, which draws a glyph of 1 color.
        if (element.Contains('\uFE0E', StringComparison.Ordinal))
        {
            return false;
        }

        // A keycap is a digit, # or * with U+20E3. TextDefaultEmoji leaves out the bases themselves.
        if (element.Contains('\u20E3', StringComparison.Ordinal))
        {
            return element[0] is (>= '0' and <= '9') or '#' or '*';
        }

        // A broken surrogate decodes as U+FFFD, which is no emoji. A regional indicator draws a flag only in a pair.
        Rune.DecodeFromUtf16(element, out Rune rune, out int used);
        int code = rune.Value;
        return code is >= 0x1F1E6 and <= 0x1F1FF
            ? element.Length > used
            : InRanges(ColorDefaultEmoji, code) || IsTextDefaultEmoji(code);
    }

    /// <summary>
    /// Gives the part of the original text that a selection in the shown text covers. Thus a copy holds none of the
    /// VS16 that <see cref="WithColorEmoji"/> added for display.
    /// </summary>
    /// <remarks>
    /// The method finds the first match of the selection in the shown text. It then maps both ends back to the
    /// original, where each character is the same except the added VS16. A selection that is not in the shown text
    /// comes back as it is.
    /// </remarks>
    /// <param name="original">The text as sent.</param>
    /// <param name="selected">The selected part of <c>WithColorEmoji(original)</c>.</param>
    /// <returns>The same part of <paramref name="original"/>.</returns>
    public static string OriginalOf(string original, string selected)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(selected);

        string shown = WithColorEmoji(original);
        int start    = selected.Length == 0 ? -1 : shown.IndexOf(selected, StringComparison.Ordinal);
        if (start < 0 || ReferenceEquals(shown, original))
        {
            return selected;
        }

        // The loop walks both texts together. A VS16 in the shown text with none at the same place in the original is
        // an added VS16.
        int end  = start + selected.Length;
        int from = -1;
        int to   = original.Length;
        int j    = 0;
        for (int i = 0; i <= shown.Length; i++)
        {
            if (i == start)
            {
                from = j;
            }

            if (i == end)
            {
                to = j;
                break;
            }

            bool added = shown[i] == '\uFE0F' && (j >= original.Length || original[j] != '\uFE0F');
            if (!added)
            {
                j++;
            }
        }

        return original[from..to];
    }

    /// <summary>
    /// Gives the list time in the current culture, as
    /// <see cref="ListTime(DateTimeOffset, DateTimeOffset, CultureInfo)"/> does.
    /// </summary>
    /// <param name="time">When the message arrived.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The text to show.</returns>
    public static string ListTime(DateTimeOffset time, DateTimeOffset now) => ListTime(time, now, CultureInfo.CurrentCulture);

    /// <summary>
    /// Gives the list time. It is the time for today, or the weekday within the last 7 days. Else it is the month and
    /// day for this year, or a short date.
    /// </summary>
    /// <remarks>
    /// The method converts <paramref name="time"/> to the offset of <paramref name="now"/> before it compares the days.
    /// </remarks>
    /// <param name="time">When the message arrived.</param>
    /// <param name="now">The current time.</param>
    /// <param name="culture">The culture to format with.</param>
    /// <returns>The text to show.</returns>
    public static string ListTime(DateTimeOffset time, DateTimeOffset now, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        DateTime local = time.ToOffset(now.Offset).DateTime;
        int daysAgo    = (now.Date - local.Date).Days;

        if (daysAgo <= 0)
        {
            return local.ToString("t", culture);
        }

        if (daysAgo < 7)
        {
            return local.ToString("ddd", culture);
        }

        if (local.Year == now.Year)
        {
            return local.ToString("MMM d", culture);
        }

        // The short date gets a year of 2 digits.
        string pattern = culture.DateTimeFormat.ShortDatePattern.Replace("yyyy", "yy", StringComparison.Ordinal);
        return local.ToString(pattern, culture);
    }

    /// <summary>
    /// Gives the first visible character of a name, for avatars.
    /// </summary>
    /// <remarks>
    /// A phone number, a short code such as "59538" or an email address is no name. The phone shows it for a person
    /// that nobody saved. It gets an empty initial, and the avatar shows the generic person icon. Any other name keeps
    /// its first character. This includes a name of only emoji, and a name that starts with a digit, such as "2Pac".
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
    /// Gives the label that takes the place of the text of a message that has no text.
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
    /// Names the media of a message: "GIF" for a GIF, "Image" for any other picture, and "Video". A file gets its name.
    /// Else the label is the kind, such as "Voice message" or "File".
    /// </summary>
    /// <remarks>The reply quotes and the list snippet both use this method, so they name media the same way.</remarks>
    /// <param name="media">The media.</param>
    /// <returns>The label.</returns>
    public static string MediaLabel(MediaPart media)
    {
        ArgumentNullException.ThrowIfNull(media);

        return media.Kind switch
        {
            MessageKind.Photo when media.Mime == "image/gif" => ChatCopy.QuoteGif,
            MessageKind.Photo                                => ChatCopy.QuoteImage,
            MessageKind.Video                                => ChatCopy.QuoteVideo,
            MessageKind.File when media.Name.Length > 0      => media.Name,
            _                                                => KindLabel(media.Kind),
        };
    }

    /// <summary>
    /// Gives the text that stands for a message in the bubble, the notification and the reply quote.
    /// </summary>
    /// <remarks>
    /// It is the text of a text message. It is the caption of a media message or the words of a system line when they
    /// have them. Else it is <see cref="KindLabel"/>.
    /// </remarks>
    /// <param name="m">The message.</param>
    /// <returns>The text to show.</returns>
    public static string ShownText(ChatMessage m)
    {
        ArgumentNullException.ThrowIfNull(m);

        return m.Kind == MessageKind.Text || ((m.Media is not null || m.Kind == MessageKind.Tombstone) && m.Text.Length > 0) ? m.Text : KindLabel(m.Kind);
    }

    /// <summary>
    /// Gives the name of a participant: the full name, else the first name, else the phone number.
    /// </summary>
    /// <param name="p">The participant, or null.</param>
    /// <returns>The name, or an empty string when there is none.</returns>
    public static string SenderName(ParticipantInfo? p) =>
        p is null ? string.Empty
        : !string.IsNullOrEmpty(p.FullName) ? p.FullName
        : !string.IsNullOrEmpty(p.FirstName) ? p.FirstName
        : p.FormattedNumber ?? string.Empty;

    /// <summary>
    /// Tells whether a message stays out of the chat. A placeholder from <see cref="ChatMessage.IsPlaceholder"/> stays
    /// out. A system line also stays out, except a change to the name of a group.
    /// </summary>
    /// <remarks>
    /// A name change, changed or cleared, shows as a centered gray line in the words of Google. The other system lines
    /// only control the lock through <see cref="EncryptionOf"/>.
    /// </remarks>
    /// <param name="m">The message.</param>
    /// <returns>True for a hidden placeholder or system line.</returns>
    public static bool IsHiddenLine(ChatMessage m)
    {
        ArgumentNullException.ThrowIfNull(m);

        return m.IsPlaceholder || (m.Kind == MessageKind.Tombstone
            && !(m.Text.Length > 0 && m.Status is MessageStatusType.TombstoneGroupRenamedLocal or MessageStatusType.TombstoneGroupRenamedGlobal or MessageStatusType.TombstoneGroupNameClearedGlobal));
    }

    /// <summary>
    /// Gives what a system line says about end-to-end encryption.
    /// </summary>
    /// <remarks>
    /// The method knows the protocol tombstones that switch a chat into or out of encryption. The newest one decides
    /// whether the chat shows the lock, when the conversation itself does not say. The flag of the conversation in
    /// <see cref="ConversationInfo.IsEncrypted"/>, field 39, comes first.
    /// </remarks>
    /// <param name="status">The message status.</param>
    /// <returns>
    /// True when the chat is now encrypted, false when it no longer is, or null when the status says nothing.
    /// </returns>
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
    /// Gives the status line under the newest message of the owner.
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
    /// Gives the reaction chip under a bubble, as Google Messages shows it.
    /// </summary>
    /// <remarks>
    /// The chip shows up to 3 emoji, the most used first, and ties keep the order in which they came. The total count
    /// follows when it is above 1.
    /// </remarks>
    /// <param name="reactions">The reactions of the message.</param>
    /// <returns>Text such as "👍" or "👍😂 3", or empty when nobody reacted.</returns>
    public static string ReactionSummary(IReadOnlyList<ReactionInfo> reactions)
    {
        ArgumentNullException.ThrowIfNull(reactions);

        List<(string Emoji, int Count)> counts = RankedCounts(reactions);
        int total                              = counts.Sum(e => e.Count);
        string top                             = string.Concat(counts.Take(3).Select(e => e.Emoji));

        return total > 1 ? $"{top} {total.ToString(CultureInfo.CurrentCulture)}" : top;
    }

    /// <summary>
    /// Ranks the emoji of a message as <see cref="ReactionSummary"/> shows them. The most used come first, and ties
    /// keep the order in which they came.
    /// </summary>
    /// <param name="reactions">The reactions of the message.</param>
    /// <returns>Each emoji that anyone reacted with, ranked, or empty when nobody reacted.</returns>
    public static IReadOnlyList<string> RankedEmoji(IReadOnlyList<ReactionInfo> reactions)
    {
        ArgumentNullException.ThrowIfNull(reactions);

        return [.. RankedCounts(reactions).Select(e => e.Emoji)];
    }

    // Counts each emoji, the most used first. OrderByDescending is stable, so ties keep the order in which they came.
    private static List<(string Emoji, int Count)> RankedCounts(IReadOnlyList<ReactionInfo> reactions) =>
        [.. reactions
            .GroupBy(r => r.Emoji)
            .Select(g => (Emoji: g.Key, Count: g.Sum(r => r.ParticipantIds.Count)))
            .Where(e => e.Count > 0)
            .OrderByDescending(e => e.Count)];

    private static bool IsTextDefaultEmoji(int code) => InRanges(TextDefaultEmoji, code);

    // A binary search over the sorted ranges, with no allocation. It runs for each shown character.
    private static bool InRanges((int First, int Last)[] ranges, int code)
    {
        int low  = 0;
        int high = ranges.Length - 1;
        while (low <= high)
        {
            int middle            = (low + high) / 2;
            (int first, int last) = ranges[middle];
            if (code < first)
            {
                high = middle - 1;
            }
            else if (code > last)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    // Tells whether VS16, VS15 or a skin tone modifier from U+1F3FB to U+1F3FF follows.
    private static bool TakesNoVs16(string text, int at) =>
        at < text.Length
        && (text[at] is '\uFE0F' or '\uFE0E' || (char.IsSurrogatePair(text, at) && char.ConvertToUtf32(text, at) is >= 0x1F3FB and <= 0x1F3FF));

    // Tells whether a name is empty, a number or a short code, or an email address. A number or a short code has only
    // digits and dial marks. An email address is 1 word with an @.
    private static bool IsNoName(string name) =>
        name.Length == 0
        || name.All(c => char.IsAsciiDigit(c) || c is '+' or '-' or '(' or ')' or '.' or ' ' or '#' or '*')
        || (name.Contains('@', StringComparison.Ordinal) && !name.Any(char.IsWhiteSpace));
}
