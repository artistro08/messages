using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;
using Keyboard = FlaUI.Core.Input.Keyboard;
using Mouse = FlaUI.Core.Input.Mouse;

namespace GMessages.App.UITests;

/// <summary>
/// The photo viewer in the app: a clicked photo opens over the window, not in the Photos app. The viewer zooms, closes
/// three ways without closing the chat, and keeps focus inside. Its toolbar saves, downloads and opens in Photos.
/// </summary>
public sealed partial class MediaTests
{
    /// <summary>
    /// A click on a photo opens the viewer, named by the photo, and opens nothing outside the app. Escape closes the
    /// viewer, the chat stays open, and focus returns to the photo.
    /// </summary>
    [TestMethod]
    public async Task Viewer_PhotoClick_OpensInApp_EscapeKeepsChat()
    {
        await OpenMediaChatAsync();
        AutomationElement photo = ViewerPhoto("At the beach");

        photo.Click();
        AutomationElement viewer = App.Find("PhotoViewer.Photo");
        Assert.AreEqual("At the beach", AppDriver.SafeName(viewer), "The viewer isn't named by the photo's caption.");
        Thread.Sleep(500);
        Assert.IsFalse(File.Exists(Path.Combine(App.DataDir, "launched.txt")), "The click opened the photo outside the app.");

        System.Drawing.Rectangle box  = viewer.BoundingRectangle;
        System.Drawing.Rectangle list = App.Find("Row.c-media").BoundingRectangle;
        Assert.IsTrue(box.Left <= list.Left, $"The viewer ({box}) doesn't cover the list ({list}).");

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        WaitForGone("PhotoViewer.Photo");
        Assert.IsNotNull(App.TryFind("Chat.Draft"), "Escape closed the chat too.");
        Wait.Until(() => App.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault == photo.Properties.AutomationId.ValueOrDefault,
            AppDriver.DefaultTimeout, () => "Focus didn't go back to the photo.");
    }

    /// <summary>
    /// The Close button and a click beside the photo both close the viewer. A click on the photo does not.
    /// </summary>
    [TestMethod]
    public async Task Viewer_CloseButtonAndScrim_Close()
    {
        await OpenMediaChatAsync();
        AutomationElement photo = ViewerPhoto(null);

        photo.Click();
        App.Find("PhotoViewer.Close").Patterns.Invoke.Pattern.Invoke();
        WaitForGone("PhotoViewer.Photo");

        photo.Click();
        System.Drawing.Rectangle box = App.Find("PhotoViewer.Photo").BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(box.Left + (box.Width / 2), box.Top + (box.Height / 2)));
        Thread.Sleep(800);
        Assert.IsNotNull(App.TryFind("PhotoViewer.Photo"), "A click on the photo closed the viewer.");

