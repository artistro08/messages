using System.Text;
using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class EmojiCatalogTests
{
    // Code Points In The Catalog That Show As Plain Text Unless U+FE0F Follows (Unicode's Emoji_Presentation=No)
    private static readonly HashSet<int> TextByDefault =
    [
        0x00A9, 0x00AE, 0x0023, 0x203C, 0x2049, 0x2122, 0x2328, 0x2600, 0x2603, 0x2611, 0x261D, 0x262E, 0x262F, 0x267B,
        0x26A0, 0x26F0, 0x2702, 0x2708, 0x2709, 0x270C, 0x270D, 0x270F, 0x2714, 0x2744, 0x2763, 0x2764, 0x27A1, 0x2B05,
        0x2B06, 0x2B07, 0x1F321, 0x1F336, 0x1F37D, 0x1F3CB, 0x1F3CD, 0x1F3CE, 0x1F3D5, 0x1F3D6, 0x1F3DD, 0x1F3F3,
        0x1F441, 0x1F56F, 0x1F576, 0x1F590, 0x1F5A5, 0x1F5A8, 0x1F5FA, 0x1F6CB, 0x1F6CF, 0x1F6E0,
    ];

    [TestMethod]
    public void Search_Eyes_FindsEyesAndEye()
    {
        List<string> found = Found("eyes");

        CollectionAssert.Contains(found, "👀");
        CollectionAssert.Contains(found, "😍");
        CollectionAssert.Contains(found, "👁️");
    }

    [TestMethod]
    public void Search_Eye_FindsEyeRollAndEyes()
    {
        List<string> found = Found("EYE");

        CollectionAssert.Contains(found, "🙄");
        CollectionAssert.Contains(found, "👀");
    }

    [TestMethod]
    public void Search_EveryWordMustMatch_NothingTypedIsEverything()
    {
        CollectionAssert.AreEqual(new List<string> { "😻" }, Found("cat heart"));
        Assert.IsEmpty(Found("zzzqqq"));
        Assert.AreSame(EmojiCatalog.Categories, EmojiCatalog.Search("  "));
    }

    [TestMethod]
    public void Categories_TextDefaultEmoji_CarryTheEmojiSelector()
    {
        foreach ((string _, IReadOnlyList<(string Emoji, string Words)> emoji) in EmojiCatalog.Categories)
        {
            foreach ((string e, string _) in emoji)
            {
                Rune[] runes = e.EnumerateRunes().ToArray();
                if (TextByDefault.Contains(runes[0].Value))
                {
                    Assert.IsTrue(runes.Length > 1 && runes[1].Value == 0xFE0F, $"U+{runes[0].Value:X4} has no U+FE0F after it.");
                }
            }
        }
    }

    // The Emoji A Search Finds, In Order
    private static List<string> Found(string query) =>
        EmojiCatalog.Search(query).SelectMany(c => c.Emoji).Select(e => e.Emoji).ToList();
}
