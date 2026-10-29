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
}
