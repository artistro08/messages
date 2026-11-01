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

    private static ConversationInfo Group() =>
        new("c1", "Team", true, ConversationStatus.Active, false, false, DateTimeOffset.UnixEpoch, string.Empty, false, string.Empty,
            [
                new ParticipantInfo("me1", "Me", "Me", string.Empty, true, null),
                new ParticipantInfo("p-bo", "Bo Park", "Bo", string.Empty, false, "+1 555-0002"),
                new ParticipantInfo("p-cy", string.Empty, "Cy", string.Empty, false, "+1 555-0003"),
                new ParticipantInfo("p-dee", string.Empty, string.Empty, string.Empty, false, "+1 555-0004"),
                new ParticipantInfo("p-none", string.Empty, string.Empty, string.Empty, false, null),
            ],
            "me1", "m1");

    [TestMethod]
    public void Reactors_MeShowsYou()
    {
        var bubble = new MessageBubbleViewModel(Message("hi", new ReactionInfo(Like, ["me1"])), Group());

        CollectionAssert.AreEqual(new[] { (Like, ChatCopy.You) }, bubble.Reactors().ToArray());
        Assert.IsTrue(bubble.IsGroupChat);
    }

    [TestMethod]
    public void Reactors_UnknownOrNamelessSkipped()
    {
        var bubble = new MessageBubbleViewModel(Message("hi", new ReactionInfo(Like, ["p-ghost", "p-none", "p-bo"])), Group());

        CollectionAssert.AreEqual(new[] { (Like, "Bo Park") }, bubble.Reactors().ToArray());
    }

    [TestMethod]
    public void Reactors_NameFallbacks_FullFirstNumber()
    {
        var bubble = new MessageBubbleViewModel(Message("hi", new ReactionInfo(Like, ["p-bo", "p-cy", "p-dee"])), Group());

        CollectionAssert.AreEqual(new[] { (Like, "Bo Park"), (Like, "Cy"), (Like, "+1 555-0004") }, bubble.Reactors().ToArray());
    }

    [TestMethod]
    public void Reactors_OrderedAsThePillRanksEmoji()
    {
        var bubble = new MessageBubbleViewModel(
            Message("hi", new ReactionInfo(Laugh, ["p-cy"]), new ReactionInfo(Like, ["p-bo", "me1"])), Group());

        CollectionAssert.AreEqual(new[] { (Like, "Bo Park"), (Like, ChatCopy.You), (Laugh, "Cy") }, bubble.Reactors().ToArray());
        Assert.AreEqual(Like + Laugh + " 3", bubble.ReactionsText);
    }

    [TestMethod]
    public void IsGroupChat_FalseOneOnOneOrUnknown()
    {
        Assert.IsFalse(new MessageBubbleViewModel(Message(), Conversation()).IsGroupChat);
        Assert.IsFalse(new MessageBubbleViewModel(Message()).IsGroupChat);
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

    private static ChatMessage MediaMessage(MessageKind kind, string caption = "") =>
        Message(caption) with
        {
            Kind  = kind,
            Media = new MediaPart("media-1", [], string.Empty, [], [], "clip.mp4", 1234, 400, 300, kind, "video/mp4"),
        };

    [TestMethod]
    public void Bubble_Photo_NoCaption_ShowsNoText()
    {
        var bubble = new MessageBubbleViewModel(MediaMessage(MessageKind.Photo) with { Text = string.Empty });

        Assert.IsTrue(bubble.HasPhoto);
        Assert.IsFalse(bubble.HasFileCard);
        Assert.IsTrue(bubble.HasMediaFile);
        Assert.IsFalse(bubble.HasBodyText);
        Assert.AreEqual(ChatCopy.Photo, bubble.PhotoName);
        Assert.AreEqual(ChatCopy.Photo, bubble.ToString());
    }

    [TestMethod]
    public void Bubble_CaptionlessMedia_HasNoCopy()
    {
        var bare     = new MessageBubbleViewModel(MediaMessage(MessageKind.Photo) with { Text = string.Empty });
        var caption  = new MessageBubbleViewModel(MediaMessage(MessageKind.Photo, "At the beach"));
        var file     = new MessageBubbleViewModel(MediaMessage(MessageKind.File) with { Text = string.Empty });
        var text     = new MessageBubbleViewModel(Message("hi"));

        Assert.IsFalse(bare.HasCopyText);
        Assert.IsTrue(caption.HasCopyText);
        Assert.IsFalse(file.HasCopyText);
        Assert.IsTrue(text.HasCopyText);
    }

    [TestMethod]
    public void Bubble_ReceivedProgram_CannotOpen()
    {
        ChatMessage m = MediaMessage(MessageKind.File);
        var program   = new MessageBubbleViewModel(m with { Media = m.Media! with { Name = "setup.exe", Mime = "application/octet-stream" } });
        var document  = new MessageBubbleViewModel(m with { Media = m.Media! with { Name = "notes.pdf", Mime = "application/pdf" } });

        Assert.IsTrue(program.HasMediaFile);
        Assert.IsFalse(program.CanOpenMedia);
        Assert.IsTrue(document.CanOpenMedia);
    }

    [TestMethod]
    [DataRow(true, false, false, true)]
    [DataRow(true, true, false, false)]
    [DataRow(true, false, true, false)]
    [DataRow(false, false, false, false)]
    public void FocusRing_KeyboardOnly_NotInHighContrast(bool focused, bool byPointer, bool highContrast, bool expected) =>
        Assert.AreEqual(expected, MessageBubbleViewModel.ShowsFocusRing(focused, byPointer, highContrast));

    [TestMethod]
    public void Bubble_PhotoWithCaption_ShowsCaption()
    {
        var bubble = new MessageBubbleViewModel(MediaMessage(MessageKind.Photo, "At the beach"));

        Assert.AreEqual("At the beach", bubble.BodyText);
        Assert.AreEqual("At the beach", bubble.PhotoName);
    }

    [TestMethod]
    public void Bubble_File_IsCard_Video_FillsBubble()
    {
        var video = new MessageBubbleViewModel(MediaMessage(MessageKind.Video) with { Text = string.Empty });
        var file  = new MessageBubbleViewModel(MediaMessage(MessageKind.File) with { Text = string.Empty });

        Assert.IsTrue(video.HasVideo);
        Assert.IsTrue(video.FillsBubble);
        Assert.IsTrue(video.HasMediaFile);
        Assert.IsFalse(video.HasFileCard);
        Assert.IsFalse(video.HasPhoto);
        Assert.AreEqual(ChatCopy.Video, video.VideoName);
        Assert.IsFalse(video.HasBodyText);

        Assert.IsTrue(file.HasFileCard);
        Assert.IsFalse(file.HasVideo);
        Assert.IsFalse(file.FillsBubble);
        Assert.AreEqual("clip.mp4", file.FileName);
        Assert.AreEqual(1234, file.FileSize);
    }

    [TestMethod]
    public void Bubble_VideoWithCaption_NamedByCaption()
    {
        var video = new MessageBubbleViewModel(MediaMessage(MessageKind.Video, "Goal!"));

        Assert.AreEqual("Goal!", video.VideoName);
        Assert.AreEqual("Goal!", video.BodyText);
    }

    [TestMethod]
    [DataRow(400, 300, 560, 400, 300)]
    [DataRow(1200, 900, 560, 480, 360)]
    [DataRow(2000, 500, 560, 560, 140)]
    [DataRow(1, 1, 560, 48, 48)]
    [DataRow(40, 20, 560, 48, 24)]
    [DataRow(64, 48, 560, 64, 48)]
    [DataRow(0, 0, 560, 240, 240)]
    [DataRow(0, 0, 200, 200, 200)]
    public void PhotoSize_FitsTheBubble(int width, int height, double maxWidth, double expectedWidth, double expectedHeight)
    {
        (double w, double h) = MessageBubbleViewModel.PhotoSize(width, height, maxWidth);

        Assert.AreEqual(expectedWidth, w);
        Assert.AreEqual(expectedHeight, h);
    }

    /// <summary>
    /// The full image decodes wide enough to fill its box once cropped, never wider than its own pixels: a landscape
    /// photo in the square box of unknown dimensions (the blur: decoded only 240 wide, then stretched 1.33x to the box's
    /// height), a photo whose reported sides were swapped, a photo matching its box, a small photo, and an unknown size.
    /// </summary>
    /// <param name="pixelWidth">The image's own width.</param>
    /// <param name="pixelHeight">The image's own height.</param>
    /// <param name="boxWidth">The box's width.</param>
    /// <param name="boxHeight">The box's height.</param>
    /// <param name="expected">The decode width.</param>
    [TestMethod]
    [DataRow(4000, 3000, 240, 240, 320)]
    [DataRow(4000, 3000, 270, 360, 480)]
    [DataRow(1200, 900, 480, 360, 480)]
    [DataRow(200, 100, 240, 240, 200)]
    [DataRow(0, 0, 240, 240, 240)]
    public void FillDecodeWidth_CoversTheBox_NeverUpscales(int pixelWidth, int pixelHeight, double boxWidth, double boxHeight, int expected) =>
        Assert.AreEqual(expected, MessageBubbleViewModel.FillDecodeWidth(pixelWidth, pixelHeight, boxWidth, boxHeight));
}
