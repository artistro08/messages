using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class ChatMessageTests
{
    private static Message Make(MessageStatusType status, MessageInfo? info = null)
    {
        var m = new Message { MessageID = "m1", TmpID = "t1", ConversationID = "c1", ParticipantID = "p1", Timestamp = 1_761_700_000_000_000, MessageStatus = new MessageStatus { Status = status } };
        if (info is not null)
        {
            m.MessageInfo.Add(info);
        }

        return m;
    }

    private static MessageInfo Text(string t) => new() { MessageContent = new MessageContent { Content = t } };

    private static MessageInfo Media(MediaFormats f) => new() { MediaContent = new MediaContent { Format = f } };

    [TestMethod]
    public void From_Text()
    {
        ChatMessage m = ChatMessage.From(Make(MessageStatusType.IncomingComplete, Text("hello")));

        Assert.AreEqual(MessageKind.Text, m.Kind);
        Assert.AreEqual("hello", m.Text);
        Assert.AreEqual("t1", m.TmpId);
        Assert.IsFalse(m.FromMe);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_761_700_000_000), m.Time);
    }

    [TestMethod]
    [DataRow(MediaFormats.ImagePng, MessageKind.Photo)]
    [DataRow(MediaFormats.VideoMp4, MessageKind.Video)]
    [DataRow(MediaFormats.AudioAac, MessageKind.Audio)]
    [DataRow(MediaFormats.UnspecifiedType, MessageKind.File)]
    public void From_MediaKindComesFromFormat(MediaFormats format, MessageKind expected) =>
        Assert.AreEqual(expected, ChatMessage.From(Make(MessageStatusType.IncomingComplete, Media(format))).Kind);

    [TestMethod]
    public void From_NoInfoIsOther() =>
        Assert.AreEqual(MessageKind.Other, ChatMessage.From(Make(MessageStatusType.IncomingComplete)).Kind);

    [TestMethod]
    public void From_Status200To299IsTombstone() =>
        Assert.AreEqual(MessageKind.Tombstone, ChatMessage.From(Make(MessageStatusType.TombstoneParticipantJoined, Text("x"))).Kind);

    [TestMethod]
    [DataRow(MessageStatusType.OutgoingComplete, true)]
    [DataRow(MessageStatusType.OutgoingFailedGeneric, true)]
    [DataRow(MessageStatusType.IncomingComplete, false)]
    [DataRow(MessageStatusType.TombstoneSelfLeft, false)]
    public void From_FromMeForOutgoingStatuses(MessageStatusType status, bool expected) =>
        Assert.AreEqual(expected, ChatMessage.From(Make(status)).FromMe);

    [TestMethod]
    [DataRow(MessageStatusType.OutgoingFailedGeneric, true)]
    [DataRow(MessageStatusType.OutgoingFailedTooLarge, true)]
    [DataRow(MessageStatusType.OutgoingFailedToEncrypt, true)]
    [DataRow(MessageStatusType.OutgoingCanceled, true)]
    [DataRow(MessageStatusType.OutgoingSending, false)]
    [DataRow(MessageStatusType.OutgoingComplete, false)]
    [DataRow(MessageStatusType.IncomingDownloadFailed, false)]
    public void IsFailed(MessageStatusType status, bool expected) =>
        Assert.AreEqual(expected, ChatMessage.From(Make(status)).IsFailed);

    [TestMethod]
    public void From_ReactionsLikeTheConnector()
    {
        Message m = Make(MessageStatusType.IncomingComplete, Text("hi"));
        m.Reactions.Add(new ReactionEntry { Data = new ReactionData { Type = EmojiType.Custom, Unicode = "🦄" }, ParticipantIDs = { "p1" } });
        m.Reactions.Add(new ReactionEntry { Data = new ReactionData { Type = EmojiType.Emotify, Unicode = "x" }, ParticipantIDs = { "p2" } });
        m.Reactions.Add(new ReactionEntry { Data = new ReactionData { Type = EmojiType.Like, Unicode = "ignored" }, ParticipantIDs = { "p2", "p3" } });
        m.Reactions.Add(new ReactionEntry { Data = new ReactionData { Type = EmojiType.ReactionTypeUnspecified }, ParticipantIDs = { "p4" } });

        IReadOnlyList<ReactionInfo> reactions = ChatMessage.From(m).Reactions;

        Assert.AreEqual(2, reactions.Count);
        Assert.AreEqual("🦄", reactions[0].Emoji);
        Assert.AreEqual("p1", string.Join(",", reactions[0].ParticipantIds));
        Assert.AreEqual("👍", reactions[1].Emoji);
        Assert.AreEqual("p2,p3", string.Join(",", reactions[1].ParticipantIds));
    }

    [TestMethod]
    public void From_ReplyToId()
    {
        Message m      = Make(MessageStatusType.IncomingComplete, Text("hi"));
        m.ReplyMessage = new ReplyMessage { MessageID = "m0" };

        Assert.AreEqual("m0", ChatMessage.From(m).ReplyToId);
        Assert.AreEqual(string.Empty, ChatMessage.From(Make(MessageStatusType.IncomingComplete)).ReplyToId);
    }

    [TestMethod]
    [DataRow(1L, false)]
    [DataRow(2L, false)]
    [DataRow(3L, false)]
    [DataRow(4L, true)]
    [DataRow(0L, true)]
    public void From_TypeDecidesRcs(long type, bool expected)
    {
        Message m = Make(MessageStatusType.IncomingComplete, Text("hi"));
        m.Type    = type;

        Assert.AreEqual(expected, ChatMessage.From(m).IsRcs);
    }

    [TestMethod]
    public void From_EncryptionChangeFromStatus()
    {
        Assert.IsTrue(ChatMessage.From(Make(MessageStatusType.TombstoneProtocolSwitchToEncryptedRcs)).EncryptionChange);
        Assert.IsNull(ChatMessage.From(Make(MessageStatusType.IncomingComplete)).EncryptionChange);
    }
}
