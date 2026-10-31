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
/// The in-app photo viewer: a clicked photo opens over the window (not in the Photos app), zooms, closes three ways
/// without closing the chat, keeps focus inside, and its toolbar saves, downloads and opens in Photos.
/// </summary>
public sealed partial class MediaTests
{
    /// <summary>
    /// Clicking a photo opens the viewer, named by the photo, and opens nothing outside the app; Escape closes it, the
    /// chat stays open, and focus is back on the photo.
    /// </summary>
    /// <returns>A task for the test.</returns>
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

        // The Viewer Covers Both Panes
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
    /// Close and a click beside the photo both close the viewer; a click on the photo doesn't.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// A double-click on the photo zooms in to 2x, and another back out to 1x.
    /// </summary>
    /// <returns>A task for the test.</returns>
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
    /// Tab and Shift + Tab stay inside the viewer, and every toolbar button carries its name.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Viewer_FocusTrapped_ButtonsNamed()
    {
        await OpenMediaChatAsync();
        AutomationElement photo = ViewerPhoto(null);

        photo.Click();
        App.Find("PhotoViewer.Photo");
        foreach ((string id, string name) in (ReadOnlySpan<(string, string)>)[
            ("PhotoViewer.SaveAs", ChatCopy.SaveAs), ("PhotoViewer.Download", ChatCopy.Download),
            ("PhotoViewer.OpenInPhotos", ChatCopy.OpenInPhotos), ("PhotoViewer.Close", ChatCopy.Close)])
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
    /// The toolbar: Open in Photos opens the photo's temp copy in its app (the test hook notes it), Download saves it
    /// to the Downloads folder, and Save as writes the picked file.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Viewer_Toolbar_OpenDownloadSaveAs()
    {
        await OpenMediaChatAsync();
        byte[] bytes            = Png(400, 300, SplitColors);
        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", bytes, "wide.png");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Full);
        photo.Click();

        // Open In Photos
        App.Find("PhotoViewer.OpenInPhotos").Patterns.Invoke.Pattern.Invoke();
        string launched = Path.Combine(App.DataDir, "launched.txt");
        Wait.Until(() => File.Exists(launched), AppDriver.DefaultTimeout, () => "Open in Photos opened nothing.");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(File.ReadAllLines(launched)[^1]), "Open in Photos opened another file.");

        // Download
        App.Find("PhotoViewer.Download").Patterns.Invoke.Pattern.Invoke();
        string downloaded = Path.Combine(App.DataDir, "Downloads", "wide.png");
        Wait.Until(() => File.Exists(downloaded) && new FileInfo(downloaded).Length == bytes.Length, AppDriver.DefaultTimeout, () => "Download saved nothing.");
        App.Find("Main.Downloaded");

        // Save As
        string target = Path.Combine(App.DataDir, "saved-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllText(Path.Combine(App.DataDir, "save-pick.txt"), target);
        App.Find("PhotoViewer.SaveAs").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => File.Exists(target) && new FileInfo(target).Length == bytes.Length, AppDriver.DefaultTimeout, () => "Save as wrote nothing.");
        Assert.IsNotNull(App.TryFind("PhotoViewer.Photo"), "A toolbar action closed the viewer.");
    }

    /// <summary>
    /// The toolbar sits in the title bar row, left of the caption buttons, and takes real mouse clicks there; the scrim
    /// tints the title bar row too (its title and profile picture read dark), and closing puts the row back.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Viewer_ToolbarInTitleBarRow_TintCoversTitleBar()
    {
        await OpenMediaChatAsync();
        AutomationElement photo      = ViewerPhoto(null);
        System.Drawing.Rectangle bar = App.Find("Main.TitleBar").BoundingRectangle;
        AutomationElement profile    = App.Find("Main.ProfileButton");
        double title_before          = Luma(PixelAt(new System.Drawing.Point(bar.Left + 160, bar.Top + (bar.Height / 2))));

        photo.Click();
        App.Find("PhotoViewer.Photo");
        System.Drawing.Rectangle window = App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).BoundingRectangle;
        foreach (string id in (ReadOnlySpan<string>)["PhotoViewer.SaveAs", "PhotoViewer.Download", "PhotoViewer.OpenInPhotos", "PhotoViewer.Close"])
        {
            System.Drawing.Rectangle button = App.Find(id).BoundingRectangle;
            Assert.IsTrue(button.Top >= bar.Top && button.Bottom <= bar.Bottom, $"{id} ({button}) isn't in the title bar row ({bar}).");
            Assert.IsTrue(button.Right <= window.Right - 100, $"{id} ({button}) overlaps the caption buttons (window {window}).");
        }

        // The Title Bar Row Is Under The Scrim: Its Title And The Profile Picture Read Near Black
        Thread.Sleep(300);
        double title_after   = Luma(PixelAt(new System.Drawing.Point(bar.Left + 160, bar.Top + (bar.Height / 2))));
        double profile_after = Luma(CenterPixel(profile));
        Assert.IsTrue(title_after < 40 && title_after <= title_before, $"The title bar row isn't tinted (luma {title_before:F0} before, {title_after:F0} after).");
        Assert.IsTrue(profile_after < 40, $"The profile picture isn't tinted (luma {profile_after:F0}).");

        // A Real Click On Close (Not UI Automation) Reaches It In The Title Bar Row
        System.Drawing.Rectangle close = App.Find("PhotoViewer.Close").BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(close.Left + (close.Width / 2), close.Top + (close.Height / 2)));
        WaitForGone("PhotoViewer.Photo");

        // The Title Bar's Own Buttons Take Clicks Again: The Profile Menu Opens
        profile.Click();
        App.Find("Main.Profile.Settings");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    // The Screen Pixel At A Point
    private static System.Drawing.Color PixelAt(System.Drawing.Point at)
    {
        using FlaUI.Core.Capturing.CaptureImage capture = FlaUI.Core.Capturing.Capture.Rectangle(new System.Drawing.Rectangle(at.X, at.Y, 1, 1));
        return capture.Bitmap.GetPixel(0, 0);
    }

    // A Color's Brightness, 0 To 255
    private static double Luma(System.Drawing.Color c) => (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);

    // A Loaded 400 x 300 Photo In Mia's Chat, With Or Without A Caption
    private AutomationElement ViewerPhoto(string? caption)
    {
        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(400, 300, SplitColors), "view.png", caption: caption);
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Full);
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
        return photo;
    }
}
