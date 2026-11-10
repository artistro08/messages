using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class MediaPartTests
{
    private static MediaPart Part(string mediaId = "", string thumbnailId = "", byte[]? inline = null, long size = 0) =>
        new(mediaId, new byte[32], thumbnailId, new byte[32], inline ?? [], "a.jpg", size, 0, 0, MessageKind.Photo, "image/jpeg");

    /// <summary>
    /// The whole file of a sent photo or video keeps showing over the echo with the file: the media ID, only a
    /// thumbnail, or the media ID with a preview. An echo with its own bytes, an empty echo, or a preview-only echo
    /// shows the echo here; the bubble keeps the sent part for a preview-only echo.
    /// </summary>
    [TestMethod]
    public void KeepsShowingOver_WholeFileOverFileEchoes()
    {
        byte[] bytes      = new byte[100_000];
        MediaPart sent    = Part(inline: bytes, size: bytes.Length);
        MediaPart preview = Part(inline: new byte[828], size: bytes.Length);

        Assert.IsTrue(sent.KeepsShowingOver(Part("m-1")), "An echo with the media ID keeps the sent bytes.");
        Assert.IsTrue(sent.KeepsShowingOver(Part(thumbnailId: "t-1")), "An echo with only a thumbnail keeps the sent bytes.");
        Assert.IsTrue(sent.KeepsShowingOver(preview with { MediaId = "m-1" }), "An echo with the media ID and a preview keeps the sent bytes.");
        Assert.IsFalse(sent.KeepsShowingOver(preview), "A preview-only echo is for the bubble.");
        Assert.IsFalse(sent.KeepsShowingOver(Part(inline: bytes, size: bytes.Length)), "An echo with its own bytes shows itself.");
        Assert.IsFalse(sent.KeepsShowingOver(Part()), "An empty echo shows itself.");
    }

    /// <summary>
    /// A received part with a media ID and a small preview is not the whole file. Its preview never stands for the file,
    /// also when the phone sends the same message again.
    /// </summary>
    [TestMethod]
    public void KeepsShowingOver_ReceivedPreviewIsNotTheFile()
    {
        MediaPart received = Part("m-1", inline: new byte[828], size: 261_205);

        Assert.IsFalse(received.IsWholeInline);
        Assert.IsFalse(received.KeepsShowingOver(received with { InlineData = new byte[828] }));
        Assert.IsFalse(received.KeepsShowingOver(received with { InlineData = new byte[828], Size = 0 }), "A size of 0 does not make the preview the file.");
    }

    /// <summary>
    /// Only small inline data is the preview of a photo. A real photo inline, as in an MMS, shows even when its size
    /// says more.
    /// </summary>
    [TestMethod]
    public void HasInlinePreview_OnlySmallPhotoData()
    {
        Assert.IsTrue(Part(inline: new byte[828], size: 261_205).HasInlinePreview);
        Assert.IsFalse(Part(inline: new byte[300_000], size: 2_000_000).HasInlinePreview, "A real photo inline is not a preview.");
    }
}
