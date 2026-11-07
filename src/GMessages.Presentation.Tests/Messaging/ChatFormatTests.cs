using System.Globalization;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class ChatFormatTests
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// The badge of a pinned tile shows the message when it is the one unread message and has 1 or 2 words.
    /// Otherwise the badge shows the unread count. A read chat has no badge.
    /// </summary>
    [TestMethod]
    [DataRow(false, 3, "hi", "")]
    [DataRow(true, 1, "hi", "hi")]
    [DataRow(true, 0, "  sounds   good ", "sounds good")]
    [DataRow(true, 1, "see you soon", "1")]
    [DataRow(true, 1, "", "1")]
    [DataRow(true, 2, "hi", "2")]
    [DataRow(true, 5, "see you soon", "5")]
    public void UnreadBadge_ShortSingleMessageElseCount(bool unread, int count, string text, string expected) =>
        Assert.AreEqual(expected, ChatFormat.UnreadBadge(unread, count, text));

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
    [DataRow(false, 1L, "", "You: Image")]
    [DataRow(false, 0L, "", "Image")]
    [DataRow(true, 0L, "Rachel Green", "Rachel: Image")]
    public void Snippet_NoText_ShowsTheMediaLabel(bool isGroup, long fromMe, string displayName, string expected)
    {
        var c = new Conversation { ConversationID = "c1", IsGroupChat = isGroup, LatestMessage = new LatestMessage { FromMe = fromMe, DisplayName = displayName } };

        Assert.AreEqual(expected, ChatFormat.Snippet(ConversationInfo.From(c) with { SnippetMedia = "Image" }));
    }

    [TestMethod]
    public void Snippet_Caption_WinsOverTheMediaLabel()
    {
        var c = new Conversation { ConversationID = "c1", LatestMessage = new LatestMessage { DisplayContent = "look", FromMe = 1 } };

        Assert.AreEqual("You: look", ChatFormat.Snippet(ConversationInfo.From(c) with { SnippetMedia = "Image" }));
    }

    /// <summary>
    /// The tooltip line of an unread message starts with the first name of the sender in a group.
    /// A one-to-one chat has no name.
    /// </summary>
    [TestMethod]
    [DataRow(true, "Rachel: hi")]
    [DataRow(false, "hi")]
    public void UnreadLine_GroupHasSenderFirstName(bool isGroup, string expected)
    {
        var c = new Conversation { ConversationID = "c1", IsGroupChat = isGroup };
        c.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "p1" }, FullName = "Rachel Green" });
        var m = new Message { MessageID = "m1", ConversationID = "c1", ParticipantID = "p1" };
        m.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = "hi" } });

        Assert.AreEqual(expected, ChatFormat.UnreadLine(ConversationInfo.From(c), ChatMessage.From(m)));
    }

    [TestMethod]
    [DataRow("2026-10-29T15:04:00-04:00", "2026-10-29T18:00:00-04:00", "3:04 PM")]   // today: time
    [DataRow("2026-10-27T15:04:00-04:00", "2026-10-29T18:00:00-04:00", "Tue")]       // within 7 days: weekday
    [DataRow("2026-03-02T15:04:00-05:00", "2026-10-29T18:00:00-04:00", "Mar 2")]     // same year: month day
    [DataRow("2025-03-02T15:04:00-05:00", "2026-10-29T18:00:00-04:00", "3/2/25")]    // older: short date
    [DataRow("2026-10-30T02:00:00+00:00", "2026-10-29T18:00:00-04:00", "10:00 PM")]  // different offset: converted to the offset of now
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

        // Equal counts keep the order of first sight. Only the top 3 show, and the count includes everyone.
        Assert.AreEqual(Sad + Love + Like + " 4", ChatFormat.ReactionSummary([new ReactionInfo(Sad, ["p1"]), new ReactionInfo(Love, ["p2"]), new ReactionInfo(Like, ["p3"]), new ReactionInfo(Laugh, ["p4"])]));
    }

    /// <summary>
    /// The last message of a group from another person starts with the first name of the sender.
    /// Google shows it the same way, for example "Rachel: Awe!".
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
    /// An emoji symbol with a text style default gets VS16, so that it draws in color. Other text does not change.
    /// </summary>
    [TestMethod]
    [DataRow("", "")]
    [DataRow("\u263A", "\u263A\uFE0F")]                                    // Smiling face
    [DataRow("I \u2764 it \u2600", "I \u2764\uFE0F it \u2600\uFE0F")]       // Heart and sun
    [DataRow("\u270C", "\u270C\uFE0F")]                                    // Victory hand
    [DataRow("\u263A\uFE0F", "\u263A\uFE0F")]                              // Already in emoji style
    [DataRow("\u263A\uFE0E", "\u263A\uFE0E")]                              // The sender requests text style
    [DataRow("\u261D\U0001F3FD", "\u261D\U0001F3FD")]                      // A skin tone follows
    [DataRow("\u2764\u200D\U0001F525", "\u2764\uFE0F\u200D\U0001F525")]    // Heart on fire
    [DataRow("\U0001F600 1 # * \u00A9 \u00AE \u2122 \u2192", "\U0001F600 1 # * \u00A9 \u00AE \u2122 \u2192")]
    [DataRow("\U0001F441", "\U0001F441\uFE0F")]                            // Eye: astral, text default
    public void WithColorEmoji_AddsVs16ToTextDefaultEmoji(string text, string expected) =>
        Assert.AreEqual(expected, ChatFormat.WithColorEmoji(text));

    /// <summary>
    /// The lookup finds both ends of each range and finds nothing in the gaps between the ranges.
    /// </summary>
    [TestMethod]
    [DataRow("\u203C", "\u203C\uFE0F")]                      // First range
    [DataRow("\U0001F6F3", "\U0001F6F3\uFE0F")]              // Last range
    [DataRow("\u2199", "\u2199\uFE0F")]                      // End of a range
    [DataRow("\u219A", "\u219A")]                            // Just after that end
    [DataRow("\u203B", "\u203B")]                            // Just before the first range
    [DataRow("\U0001F6F4", "\U0001F6F4")]                    // Just after the last range, already in emoji style
    public void WithColorEmoji_RangeEdges(string text, string expected) =>
        Assert.AreEqual(expected, ChatFormat.WithColorEmoji(text));

    /// <summary>
    /// A message of only emoji, with spaces allowed, shows big and has no bubble.
    /// A letter, a digit or a plain symbol keeps the bubble.
    /// </summary>
    [TestMethod]
    [DataRow("\U0001F600", true)]                                          // Grinning face
    [DataRow(" \U0001F600 \U0001F44D\U0001F3FD ", true)]                   // Spaces and a skin tone
    [DataRow("❤", true)]                                              // Heart with a text default
    [DataRow("❤️‍\U0001F525", true)]                        // Heart on fire
    [DataRow("\U0001F1FA\U0001F1F8", true)]                                // A flag
    [DataRow("1️⃣", true)]                                       // Keycap 1
    [DataRow("⭐⚡", true)]                                        // Star and zap, already in emoji style
    [DataRow("", false)]
    [DataRow("   ", false)]
    [DataRow("ok \U0001F600", false)]
    [DataRow("1", false)]
    [DataRow("© ™ →", false)]                               // Copyright, trademark and an arrow
    [DataRow("★✓■", false)]                                 // Star, check mark and square, all plain symbols
    [DataRow("───", false)]                                 // Box drawing
    [DataRow("⠁", false)]                                             // Braille
    [DataRow("a️", false)]                                            // VS16 on a letter
    [DataRow("#️", false)]                                            // VS16 without a keycap
    [DataRow("☺︎", false)]                                       // The sender requests text style
    [DataRow("\U0001F1FA", false)]                                         // Half of a flag
    [DataRow("\U0001F130", false)]                                         // A boxed letter
    [DataRow("\U0001F004", true)]                                          // Mahjong red dragon, an emoji tile
    [DataRow("\uD83D", false)]                                             // A broken surrogate
    public void IsEmojiOnly(string text, bool expected) =>
        Assert.AreEqual(expected, ChatFormat.IsEmojiOnly(text));

    /// <summary>
    /// A selection in the shown text copies as the original text.
    /// The copy drops the VS16 that the display added and keeps a VS16 that the sender typed.
    /// </summary>
    [TestMethod]
    [DataRow("I \u2764 it", "\u2764\uFE0F it", "\u2764 it")]
    [DataRow("I \u2764 it", "I \u2764\uFE0F it", "I \u2764 it")]
    [DataRow("\u2764\uFE0F and \u2600", "\u2764\uFE0F and \u2600\uFE0F", "\u2764\uFE0F and \u2600")]
    [DataRow("plain", "lai", "lai")]
    [DataRow("I \u2764 it", "it", "it")]
    public void OriginalOf_DropsOnlyTheAddedVs16(string original, string selected, string expected) =>
        Assert.AreEqual(expected, ChatFormat.OriginalOf(original, selected));
}