        Mouse.Click(new System.Drawing.Point(box.Left + 12, box.Bottom - 12));
        WaitForGone("PhotoViewer.Photo");
        Assert.IsNotNull(App.TryFind("Chat.Draft"), "The scrim click closed the chat too.");
    }

    /// <summary>
    /// A double-click on the photo zooms in to 2x. Another double-click zooms back out to 1x.
    /// </summary>
    [TestMethod]
    public async Task Viewer_DoubleClick_Zooms()
    {
        await OpenMediaChatAsync();
        AutomationElement photo = ViewerPhoto(null);

        photo.Click();
        AutomationElement viewer     = App.Find("PhotoViewer.Photo");
        System.Drawing.Rectangle box = viewer.BoundingRectangle;
        var center                   = new System.Drawing.Point(box.Left + (box.Width / 2), box.Top + (box.Height / 2));
        Thread.Sleep(500);

        Mouse.DoubleClick(center);
        WaitForStatus(viewer, "Zoom2");
        Mouse.DoubleClick(center);
        WaitForStatus(viewer, "Zoom1");
    }

    /// <summary>
    /// A photo wider than twice the window opens decoded narrower than its own width. When the zoom passes 2x, the
    /// viewer decodes the photo again at its own width.
    /// </summary>
    [TestMethod]
    public async Task Viewer_ZoomPast2x_DecodesFullWidth()
    {
        await OpenMediaChatAsync();
        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(6000, 600, SplitColors), "huge.png");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Full);
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();

        // On open, the viewer decodes the photo at twice the window width, not at its own 6000 px.
        photo.Click();
        string note = Path.Combine(App.DataDir, "viewer-decode.txt");
        Wait.Until(() => ReadNote(note).Length > 0, AppDriver.DefaultTimeout, () => "The viewer never decoded the photo.");
        int first = int.Parse(ReadNote(note), System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsTrue(first is > 0 and < 6000, $"The viewer decoded the 6000 px photo {first} px wide.");

        AutomationElement viewer     = App.Find("PhotoViewer.Photo");
        System.Drawing.Rectangle box = viewer.BoundingRectangle;
        Mouse.Position               = new System.Drawing.Point(box.Left + (box.Width / 2), box.Top + (box.Height / 2));
        Thread.Sleep(500);
        using (Keyboard.Pressing(VirtualKeyShort.CONTROL))
        {
            for (int i = 0; i < 8; i++)
            {
                Mouse.Scroll(2);
                Thread.Sleep(50);
            }
        }

        Wait.Until(() => ReadNote(note) == "6000", AppDriver.DefaultTimeout, () => $"Zoomed past 2x, the photo stayed decoded {ReadNote(note)} px wide.");
    }

    /// <summary>
    /// A tall, narrow photo of 100 x 20000 decodes narrow enough that its height stays within 8192 px. This holds when
    /// the viewer opens and when the zoom passes 2x.
    /// </summary>
    [TestMethod]
    public async Task Viewer_TallPhoto_DecodeHeightCapped()
    {
        await OpenMediaChatAsync();
        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(100, 20000, SplitColors), "tall.png");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Full);
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();

        // The bubble is only a few pixels wide, too narrow to click reliably. The test invokes it.
        photo.Patterns.Invoke.Pattern.Invoke();
        string note = Path.Combine(App.DataDir, "viewer-decode.txt");
        Wait.Until(() => ReadNote(note).Length > 0, AppDriver.DefaultTimeout, () => "The viewer never decoded the photo.");
        AssertHeightCapped();

        AutomationElement viewer     = App.Find("PhotoViewer.Photo");
        System.Drawing.Rectangle box = viewer.BoundingRectangle;
        Mouse.Position               = new System.Drawing.Point(box.Left + (box.Width / 2), box.Top + (box.Height / 2));
        Thread.Sleep(500);
        using (Keyboard.Pressing(VirtualKeyShort.CONTROL))
        {
            for (int i = 0; i < 8; i++)
            {
                Mouse.Scroll(2);
                Thread.Sleep(50);
            }
        }

        Wait.Until(() => viewer.Properties.ItemStatus.ValueOrDefault is "Zoom3" or "Zoom4" or "Zoom5" or "Zoom6" or "Zoom7" or "Zoom8",
            AppDriver.DefaultTimeout, () => $"Ctrl + wheel didn't zoom past 2x ({viewer.Properties.ItemStatus.ValueOrDefault}).");
        Thread.Sleep(1000);
        AssertHeightCapped();

        void AssertHeightCapped()
        {
            int width   = int.Parse(ReadNote(note), System.Globalization.CultureInfo.InvariantCulture);
            long height = (long)width * 20000 / 100;
            Assert.IsTrue(width > 0 && height <= 8192, $"The 100 x 20000 photo decoded {width} px wide, {height} px tall.");
        }
    }

    // A test note that the app wrote, or an empty string before the app writes it.
    private static string ReadNote(string path)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadToEnd().Trim();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Tab and Shift + Tab keep the focus inside the viewer, and every toolbar button has its name.
    /// </summary>
    [TestMethod]
    public async Task Viewer_FocusTrapped_ButtonsNamed()
    {
        await OpenMediaChatAsync();
        AutomationElement photo = ViewerPhoto(null);

        photo.Click();
        App.Find("PhotoViewer.Photo");
        foreach ((string id, string name) in (ReadOnlySpan<(string, string)>)[
            ("PhotoViewer.SaveAs", ChatCopy.SaveAs), ("PhotoViewer.Download", ChatCopy.Download),
            ("PhotoViewer.OpenInPhotos", ChatCopy.OpenInPhotos), ("PhotoViewer.OpenWith", ChatCopy.OpenWith), ("PhotoViewer.Close", ChatCopy.Close)])
        {
            Assert.AreEqual(name, AppDriver.SafeName(App.Find(id)), $"{id} isn't named '{name}'.");
        }

        for (int i = 0; i < 12; i++)
        {
            Keyboard.Type(VirtualKeyShort.TAB);
            Thread.Sleep(100);
            string focused = App.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault ?? string.Empty;
            Assert.IsTrue(focused.StartsWith("PhotoViewer.", StringComparison.Ordinal), $"Tab {i + 1} moved focus out of the viewer, to '{focused}'.");
        }

        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        string back = App.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault ?? string.Empty;
        Assert.IsTrue(back.StartsWith("PhotoViewer.", StringComparison.Ordinal), $"Shift + Tab moved focus out of the viewer, to '{back}'.");
    }

    /// <summary>
    /// Open in Photos in the toolbar opens the temp copy of the photo in its app. The test hook notes the open.
    /// Download saves the photo to the Downloads folder. Save as writes the picked file.
    /// </summary>
    [TestMethod]
    public async Task Viewer_Toolbar_OpenDownloadSaveAs()
    {
        await OpenMediaChatAsync();
        byte[] bytes            = Png(400, 300, SplitColors);
        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", bytes, "wide.png");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Full);
        photo.Click();

        App.Find("PhotoViewer.OpenInPhotos").Patterns.Invoke.Pattern.Invoke();
        string launched = Path.Combine(App.DataDir, "launched.txt");
        Wait.Until(() => File.Exists(launched), AppDriver.DefaultTimeout, () => "Open in Photos opened nothing.");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(File.ReadAllLines(launched)[^1]), "Open in Photos opened another file.");

        App.Find("PhotoViewer.Download").Patterns.Invoke.Pattern.Invoke();
        string downloaded = Path.Combine(App.DataDir, "Downloads", "wide.png");
        Wait.Until(() => File.Exists(downloaded) && new FileInfo(downloaded).Length == bytes.Length, AppDriver.DefaultTimeout, () => "Download saved nothing.");
        App.Find("Main.Downloaded");

        string target = Path.Combine(App.DataDir, "saved-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllText(Path.Combine(App.DataDir, "save-pick.txt"), target);
        App.Find("PhotoViewer.SaveAs").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => File.Exists(target) && new FileInfo(target).Length == bytes.Length, AppDriver.DefaultTimeout, () => "Save as wrote nothing.");
        Assert.IsNotNull(App.TryFind("PhotoViewer.Photo"), "A toolbar action closed the viewer.");
    }

    /// <summary>
    /// Open with in the toolbar launches the temp copy of the photo with the Windows app picker. The viewer stays
    /// open. The test hook notes the path and the word <c>picker</c> instead.
    /// </summary>
    [TestMethod]
    public async Task Viewer_Toolbar_OpenWith()
    {
        await OpenMediaChatAsync();
        byte[] bytes            = Png(400, 300, SplitColors);
        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", bytes, "wide.png");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Full);
        photo.Click();

        App.Find("PhotoViewer.OpenWith").Patterns.Invoke.Pattern.Invoke();
        string path = LaunchedWithPicker("The viewer's Open with");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), "The viewer's Open with launched another file.");
        Assert.IsNotNull(App.TryFind("PhotoViewer.Photo"), "Open with closed the viewer.");
    }

    /// <summary>
    /// The toolbar sits in the top-left corner of the title bar row. The order is Close, Open in Photos, Open with,
    /// Download and Save as. It takes real mouse clicks there. The scrim tints the title bar row too, so the title and
    /// the profile picture read dark. Close puts the row back.
    /// </summary>
    [TestMethod]
    public async Task Viewer_ToolbarInTitleBarRow_TintCoversTitleBar()
    {
        await OpenMediaChatAsync();
        AutomationElement photo      = ViewerPhoto(null);
        System.Drawing.Rectangle bar = App.Find("Main.TitleBar").BoundingRectangle;
        AutomationElement profile    = App.Find("Main.ProfileButton");
        double titleBefore           = Luma(PixelAt(new System.Drawing.Point(bar.Left + 240, bar.Top + (bar.Height / 2))));

        photo.Click();
        App.Find("PhotoViewer.Photo");
        System.Drawing.Rectangle window = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).BoundingRectangle;
        int leftEdge                    = window.Left;
        foreach (string id in (ReadOnlySpan<string>)["PhotoViewer.Close", "PhotoViewer.OpenInPhotos", "PhotoViewer.OpenWith", "PhotoViewer.Download", "PhotoViewer.SaveAs"])
        {
            System.Drawing.Rectangle button = App.Find(id).BoundingRectangle;
            Assert.IsTrue(button.Top >= bar.Top && button.Bottom <= bar.Bottom, $"{id} ({button}) isn't in the title bar row ({bar}).");
            Assert.IsTrue(button.Left >= leftEdge, $"{id} ({button}) is out of order (Close, Open, Open with, Download, Save as from the left).");
            // The 5 buttons end 289 px in at 125% scaling on the test machine. That is 224 epx plus the window edge.
            Assert.IsTrue(button.Right <= window.Left + 295, $"{id} ({button}) isn't in the top-left corner (window {window}).");
            leftEdge = button.Right;
        }

        // The title bar row is under the scrim, so its title and the profile picture read near black.
        Thread.Sleep(300);
        double titleAfter    = Luma(PixelAt(new System.Drawing.Point(bar.Left + 240, bar.Top + (bar.Height / 2))));
        double profileAfter  = Luma(CenterPixel(profile));
        Assert.IsTrue(titleAfter < 40 && titleAfter <= titleBefore, $"The title bar row isn't tinted (luma {titleBefore:F0} before, {titleAfter:F0} after).");
        Assert.IsTrue(profileAfter < 40, $"The profile picture isn't tinted (luma {profileAfter:F0}).");

        // A real mouse click, not UI Automation, on Close must reach the button in the title bar row.
        System.Drawing.Rectangle close = App.Find("PhotoViewer.Close").BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(close.Left + (close.Width / 2), close.Top + (close.Height / 2)));
        WaitForGone("PhotoViewer.Photo");

        // The title bar buttons take clicks again. The profile menu opens.
        profile.Click();
        App.Find("Main.Profile.Settings");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    /// <summary>
    /// The icons of the toolbar dim while the window is inactive, because the tray menu takes activation. They come
    /// back when the window is active again. The viewer stays open throughout.
    /// </summary>
    [TestMethod]
    public async Task Viewer_ToolbarDimsWhenInactive()
    {
        await OpenMediaChatAsync();
        ViewerPhoto(null).Click();
        AutomationElement close = App.Find("PhotoViewer.Close");

        Tray.RightClick();
        App.Find("TrayMenu.Open");
        WaitForStatus(close, "Inactive");

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Tray.CloseOverflow();
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
        WaitForStatus(close, "Active");
        Assert.IsNotNull(App.TryFind("PhotoViewer.Photo"), "Leaving the window closed the viewer.");
    }

    /// <summary>
    /// A photo and a video grow out of their bubbles into the viewer. Escape and Close shrink them back before the
    /// viewer goes. The test needs animation effects on, as on the test machine.
    /// </summary>
    [TestMethod]
    public async Task Viewer_GrowsFromBubble_ShrinksBack()
    {
        await OpenMediaChatAsync();
        string motion = Path.Combine(App.DataDir, "viewer-motion.txt");

        // The photo closes with Escape.
        AutomationElement photo = ViewerPhoto(null);
        photo.Click();
        WaitForMotion(motion, "Grew");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        WaitForMotion(motion, "Shrank");
        WaitForGone("PhotoViewer.Photo");

        // The video closes with Close. Its still grows, then the player takes over when the video plays.
        Message video = Server.Phone.PushIncomingMedia("c-media", "video/mp4", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "clip-long.mp4")), "clip-long.mp4");
        WaitForStatus(App.Find("Video.Play." + video.MessageID), "Frame");
        App.Find("Bubble.Video." + video.MessageID).Patterns.Invoke.Pattern.Invoke();
        WaitForMotion(motion, "GrewStill");
        WaitForMotion(motion, "Player");
        App.Find("PhotoViewer.Close").Patterns.Invoke.Pattern.Invoke();
        WaitForMotion(motion, "Shrank");
        WaitForGone("PhotoViewer.Video");
        Assert.IsNotNull(App.TryFind("Chat.Draft"), "Closing the viewer closed the chat too.");
    }

    // The last motion of the viewer, as the app noted it.
    private static void WaitForMotion(string path, string expected)
    {
        string last = string.Empty;
        Wait.Until(() =>
        {
            last = File.Exists(path) ? ReadShared(path) : string.Empty;
            return last == expected;
        }, AppDriver.DefaultTimeout, () => $"The viewer's motion stayed '{last}', not '{expected}'.");
    }

    private static System.Drawing.Color PixelAt(System.Drawing.Point at)
    {
        using FlaUI.Core.Capturing.CaptureImage capture = FlaUI.Core.Capturing.Capture.Rectangle(new System.Drawing.Rectangle(at.X, at.Y, 1, 1));
        return capture.Bitmap.GetPixel(0, 0);
    }

    // The brightness of a color, from 0 to 255.
    private static double Luma(System.Drawing.Color c) => (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);

    // A loaded 400 x 300 photo in Mia's chat, with or without a caption.
    private AutomationElement ViewerPhoto(string? caption)
    {
        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(400, 300, SplitColors), "view.png", caption: caption);
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Full);
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
        return photo;
    }
}
