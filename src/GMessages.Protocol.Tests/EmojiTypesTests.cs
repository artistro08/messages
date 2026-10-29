using GMessages.Protocol.Proto;

namespace GMessages.Protocol.Tests;

/// <summary>
/// Checks <see cref="EmojiTypes"/> against every case of <c>gmproto/emojitype.go</c>, in both directions.
/// </summary>
/// <remarks>
/// C#-only. The pairs were copied from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/gmproto/emojitype.go">emojitype.go</see>.
/// </remarks>
[TestClass]
public class EmojiTypesTests
{
    /// <summary>
    /// Each Go case maps the type to its emoji and the emoji back to the type.
    /// </summary>
    /// <param name="type">The emoji type.</param>
    /// <param name="emoji">Go's emoji for it.</param>
    [TestMethod]
    [DataRow(EmojiType.Like, "\U0001F44D")]
    [DataRow(EmojiType.Love, "\U0001F60D")]
    [DataRow(EmojiType.Laugh, "\U0001F602")]
    [DataRow(EmojiType.Surprised, "\U0001F62E")]
    [DataRow(EmojiType.Sad, "\U0001F625")]
    [DataRow(EmojiType.Angry, "\U0001F620")]
    [DataRow(EmojiType.Dislike, "\U0001F44E")]
    [DataRow(EmojiType.Questioning, "\U0001F914")]
    [DataRow(EmojiType.CryingFace, "\U0001F622")]
    [DataRow(EmojiType.PoutingFace, "\U0001F621")]
    [DataRow(EmojiType.RedHeart, "❤️")]
    public void EmojiTypes_MatchGo(EmojiType type, string emoji)
    {
        Assert.AreEqual(emoji, EmojiTypes.Unicode(type));
        Assert.AreEqual(type, EmojiTypes.FromUnicode(emoji));
    }

    /// <summary>
    /// The heart without the variation selector is a red heart too, as in Go.
    /// </summary>
    [TestMethod]
    public void FromUnicode_BareHeart_IsRedHeart() => Assert.AreEqual(EmojiType.RedHeart, EmojiTypes.FromUnicode("❤"));

    /// <summary>
    /// Anything Go has no case for is custom.
    /// </summary>
    /// <param name="emoji">An emoji with no case.</param>
    [TestMethod]
    [DataRow("\U0001F525")]
    [DataRow("")]
    [DataRow("ok")]
    public void FromUnicode_Unknown_IsCustom(string emoji) => Assert.AreEqual(EmojiType.Custom, EmojiTypes.FromUnicode(emoji));

    /// <summary>
    /// Types with no case give an empty string, as Go's default branch does.
    /// </summary>
    /// <param name="type">A type with no case.</param>
    [TestMethod]
    [DataRow(EmojiType.ReactionTypeUnspecified)]
    [DataRow(EmojiType.Custom)]
    [DataRow(EmojiType.Emotify)]
    public void Unicode_NoCase_IsEmpty(EmojiType type) => Assert.AreEqual("", EmojiTypes.Unicode(type));

    /// <summary>
    /// Reaction data carries the emoji as given and its type.
    /// </summary>
    [TestMethod]
    public void MakeReactionData_CarriesEmojiAndType()
    {
        ReactionData known  = EmojiTypes.MakeReactionData("❤");
        ReactionData custom = EmojiTypes.MakeReactionData("\U0001F525");

        Assert.AreEqual("❤", known.Unicode);
        Assert.AreEqual(EmojiType.RedHeart, known.Type);
        Assert.AreEqual("\U0001F525", custom.Unicode);
        Assert.AreEqual(EmojiType.Custom, custom.Type);
    }
}
