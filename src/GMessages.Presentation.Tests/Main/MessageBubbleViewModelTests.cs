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

    [TestMethod]
    [DataRow("check this https://x.com", "check this", DisplayName = "A link at the end")]
    [DataRow("see https://x.com/a today", "see today", DisplayName = "A link in the middle")]
    [DataRow("https://x.com", "", DisplayName = "Only a link")]
    [DataRow("go to https://www.x.com/page now", "go to now", DisplayName = "A www. link")]
    [DataRow("www.y.com or https://x.com", "www.y.com or", DisplayName = "A bare www. link before it stays")]
    [DataRow("https://x.com and https://y.com", "and https://y.com", DisplayName = "Two links, only the first goes")]
    [DataRow("see https://x.com, ok", "see, ok", DisplayName = "Punctuation after it")]
    [DataRow("top\nhttps://x.com\nbottom", "top\nbottom", DisplayName = "A line of its own leaves no blank line")]
    [DataRow("top https://x.com\nbottom", "top\nbottom", DisplayName = "At a line's end")]
    public void BodyText_WithPreview_DropsTheLink(string text, string expected)
    {
        var bubble = new MessageBubbleViewModel(Message(text));
        Assert.AreEqual(text, bubble.BodyText, "Without a card the text shows whole.");

        bubble.SetPreview(new LinkPreview(bubble.LinkUrl!, "Title", "x.com", null));

        Assert.AreEqual(expected, bubble.BodyText);
        Assert.AreEqual(expected.Length > 0, bubble.HasBodyText);
        Assert.AreEqual(text, bubble.Text, "The message's own text stays whole.");
        Assert.AreEqual(text, bubble.ToString(), "Screen readers hear the whole text.");
    }

    [TestMethod]
    public void BodyText_NoPreview_Unchanged()
    {
        var bubble = new MessageBubbleViewModel(Message("see https://x.com today"));

        Assert.AreEqual("see https://x.com today", bubble.BodyText);
        Assert.IsTrue(bubble.HasBodyText);
        Assert.AreEqual(string.Empty, bubble.PreviewName);
    }

    [TestMethod]
    public void BodyText_PreviewArrivesLater_UpdatesAndRaisesChange()
    {
        var bubble  = new MessageBubbleViewModel(Message("see https://x.com today"));
        int changes = 0;
        bubble.PropertyChanged += (_, e) => changes += e.PropertyName?.Length == 0 ? 1 : 0;

        bubble.SetPreview(new LinkPreview(bubble.LinkUrl!, "Title", "x.com", null));

        Assert.AreEqual("see today", bubble.BodyText);
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public void BodyText_EchoKeepsPreview_StillWithoutLink()
    {
        var bubble = new MessageBubbleViewModel(Message("see https://x.com today"));
        bubble.SetPreview(new LinkPreview(bubble.LinkUrl!, "Title", "x.com", null));

        bubble.Update(Message("see https://x.com today"));

        Assert.AreEqual("see today", bubble.BodyText);
    }

    [TestMethod]
    public void BodyText_WithPreview_ShowsColorEmoji()
    {
        var bubble = new MessageBubbleViewModel(Message("hi ☺ https://x.com"));

        bubble.SetPreview(new LinkPreview(bubble.LinkUrl!, "Title", "x.com", null));

        Assert.AreEqual("hi ☺️", bubble.BodyText);
    }

    [TestMethod]
    public void PreviewName_TitleThenHost()
    {
        var bubble = new MessageBubbleViewModel(Message("see https://www.x.com/a"));

        bubble.SetPreview(new LinkPreview(bubble.LinkUrl!, "A Page", "x.com", null));
        Assert.AreEqual("A Page, www.x.com", bubble.PreviewName);

        bubble.SetPreview(new LinkPreview(bubble.LinkUrl!, string.Empty, "x.com", null));
        Assert.AreEqual("www.x.com", bubble.PreviewName);
    }

    [TestMethod]
    public void CanAct_FalseForTombstoneSendingFailedAndUnsent()
    {
        Assert.IsTrue(new MessageBubbleViewModel(Message()).CanAct);
        Assert.IsFalse(new MessageBubbleViewModel(Message() with { Kind = MessageKind.Tombstone }).CanAct);
        Assert.IsFalse(new MessageBubbleViewModel(Message() with { Status = MessageStatusType.OutgoingSending }).CanAct);
        Assert.IsFalse(new MessageBubbleViewModel(Message() with { Status = MessageStatusType.OutgoingFailedGeneric }).CanAct);
        Assert.IsFalse(new MessageBubbleViewModel(Message() with { Id = string.Empty, TmpId = "t1" }).CanAct);

        // Marked Failed After Sending
        var bubble = new MessageBubbleViewModel(Message());
        bubble.MarkFailed();
        Assert.IsFalse(bubble.CanAct);
    }
}
