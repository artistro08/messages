using GMessages.Protocol.Proto;

namespace GMessages.Protocol;

/// <summary>
/// Maps reaction emoji to LibGM's <see cref="EmojiType"/> and back.
/// </summary>
/// <remarks>
/// Ported from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/gmproto/emojitype.go">gmproto/emojitype.go</see>.
/// The phone shows a reaction by its type when it knows it, and by its text when the type is
/// <see cref="EmojiType.Custom"/>, so both are sent.
/// </remarks>
public static class EmojiTypes
{
    /// <summary>
    /// Gets the emoji of a reaction type.
    /// </summary>
    /// <remarks>Ported from <c>EmojiType.Unicode</c>.</remarks>
    /// <param name="type">The reaction type.</param>
    /// <returns>The emoji, or an empty string for a type with no fixed emoji (custom, unspecified and so on).</returns>
    public static string Unicode(EmojiType type) => type switch
    {
        EmojiType.Like        => "\U0001F44D",
        EmojiType.Love        => "\U0001F60D",
        EmojiType.Laugh       => "\U0001F602",
        EmojiType.Surprised   => "\U0001F62E",
        EmojiType.Sad         => "\U0001F625",
        EmojiType.Angry       => "\U0001F620",
        EmojiType.Dislike     => "\U0001F44E",
        EmojiType.Questioning => "\U0001F914",
        EmojiType.CryingFace  => "\U0001F622",
        EmojiType.PoutingFace => "\U0001F621",
        EmojiType.RedHeart    => "❤️",
        _                     => "",
    };

    /// <summary>
    /// Gets the reaction type of an emoji.
    /// </summary>
    /// <remarks>Ported from <c>UnicodeToEmojiType</c>: the heart counts with or without its variation selector.</remarks>
    /// <param name="emoji">The emoji.</param>
    /// <returns>The type, or <see cref="EmojiType.Custom"/> for any other emoji.</returns>
    public static EmojiType FromUnicode(string emoji) => emoji switch
    {
        "\U0001F44D"               => EmojiType.Like,
        "\U0001F60D"               => EmojiType.Love,
        "\U0001F602"               => EmojiType.Laugh,
        "\U0001F62E"               => EmojiType.Surprised,
        "\U0001F625"               => EmojiType.Sad,
        "\U0001F620"               => EmojiType.Angry,
        "\U0001F44E"               => EmojiType.Dislike,
        "\U0001F914"               => EmojiType.Questioning,
        "\U0001F622"               => EmojiType.CryingFace,
        "\U0001F621"               => EmojiType.PoutingFace,
        "❤" or "❤️" => EmojiType.RedHeart,
        _                          => EmojiType.Custom,
    };

    /// <summary>
    /// Makes the reaction data for an emoji: the emoji as given and its type.
    /// </summary>
    /// <remarks>Ported from <c>MakeReactionData</c>.</remarks>
    /// <param name="emoji">The emoji.</param>
    /// <returns>The reaction data.</returns>
    public static ReactionData MakeReactionData(string emoji) => new() { Unicode = emoji, Type = FromUnicode(emoji) };
}
