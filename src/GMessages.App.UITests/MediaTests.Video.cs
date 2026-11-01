using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace GMessages.App.UITests;

/// <summary>
/// Videos: a video bubble plays in place (one at a time, as media) from its play button, and opens in the window's
/// viewer from its frame or its expand button; Escape stops the viewer's video and closes it.
/// </summary>
public sealed partial class MediaTests
{
    /// <summary>
    /// The play button plays the video in its bubble, with Windows' controls, as media (not communications), and opens
    /// nothing outside the app; the expand button then shows.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Video_InlinePlay_PlaysInPlace()
    {
        await OpenMediaChatAsync();
        Message message = PushVideo();

        AutomationElement frame = App.Find("Bubble.Video." + message.MessageID);
        Assert.AreEqual(ChatCopy.Video, AppDriver.SafeName(frame), "The video's frame isn't named \"Video\".");
        AutomationElement play = App.Find("Video.Play." + message.MessageID);
        Assert.AreEqual(ChatCopy.PlayVideo, AppDriver.SafeName(play), "The play button has another name.");

        play.Patterns.Invoke.Pattern.Invoke();
        AutomationElement player = App.Find("Video.Player." + message.MessageID);
        WaitForStatus(player, "Playing");
        WaitForVideoPlayer("Playing Media");
        Assert.AreEqual(ChatCopy.OpenVideo, AppDriver.SafeName(App.Find("Video.Expand." + message.MessageID)), "The expand button has another name.");
        Assert.IsFalse(File.Exists(Path.Combine(App.DataDir, "launched.txt")), "Playing the video opened it outside the app.");
    }

    /// <summary>
    /// Starting a second video stops the first: only one plays in place at a time.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Video_SecondInlinePlay_StopsTheFirst()
    {
        await OpenMediaChatAsync();
        Message first  = PushVideo();
        Message second = PushVideo();

        App.Find("Video.Play." + first.MessageID).Patterns.Invoke.Pattern.Invoke();
        WaitForStatus(App.Find("Video.Player." + first.MessageID), "Playing");

        App.Find("Video.Play." + second.MessageID).Patterns.Invoke.Pattern.Invoke();
        WaitForStatus(App.Find("Video.Player." + second.MessageID), "Playing");
        WaitForGone("Video.Player." + first.MessageID);
    }

    /// <summary>
    /// A click on the frame opens the video in the viewer, playing, with Open in default app in place of Open in
    /// Photos; Escape stops it and closes the viewer, the chat stays open, and focus goes back to the frame.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Video_FrameOpensViewer_EscapeStops()
    {
        await OpenMediaChatAsync();
        Message message = PushVideo();

        AutomationElement frame = App.Find("Bubble.Video." + message.MessageID);
        frame.Patterns.Invoke.Pattern.Invoke();
        AutomationElement player = App.Find("PhotoViewer.Video");
        WaitForStatus(player, "Playing");
        WaitForVideoPlayer("Playing Media");
        Assert.AreEqual(ChatCopy.OpenInDefaultApp, AppDriver.SafeName(App.Find("PhotoViewer.OpenInPhotos")), "The viewer's open button isn't Open in default app for a video.");
        Assert.IsNull(App.TryFind("PhotoViewer.Photo"), "The viewer shows the photo layer over the video.");

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        WaitForGone("PhotoViewer.Video");
        Assert.IsNotNull(App.TryFind("Chat.Draft"), "Escape closed the chat too.");
        Wait.Until(() => App.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault == "Bubble.Video." + message.MessageID,
            AppDriver.DefaultTimeout, () => "Focus didn't go back to the video.");
    }

    /// <summary>
    /// The expand button on a playing video opens the viewer and stops the inline player.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Video_Expand_OpensViewer_StopsInline()
    {
        await OpenMediaChatAsync();
        Message message = PushVideo();

        App.Find("Video.Play." + message.MessageID).Patterns.Invoke.Pattern.Invoke();
        WaitForStatus(App.Find("Video.Player." + message.MessageID), "Playing");

        App.Find("Video.Expand." + message.MessageID).Patterns.Invoke.Pattern.Invoke();
        WaitForStatus(App.Find("PhotoViewer.Video"), "Playing");
        WaitForGone("Video.Player." + message.MessageID);

        App.Find("PhotoViewer.Close").Patterns.Invoke.Pattern.Invoke();
        WaitForGone("PhotoViewer.Video");
    }

    /// <summary>
    /// A video the phone sent no thumbnail for shows a frame made from the video in its bubble (the play button's state
    /// goes from Pending to Frame), and a reply to it shows the same frame in its quote and in the reply chip.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Video_NoThumbnail_FrameMadeForBubbleQuoteAndChip()
    {
        await OpenMediaChatAsync();
        Message video = PushVideo();
        WaitForStatus(App.Find("Video.Play." + video.MessageID), "Frame");

        Message reply = Server.Phone.PushIncoming("c-media", "Nice", replyToId: video.MessageID);
        App.WaitForText("Quote." + reply.MessageID, ChatCopy.QuoteVideo);
        WaitForStatus(App.Find("QuoteThumb." + reply.MessageID), "Thumb");

        // The Reply Chip
        OpenMenu(App.Find("Bubble.Video." + video.MessageID));
        App.Find("Bubble.Reply").Patterns.Invoke.Pattern.Invoke();
        App.WaitForText("Chat.ReplyingTo", ChatCopy.QuoteVideo);
        WaitForStatus(App.Find("Chat.ReplyingToThumb"), "Thumb");
    }

    // Mia Sends The 2 s Test Clip
    private Message PushVideo() =>
        Server.Phone.PushIncomingMedia("c-media", "video/mp4", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "clip.mp4")), "clip.mp4");

    // The Video Player's Own State And Audio Category, As The App Noted It
    private void WaitForVideoPlayer(string expected)
    {
        string state  = Path.Combine(App.DataDir, "video-player.txt");
        string failed = Path.Combine(App.DataDir, "video-failed.txt");
        string last   = string.Empty;
        Wait.Until(() =>
        {
            Assert.IsFalse(File.Exists(failed), $"The video player failed: {(File.Exists(failed) ? ReadShared(failed) : string.Empty)}.");
            last = File.Exists(state) ? ReadShared(state) : string.Empty;
            return last == expected;
        }, AppDriver.DefaultTimeout, () => $"The video player stayed '{last}', not '{expected}'.");
    }
}
