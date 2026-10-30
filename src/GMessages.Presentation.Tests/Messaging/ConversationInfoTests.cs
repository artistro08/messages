using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class ConversationInfoTests
{
    [TestMethod]
    public void From_UsesMicrosecondTimestamps()
    {
        var c = new Conversation { ConversationID = "c1", Name = "Ann", LastMessageTimestamp = 1_761_700_000_000_000 };

        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_761_700_000_000), ConversationInfo.From(c).LastMessageTime);
    }

    [TestMethod]
    public void From_CopiesFieldsAndParticipants()
    {
        var c = new Conversation
        {
            ConversationID = "c1", Name = "Ann", IsGroupChat = true, Unread = true, Pinned = true,
            Status = ConversationStatus.Archived, DefaultOutgoingID = "me", LatestMessageID = "m9", AvatarHexColor = "#ff8800",
            LatestMessage = new LatestMessage { DisplayContent = "hi", FromMe = 1 },
        };
        c.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "p1" }, FullName = "Ann Lee", FirstName = "Ann", FormattedNumber = "+1 555" });

        ConversationInfo info = ConversationInfo.From(c);

        Assert.IsTrue(info.IsGroup && info.Unread && info.Pinned && info.SnippetFromMe);
        Assert.IsFalse(info.IsMainView);
        Assert.AreEqual("hi", info.Snippet);
        Assert.AreEqual("me", info.DefaultOutgoingId);
        Assert.AreEqual("m9", info.LatestMessageId);
        Assert.AreEqual("Ann Lee", info.FindParticipant("p1")?.FullName);
        Assert.AreEqual("+1 555", info.FindParticipant("p1")?.FormattedNumber);
        Assert.IsNull(info.FindParticipant("nope"));
    }

    /// <summary>
    /// The phone's mute field as the Plan 5 probe saw it: 7 = 1 (varint) when muted, absent when not.
    /// </summary>
    [TestMethod]
    public void From_MuteField_SetsIsMuted()
    {
        Conversation muted   = Conversation.Parser.ParseFrom([0x0A, 0x02, (byte)'c', (byte)'1', 0x38, 0x01]);
        Conversation unmuted = Conversation.Parser.ParseFrom([0x0A, 0x02, (byte)'c', (byte)'1']);

        Assert.IsTrue(ConversationInfo.MutedOf(muted));
        Assert.IsTrue(ConversationInfo.From(muted).IsMuted);
        Assert.IsFalse(ConversationInfo.From(unmuted).IsMuted);
    }

    [TestMethod]
    public void IsMainView_TrueOnlyWhenActive()
    {
        Assert.IsTrue(ConversationInfo.From(new Conversation { Status = ConversationStatus.Active }).IsMainView);
        Assert.IsFalse(ConversationInfo.From(new Conversation { Status = ConversationStatus.SpamFolder }).IsMainView);
    }
}
