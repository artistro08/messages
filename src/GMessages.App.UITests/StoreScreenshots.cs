using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;
using Keyboard = FlaUI.Core.Input.Keyboard;
using Mouse = FlaUI.Core.Input.Mouse;

namespace GMessages.App.UITests;

/// <summary>
/// Takes the store and README screenshots of the app showing a mock phone (<see cref="FakeGoogle.FakeChats.AddScreenshotChats"/>)
/// with the Unsplash photos in <c>Screenshots/Photos</c>, into <c>docs/screenshots</c> at 1920 x 1080.
/// </summary>
/// <remarks>
/// Not part of a normal run: it only goes with the <c>MESSAGES_SCREENSHOTS</c> environment variable set to 1, and is
/// inconclusive otherwise. Run it with:
/// <c>$env:MESSAGES_SCREENSHOTS = 1; dotnet test src/GMessages.App.UITests -p:Platform=x64 --filter StoreScreenshots</c>.
/// Each shot is the window's visible frame (no resize border or shadow), with square corners, the pointer moved off it.
/// </remarks>
[TestClass]
public sealed partial class StoreScreenshots : UiTest
{
    // The Store's Recommended Desktop Size
    private const int ShotWidth  = 1920;
    private const int ShotHeight = 1080;

    // Where The Window Sits While Shots Are Taken (The Pointer Waits Above And Left Of It)
    private const int WindowLeft = 120;
    private const int WindowTop  = 120;

    private static readonly string[] ThemeNames = ["Use system setting", "Light", "Dark"];

    /// <summary>
    /// Takes every screenshot: the chat, a group, the photo viewer and shortcut help in dark, then the chat and settings
    /// in light.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    [TestCategory("Screenshots")]
    public async Task TakeStoreScreenshots()
    {
        if (Environment.GetEnvironmentVariable("MESSAGES_SCREENSHOTS") != "1")
        {
            Assert.Inconclusive("Screenshots run only with MESSAGES_SCREENSHOTS=1.");
        }

        _ = SetThreadDpiAwarenessContext(-4);
        string root   = RepoRoot();
        string output = Path.Combine(root, "docs", "screenshots");
        Directory.CreateDirectory(output);

        // The Mock Phone: Its Chats, And Contact Photos Cut From The Same Pictures
        string photos = Path.Combine(root, "src", "GMessages.App.UITests", "Screenshots", "Photos");
        Server.Phone.Chats.AddScreenshotChats(new Dictionary<string, (byte[], int, int)>
        {
            ["farm"]   = Load(photos, "farm.jpg"),
            ["sprout"] = Load(photos, "sprout.jpg"),
            ["city"]   = Load(photos, "city.jpg"),
            ["statue"] = Load(photos, "statue.jpg"),
        });
        Server.Phone.ParticipantPhotos.Clear();
        Server.Phone.ParticipantPhotos["me-1"]     = Avatar(photos, "phone.jpg", 0.5, 0.42, 0.45);
        Server.Phone.ParticipantPhotos["p-maya"]   = Avatar(photos, "sprout.jpg", 0.56, 0.42, 0.5);
        Server.Phone.ParticipantPhotos["p-dad"]    = Avatar(photos, "farm.jpg", 0.42, 0.62, 0.55);
        Server.Phone.ParticipantPhotos["p-jordan"] = Avatar(photos, "statue.jpg", 0.5, 0.62, 0.45);
        Server.Phone.ParticipantPhotos["p-alex"]   = Avatar(photos, "city.jpg", 0.4, 0.75, 0.5);

        await OpenMainAsync();
        Window window = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);

        // Dark: The Chat, A Group, The Photo Viewer, Shortcut Help
        SetTheme(2);
        window = PlaceWindow();
        OpenChat("Row.c-maya");
        Shoot(window, output, "01-chat-dark");

        OpenChat("Row.c-family");
        Shoot(window, output, "02-group-dark");

        OpenChat("Row.c-alex");
        AutomationElement photo = FirstPhoto();
        photo.Click();
        App.Find("PhotoViewer.Photo");
        Thread.Sleep(1500);
        Shoot(window, output, "03-photo-viewer-dark");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Wait.Until(() => App.TryFind("PhotoViewer.Photo") is null, AppDriver.DefaultTimeout, () => "The photo viewer stayed open.");

