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
    /// A conversation with field 7 set to 1 (varint) is muted. A conversation without field 7 is not muted.
    /// The Plan 5 probe showed this phone behavior.
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

    /// <summary>
    /// An active one-on-one chat with only a "conversation created" line is empty and not in the main list.
    /// The phone creates this chat for a group MMS sender and shows it nowhere.
    /// A group with a created line, or a chat with a real message, stays in the list.
    /// </summary>
    [TestMethod]
    public void IsEmpty_WhenOnlyTheOneOnOneCreatedLineIsLeft()
    {
        static ConversationInfo With(MessageStatusType status) => ConversationInfo.From(new Conversation
        {
            Status        = ConversationStatus.Active,
            LatestMessage = new LatestMessage { LatestMessageStatus = new LatestMessageStatus { Status = status } },
        });

        Assert.IsTrue(With(MessageStatusType.TombstoneOneOnOneSmsCreated).IsEmpty);
        Assert.IsTrue(With(MessageStatusType.TombstoneOneOnOneRcsCreated).IsEmpty);
        Assert.IsTrue(With(MessageStatusType.TombstoneEncryptedOneOnOneRcsCreated).IsEmpty);
        Assert.IsFalse(With(MessageStatusType.TombstoneOneOnOneSmsCreated).IsMainView);

        Assert.IsFalse(With(MessageStatusType.TombstoneRcsGroupCreated).IsEmpty);
        Assert.IsFalse(With(MessageStatusType.IncomingComplete).IsEmpty);
        Assert.IsTrue(With(MessageStatusType.IncomingComplete).IsMainView);
        Assert.AreEqual(MessageStatusType.IncomingComplete, With(MessageStatusType.IncomingComplete).LatestMessageStatus);
        Assert.IsFalse(ConversationInfo.From(new Conversation { Status = ConversationStatus.Active }).IsEmpty, "no newest message known");
    }
}
