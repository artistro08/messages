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
}