        OpenChat("Row.c-maya");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.OEM_2);
        App.Find("Shortcuts.Close");
        Thread.Sleep(800);
        Shoot(window, output, "04-shortcuts-dark");
        App.Find("Shortcuts.Close").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => App.TryFind("Shortcuts.Close") is null, AppDriver.DefaultTimeout, () => "Shortcut help stayed open.");

        // Light: The Chat, Then Settings
        SetTheme(1);
        window = PlaceWindow();
        OpenChat("Row.c-family");
        OpenChat("Row.c-maya");
        Shoot(window, output, "05-chat-light");

        // Settings: A Click On The Empty Left Margin Ends Keyboard Focus, So Back Shows No Focus Outline
        InvokeTrayMenu("TrayMenu.Settings");
        window = WaitForSettings();
        (_, Rect page) = Bounds(window.Properties.NativeWindowHandle.Value);
        Mouse.Click(new System.Drawing.Point(page.Left + 120, page.Top + 500));
        Thread.Sleep(800);
        Shoot(window, output, "06-settings-light");
    }

    // =========================================================================
    // STEPS
    // =========================================================================

    // Opens Settings From The Tray, Picks The Theme And Goes Back To The Chats
    private void SetTheme(int index)
    {
        InvokeTrayMenu("TrayMenu.Settings");
        WaitForSettings().Focus();
        ComboBox theme = App.Find("Settings.Theme").AsComboBox();
        theme.Select(index);
        theme.Collapse();
        Wait.Until(() => AppDriver.SafeName(App.Find("Settings.Theme").AsComboBox().SelectedItem) == ThemeNames[index], AppDriver.DefaultTimeout, () => $"The theme didn't change to {ThemeNames[index]}.");
        App.Find("Settings.Back").Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Settings.Back");
        App.Find("Main.ConversationList");
    }

    // Opens A Chat From The List And Lets Its Photos And Avatars Load
    private void OpenChat(string rowId)
    {
        App.Find(rowId).Patterns.SelectionItem.Pattern.Select();
        App.Find("Chat.Header");
        Thread.Sleep(2500);
    }

    // The Open Chat's First Photo Bubble
    private AutomationElement FirstPhoto()
    {
        AutomationElement? photo = null;
        Wait.Until(() => (photo = AppDriver.Search(App.Find("Chat.Messages"), e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("Bubble.Photo.", StringComparison.Ordinal))) is not null,
            AppDriver.DefaultTimeout, () => "The chat showed no photo.");
        return photo!;
    }

    // Puts The Window At A Set Place, Restored, With Its Visible Frame Exactly The Shot's Size And Square Corners
    private Window PlaceWindow()
    {
        Window window = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);
        nint hwnd     = window.Properties.NativeWindowHandle.Value;
        _             = ShowWindow(hwnd, 9);

        // The Outer Rectangle Includes An Invisible Resize Border; Sized So The Visible Frame Comes Out Exact
        _ = SetWindowPos(hwnd, 0, WindowLeft, WindowTop, ShotWidth, ShotHeight, 0x0014);
        Thread.Sleep(400);
        (Rect outer, Rect frame) = Bounds(hwnd);
        int extraW = (outer.Right - outer.Left) - (frame.Right - frame.Left);
        int extraH = (outer.Bottom - outer.Top) - (frame.Bottom - frame.Top);
        _ = SetWindowPos(hwnd, 0, WindowLeft, WindowTop, ShotWidth + extraW, ShotHeight + extraH, 0x0014);

        int square = 1;
        int result = DwmSetWindowAttribute(hwnd, 33, ref square, sizeof(int));
        TestContext.WriteLine($"Square corners: 0x{result:X8}");
        Thread.Sleep(800);
        window.SetForeground();
        return window;
    }

    // Saves The Window's Visible Frame. The Pointer Rests On An Empty Stretch Of The Title Bar First, So No Bubble Shows
    // Its Hover Buttons (A Capture Never Includes The Pointer Itself)
    private void Shoot(Window window, string folder, string name)
    {
        window.SetForeground();
        (_, Rect frame) = Bounds(window.Properties.NativeWindowHandle.Value);
        Mouse.Position  = new System.Drawing.Point(frame.Left + ((frame.Right - frame.Left) / 2), frame.Top + 20);
        Thread.Sleep(1200);

        var area        = new System.Drawing.Rectangle(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        using FlaUI.Core.Capturing.CaptureImage shot = FlaUI.Core.Capturing.Capture.Rectangle(area);
        string file = Path.Combine(folder, name + ".png");
        shot.Bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        TestContext.WriteLine($"{file}: {shot.Bitmap.Width} x {shot.Bitmap.Height}");
    }

    // =========================================================================
    // FILES
    // =========================================================================

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("No repository above " + AppContext.BaseDirectory);
    }

    private static (byte[] Jpeg, int Width, int Height) Load(string folder, string file)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(folder, file));
        using var stream = new MemoryStream(bytes);
        using var image  = System.Drawing.Image.FromStream(stream);
        return (bytes, image.Width, image.Height);
    }

    // A 256 px Square Contact Photo Cut From A Picture: Centered At (x, y) As Parts Of Its Width And Height, The Square's
    // Side A Part Of Its Shorter Side
    private static byte[] Avatar(string folder, string file, double x, double y, double side)
    {
        using var image  = System.Drawing.Image.FromFile(Path.Combine(folder, file));
        int size         = (int)(Math.Min(image.Width, image.Height) * side);
        int left         = Math.Clamp((int)((image.Width * x) - (size / 2)), 0, image.Width - size);
        int top          = Math.Clamp((int)((image.Height * y) - (size / 2)), 0, image.Height - size);
        using var square = new System.Drawing.Bitmap(256, 256);
        using (var g = System.Drawing.Graphics.FromImage(square))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, new System.Drawing.Rectangle(0, 0, 256, 256), new System.Drawing.Rectangle(left, top, size, size), System.Drawing.GraphicsUnit.Pixel);
        }

        using var stream = new MemoryStream();
        square.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    // =========================================================================
    // WIN32
    // =========================================================================

    private static (Rect Outer, Rect Frame) Bounds(nint hwnd)
    {
        _ = GetWindowRect(hwnd, out Rect outer);
        _ = DwmGetWindowAttribute(hwnd, 9, out Rect frame, Marshal.SizeOf<Rect>());
        return (outer, frame);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
