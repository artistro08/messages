using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Tests.Main;

[TestClass]
public sealed class MessageBubbleViewModelTests
{
    private const string Like  = "\U0001F44D";
    private const string Laugh = "\U0001F602";

    private static ChatMessage Message(string text = "hi", params ReactionInfo[] reactions) =>
        new("m1", string.Empty, "c1", "p2", DateTimeOffset.UnixEpoch, false, MessageKind.Text, text, MessageStatusType.IncomingComplete)
        {
            Reactions = reactions,
            IsRcs     = true,
        };

    private static ConversationInfo Conversation(string defaultOutgoingId = "me1") =>
        new("c1", "Ann", false, ConversationStatus.Active, false, false, DateTimeOffset.UnixEpoch, string.Empty, false, string.Empty,
            [new ParticipantInfo("me1", "Me", "Me", string.Empty, true, null), new ParticipantInfo("p2", "Ann", "Ann", string.Empty, false, null)],
            defaultOutgoingId, "m1");

    [TestMethod]
    public void Bubble_MyReaction_FromMeParticipant()
    {
        var bubble = new MessageBubbleViewModel(Message("hi", new ReactionInfo(Laugh, ["p2"]), new ReactionInfo(Like, ["me1"])), Conversation());

        Assert.AreEqual(Like, bubble.MyReaction);
        Assert.IsTrue(bubble.HasMyReaction);
        Assert.IsTrue(bubble.HasReactions);
        Assert.AreEqual(Laugh + Like + " 2", bubble.ReactionsText);
    }

    [TestMethod]
    public void Bubble_MyReaction_FromDefaultOutgoingId()
    {
        var bubble = new MessageBubbleViewModel(Message("hi", new ReactionInfo(Like, ["sim2"])), Conversation("sim2"));

        Assert.AreEqual(Like, bubble.MyReaction);
    }

    [TestMethod]
    public void Bubble_NoConversation_NoMyReaction()
    {
        var bubble = new MessageBubbleViewModel(Message("hi", new ReactionInfo(Like, ["me1"])));

        Assert.IsNull(bubble.MyReaction);
        Assert.IsFalse(bubble.HasMyReaction);
        Assert.IsTrue(bubble.HasReactions);
    }

    [TestMethod]
    public void Bubble_ItemTypeName_RcsOrSms()
    {
        Assert.AreEqual(ChatCopy.ChatMessage, new MessageBubbleViewModel(Message()).ItemTypeName);
        Assert.AreEqual(ChatCopy.TextMessage, new MessageBubbleViewModel(Message() with { IsRcs = false }).ItemTypeName);
    }

    [TestMethod]
    public void Bubble_LinkUrl_FirstWebLink()
    {
        Assert.AreEqual(new Uri("https://example.com/a"), new MessageBubbleViewModel(Message("see https://example.com/a and http://b.example")).LinkUrl);
        Assert.IsNull(new MessageBubbleViewModel(Message("no link here")).LinkUrl);
    }
}
