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
}
