using System.Globalization;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class ChatFormatTests
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    [TestMethod]
    public void Snippet_OwnMessage_HasYouPrefix()
    {
        var c = new Conversation { ConversationID = "c1", LatestMessage = new LatestMessage { DisplayContent = "hi", FromMe = 1 } };

        Assert.AreEqual("You: hi", ChatFormat.Snippet(ConversationInfo.From(c)));
    }

    [TestMethod]
    public void Snippet_TheirMessage_HasNoPrefix()
    {
        var c = new Conversation { ConversationID = "c1", LatestMessage = new LatestMessage { DisplayContent = "hi" } };

        Assert.AreEqual("hi", ChatFormat.Snippet(ConversationInfo.From(c)));
    }

    [TestMethod]
    [DataRow("2026-10-29T15:04:00-04:00", "2026-10-29T18:00:00-04:00", "3:04 PM")]   // today: time
    [DataRow("2026-10-27T15:04:00-04:00", "2026-10-29T18:00:00-04:00", "Tue")]       // within 7 days: weekday
    [DataRow("2026-03-02T15:04:00-05:00", "2026-10-29T18:00:00-04:00", "Mar 2")]     // same year: month day
    [DataRow("2025-03-02T15:04:00-05:00", "2026-10-29T18:00:00-04:00", "3/2/25")]    // older: short date
    [DataRow("2026-10-30T02:00:00+00:00", "2026-10-29T18:00:00-04:00", "10:00 PM")]  // other offset: compared in now's offset
    public void ListTime_MatchesGoogleStyle(string time, string now, string expected) =>
        Assert.AreEqual(expected, ChatFormat.ListTime(DateTimeOffset.Parse(time, CultureInfo.InvariantCulture), DateTimeOffset.Parse(now, CultureInfo.InvariantCulture), EnUs).Replace(' ', ' '));

    [TestMethod]
    [DataRow("  ann", "A")]
    [DataRow("", "")]
    [DataRow("59538", "")]
    [DataRow("+1 (555) 010-0100", "")]
    [DataRow("ann.lee@example.com", "")]
    [DataRow("😀", "😀")]
    [DataRow("2Pac", "2")]
    [DataRow("\U0001F600 Bo", "\U0001F600")]
    public void Initial(string name, string expected) => Assert.AreEqual(expected, ChatFormat.Initial(name));

    [TestMethod]
    public void ColorFromHex_ParsesWithAndWithoutHash()
    {
        Assert.AreEqual(0xFFFF8800u, ChatFormat.ColorFromHex("#ff8800", 1));
        Assert.AreEqual(0xFFFF8800u, ChatFormat.ColorFromHex("ff8800", 1));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("#xyz")]
    [DataRow("#ff88")]
    public void ColorFromHex_BadInputFallsBack(string hex) => Assert.AreEqual(7u, ChatFormat.ColorFromHex(hex, 7));

    [TestMethod]
    [DataRow(MessageStatusType.TombstoneEncryptedOneOnOneRcsCreated, true)]
    [DataRow(MessageStatusType.TombstoneProtocolSwitchToEncryptedRcs, true)]
    [DataRow(MessageStatusType.TombstoneProtocolSwitchToEncryptedRcsInfo, true)]
    [DataRow(MessageStatusType.MessageStatusTombstoneEncryptedGroupCreated, true)]
    [DataRow(MessageStatusType.MessageStatusTombstoneGroupProtocolSwitchRcsToE2Ee, true)]
    [DataRow(MessageStatusType.MessageStatusTombstoneProtocolSwitchTextToE2Ee, true)]
    [DataRow(MessageStatusType.MessageStatusTombstoneProtocolSwitchRcsToE2Ee, true)]
    [DataRow(MessageStatusType.TombstoneProtocolSwitchToText, false)]
    [DataRow(MessageStatusType.TombstoneProtocolSwitchToRcs, false)]
    [DataRow(MessageStatusType.TombstoneOneOnOneSmsCreated, false)]
    [DataRow(MessageStatusType.TombstoneOneOnOneRcsCreated, false)]
    [DataRow(MessageStatusType.TombstoneRcsGroupCreated, false)]
    [DataRow(MessageStatusType.TombstoneMmsGroupCreated, false)]
    [DataRow(MessageStatusType.MessageStatusTombstoneGroupProtocolSwitchE2EeToRcs, false)]
    [DataRow(MessageStatusType.MessageStatusTombstoneProtocolSwitchE2EeToText, false)]
    [DataRow(MessageStatusType.MessageStatusTombstoneProtocolSwitchE2EeToRcs, false)]
    [DataRow(MessageStatusType.IncomingComplete, null)]
    public void EncryptionOf_Table(MessageStatusType status, bool? expected) => Assert.AreEqual(expected, ChatFormat.EncryptionOf(status));

    [TestMethod]
    [DataRow(MessageStatusType.OutgoingComplete, "Sent")]
    [DataRow(MessageStatusType.OutgoingDelivered, "Delivered")]
    [DataRow(MessageStatusType.OutgoingDisplayed, "Read")]
    [DataRow(MessageStatusType.OutgoingSending, "")]
    [DataRow(MessageStatusType.IncomingComplete, "")]
    public void StatusText_SpecWords(MessageStatusType status, string expected) => Assert.AreEqual(expected, ChatFormat.StatusText(status));

    [TestMethod]
    public void ReactionSummary_LikeGoogle()
    {
        const string Like  = "👍";
        const string Laugh = "😂";
        const string Love  = "😍";
        const string Sad   = "😥";

        Assert.AreEqual(string.Empty, ChatFormat.ReactionSummary([]));
        Assert.AreEqual(Like, ChatFormat.ReactionSummary([new ReactionInfo(Like, ["p1"])]));
        Assert.AreEqual(Like + Laugh + " 3", ChatFormat.ReactionSummary([new ReactionInfo(Laugh, ["p1"]), new ReactionInfo(Like, ["p2", "p3"])]));

        // Ties Keep First-Seen Order, Then Only The Top 3 Show; The Count Is Everyone
        Assert.AreEqual(Sad + Love + Like + " 4", ChatFormat.ReactionSummary([new ReactionInfo(Sad, ["p1"]), new ReactionInfo(Love, ["p2"]), new ReactionInfo(Like, ["p3"]), new ReactionInfo(Laugh, ["p4"])]));
    }

    // =========================================================================
    // GROUP SNIPPETS AND COLOR EMOJI
    // =========================================================================

    /// <summary>
    /// A group's last message from someone else starts with their first name, as Google shows it ("Rachel: Awe!").
    /// </summary>
    [TestMethod]
    [DataRow(true, 0L, "Rachel Green", "Awe!", "Rachel: Awe!")]
    [DataRow(true, 0L, "+1 555-0100", "hi", "+1 555-0100: hi")]
    [DataRow(true, 0L, "", "hi", "hi")]
    [DataRow(true, 1L, "Rachel Green", "hi", "You: hi")]
    [DataRow(false, 0L, "Rachel Green", "hi", "hi")]
    public void Snippet_GroupNamesTheSender(bool isGroup, long fromMe, string displayName, string content, string expected)
    {
        var c = new Conversation
        {
            ConversationID = "c1",
            IsGroupChat    = isGroup,
            LatestMessage  = new LatestMessage { DisplayContent = content, FromMe = fromMe, DisplayName = displayName },
        };

        Assert.AreEqual(expected, ChatFormat.Snippet(ConversationInfo.From(c)));
    }

    [TestMethod]
    public void Snippet_ShowsColorEmoji()
    {
        var c = new Conversation { ConversationID = "c1", LatestMessage = new LatestMessage { DisplayContent = "\u263A" } };

        Assert.AreEqual("\u263A\uFE0F", ChatFormat.Snippet(ConversationInfo.From(c)));
    }

    /// <summary>
    /// Symbols that are emoji but default to text style get VS16, so they draw in color; anything else is left alone.
    /// </summary>
    [TestMethod]
    [DataRow("", "")]
    [DataRow("\u263A", "\u263A\uFE0F")]                                    // Smiling Face
    [DataRow("I \u2764 it \u2600", "I \u2764\uFE0F it \u2600\uFE0F")]       // Heart, Sun
    [DataRow("\u270C", "\u270C\uFE0F")]                                    // Victory Hand
    [DataRow("\u263A\uFE0F", "\u263A\uFE0F")]                              // Already Emoji Style
    [DataRow("\u263A\uFE0E", "\u263A\uFE0E")]                              // Text Style Asked For
    [DataRow("\u261D\U0001F3FD", "\u261D\U0001F3FD")]                      // A Skin Tone Follows
    [DataRow("\u2764\u200D\U0001F525", "\u2764\uFE0F\u200D\U0001F525")]    // Heart On Fire
    [DataRow("\U0001F600 1 # * \u00A9 \u00AE \u2122 \u2192", "\U0001F600 1 # * \u00A9 \u00AE \u2122 \u2192")]
    [DataRow("\U0001F441", "\U0001F441\uFE0F")]                            // Eye (Astral, Text Default)
    public void WithColorEmoji_AddsVs16ToTextDefaultEmoji(string text, string expected) =>
        Assert.AreEqual(expected, ChatFormat.WithColorEmoji(text));
}
