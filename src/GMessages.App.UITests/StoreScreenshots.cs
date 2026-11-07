using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;
using Keyboard = FlaUI.Core.Input.Keyboard;
using Mouse = FlaUI.Core.Input.Mouse;

namespace GMessages.App.UITests;

/// <summary>
/// Takes the store and README screenshots into <c>docs/screenshots</c> at 1920 x 1080. The app shows a mock phone
/// (<see cref="FakeGoogle.FakeChats.AddScreenshotChats"/>) with the Unsplash photos in <c>Screenshots/Photos</c>.
/// </summary>
/// <remarks>
/// A normal run skips this class. The test runs only when the <c>MESSAGES_SCREENSHOTS</c> environment variable is 1,
/// and it is inconclusive otherwise. Run it with:
/// <c>$env:MESSAGES_SCREENSHOTS = 1; dotnet test src/GMessages.App.UITests -p:Platform=x64 --filter StoreScreenshots</c>.
/// Each shot is the visible frame of the window, with no resize border or shadow, square corners and no pointer.
/// </remarks>
[TestClass]
public sealed partial class StoreScreenshots : UiTest
{
    // The store recommends this desktop size.
    private const int ShotWidth  = 1920;
    private const int ShotHeight = 1080;

    // The window position while the test takes shots.
    private const int WindowLeft = 120;
    private const int WindowTop  = 120;

    private static readonly string[] ThemeNames = ["Use system setting", "Light", "Dark"];

    /// <summary>
    /// Takes every screenshot: the chat, a group, the photo viewer and the shortcut help in the dark theme, then the
    /// chat and settings in the light theme.
    /// </summary>
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

        // The contact photos are cut from the same pictures as the chats.
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

        SetTheme(1);
        window = PlaceWindow();
        OpenChat("Row.c-family");
        OpenChat("Row.c-maya");
        Shoot(window, output, "05-chat-light");

        // A click on the empty left margin ends the keyboard focus, so the Back button shows no focus outline.
        InvokeTrayMenu("TrayMenu.Settings");
        window = WaitForSettings();
        (_, Rect page) = Bounds(window.Properties.NativeWindowHandle.Value);
        Mouse.Click(new System.Drawing.Point(page.Left + 120, page.Top + 500));
        Thread.Sleep(800);
        Shoot(window, output, "06-settings-light");
    }

    // Opens Settings from the tray, picks the theme and goes back to the chats.
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

    // Opens a chat from the list and waits 2.5 s for its photos and avatars to load.
    private void OpenChat(string rowId)
    {
        App.Find(rowId).Patterns.SelectionItem.Pattern.Select();
        App.Find("Chat.Header");
        Thread.Sleep(2500);
    }

    // Finds the first photo bubble of the open chat.
    private AutomationElement FirstPhoto()
    {
        AutomationElement? photo = null;
        Wait.Until(() => (photo = AppDriver.Search(App.Find("Chat.Messages"), e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("Bubble.Photo.", StringComparison.Ordinal))) is not null,
            AppDriver.DefaultTimeout, () => "The chat showed no photo.");
        return photo!;
    }

    // Restores the window at a set place. The visible frame has exactly the shot size and square corners.
    private Window PlaceWindow()
    {
        Window window = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);
        nint hwnd     = window.Properties.NativeWindowHandle.Value;
        _             = ShowWindow(hwnd, 9);

        // The outer rectangle includes an invisible resize border. The second SetWindowPos call adds the border size,
        // so the visible frame has the exact shot size.
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

    // Saves the visible frame of the window. The pointer first rests on an empty part of the title bar,
    // so no bubble shows its hover buttons. A capture never includes the pointer itself.
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

    // Finds the repository above the test build: a folder with .git. In a Git worktree, .git is a file.
    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Path.Exists(Path.Combine(dir.FullName, ".git")))
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

    // Cuts a 256 px square contact photo from a picture. The point (x, y) is the center, as fractions of the
    // width and height. The side of the square is a fraction of the shorter side.
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
