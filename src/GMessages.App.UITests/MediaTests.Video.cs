using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;
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
    /// Windows' play and pause button works on a video playing in its bubble: a click while the 20 s clip plays pauses it
    /// where it is, and another click plays it on. There's no cast button.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Video_InlinePlayPauseButton_PausesAndPlays()
    {
        await OpenMediaChatAsync();
        Message message = PushVideo("clip-long.mp4");

        App.Find("Video.Play." + message.MessageID).Patterns.Invoke.Pattern.Invoke();
        AutomationElement player = App.Find("Video.Player." + message.MessageID);
        WaitForStatus(player, "Playing");
        AssertPlayPauseWorks(player);
    }

    /// <summary>
    /// Windows' play and pause button works in the viewer too: it pauses the 20 s clip where it is and plays it on.
    /// There's no cast button.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Video_ViewerPlayPauseButton_PausesAndPlays()
    {
        await OpenMediaChatAsync();
        Message message = PushVideo("clip-long.mp4");

        App.Find("Bubble.Video." + message.MessageID).Patterns.Invoke.Pattern.Invoke();
        AutomationElement player = App.Find("PhotoViewer.Video");
        WaitForStatus(player, "Playing");
        AssertPlayPauseWorks(player);
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

    // Mia Sends A Test Clip (The 2 s One Unless Named)
    /// <summary>
    /// A portrait video's narrow bubble still gets the whole compact bar in order: the seek bar shrinks to the room left
    /// between play and volume instead of running under volume with its thumb cut off (owner report).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Video_NarrowBubble_KeepsTheCompactBarInOrder()
    {
        await OpenMediaChatAsync();

        // The Clip Is Small; Its Reported Dimensions Shape The Bubble, As A Phone's Portrait Video Would
        Message message = Server.Phone.PushIncomingMedia("c-media", "video/mp4", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "clip-long.mp4")), "portrait.mp4",
            dimensions: new Dimensions { Width = 720, Height = 1280 });

        App.Find("Video.Play." + message.MessageID).Patterns.Invoke.Pattern.Invoke();
        AutomationElement player = App.Find("Video.Player." + message.MessageID);
        WaitForStatus(player, "Playing");

        // The Pointer Over The Video Shows The Bar
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
        System.Drawing.Rectangle box = player.BoundingRectangle;
        FlaUI.Core.Input.Mouse.MoveTo(box.X + (box.Width / 2), box.Y + (box.Height / 2));
        Wait.Until(() => AppDriver.Search(player, e => e.Properties.AutomationId.ValueOrDefault == "ProgressSlider") is { IsOffscreen: false },
            AppDriver.DefaultTimeout, () => "The seek bar didn't show.");
        AssertCompactBar(player);
    }

    /// <summary>
    /// A video playing in its bubble shows its frames (the app draws them: the player's own picture ignored the bubble's
    /// corners on the owner's install) with the bubble's rounded corners: the middle of the box shows the clip, and the
    /// box's bottom right corner shows the chat behind it.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Video_InlinePlay_ShowsFramesWithRoundedCorners()
    {
        await OpenMediaChatAsync();

        // The Clip's Own Shape, So Its Picture Fills The Box To The Corners
        Message message = Server.Phone.PushIncomingMedia("c-media", "video/mp4", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "clip-long.mp4")), "clip-long.mp4",
            dimensions: new Dimensions { Width = 160, Height = 120 });
        App.Find("Video.Play." + message.MessageID).Patterns.Invoke.Pattern.Invoke();
        AutomationElement player = App.Find("Video.Player." + message.MessageID);
        WaitForStatus(player, "Playing");

        // The Pointer Away, So The Bar Hides; A Moment For Frames To Draw
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
        System.Drawing.Rectangle box = player.BoundingRectangle;
        FlaUI.Core.Input.Mouse.MoveTo(box.X - 60, box.Y - 60);
        Thread.Sleep(2500);

        using FlaUI.Core.Capturing.CaptureImage capture = FlaUI.Core.Capturing.Capture.Rectangle(box);
        System.Drawing.Color middle = capture.Bitmap.GetPixel(box.Width / 2, box.Height / 2);
        System.Drawing.Color corner = capture.Bitmap.GetPixel(box.Width - 2, box.Height - 2);
        System.Drawing.Color beside = FlaUI.Core.Capturing.Capture.Rectangle(new System.Drawing.Rectangle(box.Right + 8, box.Bottom - 2, 1, 1)).Bitmap.GetPixel(0, 0);
        Assert.IsTrue(Math.Max(Math.Abs(middle.R - corner.R), Math.Max(Math.Abs(middle.G - corner.G), Math.Abs(middle.B - corner.B))) > 60,
            $"The video's middle ({middle}) looks like its corner ({corner}): no frames drawn.");
        Assert.IsTrue(Math.Abs(corner.R - beside.R) + Math.Abs(corner.G - beside.G) + Math.Abs(corner.B - beside.B) < 30,
            $"The video's bottom right corner ({corner}) isn't the chat behind it ({beside}): the corner isn't rounded.");
    }

    private Message PushVideo(string file = "clip.mp4") =>
        Server.Phone.PushIncomingMedia("c-media", "video/mp4", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), file);

    /// <summary>
    /// Expanding a video playing in its bubble carries on in the viewer from where it got to, and closing the viewer
    /// carries on in the bubble from where the viewer got to, still playing.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Video_ExpandAndClose_KeepThePosition()
    {
        await OpenMediaChatAsync();
        Message message = PushVideo("clip-long.mp4");

        App.Find("Video.Play." + message.MessageID).Patterns.Invoke.Pattern.Invoke();
        AutomationElement inline = App.Find("Video.Player." + message.MessageID);
        WaitForStatus(inline, "Playing");
        Thread.Sleep(3000);

        App.Find("Video.Expand." + message.MessageID).Patterns.Invoke.Pattern.Invoke();
        AutomationElement viewer = App.Find("PhotoViewer.Video");
        WaitForStatus(viewer, "Playing");
        Wait.Until(() => SeekValue(viewer) >= 10, AppDriver.DefaultTimeout, () => $"The viewer is at {SeekValue(viewer):0}%, not where the bubble got to.");
        Thread.Sleep(2000);
        double viewerEnd = SeekValue(viewer);

        App.Find("PhotoViewer.Close").Patterns.Invoke.Pattern.Invoke();
        WaitForGone("PhotoViewer.Video");
        inline = App.Find("Video.Player." + message.MessageID);
        WaitForStatus(inline, "Playing");
        double back = SeekValue(inline);
        Assert.IsTrue(back >= viewerEnd - 1, $"The bubble carried on at {back:0}%, not where the viewer got to ({viewerEnd:0}%).");
    }

    // The Seek Bar's Value (Percent Of The Clip), Read The Way A Person Sees It: The Pointer Moves Over The Video First,
    // Which Shows The Bar. The Bar Hides 1 s After The Pointer Rests While Playing, And Hidden, Windows Stops Moving
    // Its Seek Bar
    private static double SeekValue(AutomationElement player)
    {
        System.Drawing.Rectangle box = player.BoundingRectangle;
        s_nudge                      = -s_nudge;
        FlaUI.Core.Input.Mouse.MoveTo(box.X + (box.Width / 2) + s_nudge, box.Y + (box.Height / 3));
        Thread.Sleep(100);

        AutomationElement slider = AppDriver.Search(player, e => e.Properties.AutomationId.ValueOrDefault == "ProgressSlider")
            ?? throw new AssertFailedException("The player has no seek bar.");
        return slider.Patterns.RangeValue.Pattern.Value.Value;
    }

    // Each Read Moves The Pointer A Pixel The Other Way, So It Is Always A Real Move
    private static int s_nudge = 1;

    // The Bar Is The Same In The Bubble And The Viewer: Play Or Pause On The Left, The Seek Bar, Volume On The Right,
    // Nothing Else (No Cast Or Full Window)
    private static void AssertCompactBar(AutomationElement player)
    {
        AutomationElement? Part(string id) => AppDriver.Search(player, e => e.Properties.AutomationId.ValueOrDefault == id);

        AutomationElement play   = Part("PlayPauseButtonOnLeft") ?? throw new AssertFailedException("The bar has no play button on its left.");
        AutomationElement seek   = Part("ProgressSlider") ?? throw new AssertFailedException("The bar has no seek bar.");
        AutomationElement volume = Part("VolumeMuteButton") ?? throw new AssertFailedException("The bar has no volume button.");
        // A Narrow Bubble's Bar Drops Volume (Windows' Controls Do That Below Their Width)
        bool volumeShown = volume.BoundingRectangle.Width > 0;
        Assert.IsTrue(play.BoundingRectangle.Right <= seek.BoundingRectangle.Left && (!volumeShown || seek.BoundingRectangle.Right <= volume.BoundingRectangle.Left),
            $"The bar isn't play, seek, volume: {play.BoundingRectangle} {seek.BoundingRectangle} {volume.BoundingRectangle}.");
        Assert.IsNull(Part("CastButton"), "The bar offers a cast button.");
        Assert.IsNull(Part("FullWindowButton"), "The bar offers a full window button.");
    }

    // Pause Holds The Position, Play Moves It On Again, And No Cast Button Shows
    private void AssertPlayPauseWorks(AutomationElement player)
    {
        PressPlayPause(player, "Paused");
        AssertCompactBar(player);
        double pausedAt = SeekValue(player);
        Thread.Sleep(1500);
        Assert.AreEqual(pausedAt, SeekValue(player), "The video played on after the pause button.");

        PressPlayPause(player, "Playing");
        Wait.Until(() => SeekValue(player) > pausedAt, AppDriver.DefaultTimeout,
            () => "The video didn't move on after the play button.");
        Assert.IsNull(AppDriver.Search(player, e => e.Properties.AutomationId.ValueOrDefault == "CastButton"), "The player offers a cast button.");
    }

    // Windows' Play And Pause Button, Clicked With The Mouse As A Person Would (The Pointer Over The Player Shows Its
    // Controls First), Then The Player's State It Should Bring
    private void PressPlayPause(AutomationElement player, string expected)
    {
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
        System.Drawing.Rectangle box = player.BoundingRectangle;
        FlaUI.Core.Input.Mouse.MoveTo(box.X + (box.Width / 2), box.Y + (box.Height / 2));
        AutomationElement? button = null;
        Wait.Until(() => (button = AppDriver.Search(player, e => e.Properties.AutomationId.ValueOrDefault is "PlayPauseButton" or "PlayPauseButtonOnLeft")) is { IsOffscreen: false },
            AppDriver.DefaultTimeout, () => "The player's play and pause button didn't show.");
        Thread.Sleep(400);
        button!.Click();
        WaitForStatus(player, expected);
    }

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
