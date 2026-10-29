using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using Keyboard = FlaUI.Core.Input.Keyboard;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;
using GMessages.Presentation.Messaging;

namespace GMessages.App.UITests;

/// <summary>
/// The settings page in the main window: values that survive a restart, the live tray menu theme, Back, and unpairing.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class SettingsTests : UiTest
{
    // Theme ComboBox Items, In Order
    private static readonly string[] ThemeNames = ["Use system setting", "Light", "Dark"];
    private const int LightIndex                = 1;
    private const int DarkIndex                 = 2;

    /// <summary>
    /// Theme, both notification switches, the snooze length and Start with Windows (the test marker file) survive a quit
    /// and relaunch.
    /// The tray menu follows a theme change live.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Settings_ToggleAndRestart_Persists()
    {
        string marker = Path.Combine(App.DataDir, "startup-enabled");
        OpenSettings();
        AssertTitleBar("Main.TitleBar", AppInfo.DisplayName);

        // Defaults
        Assert.AreEqual(0, ThemeIndex(), "Theme should start at 'Use system setting'.");
        WaitForToggle("Settings.Notifications", ToggleState.On);
        WaitForToggle("Settings.NotifyViewedChat", ToggleState.Off);
        WaitForToggle("Settings.StartWithWindows", ToggleState.Off);
        Assert.AreEqual(ChatCopy.Minutes15, SnoozeLength(), "Snooze length should start at 15 minutes.");

        // Theme Flips The Tray Menu Live, Both Ways (So It Doesn't Depend On The System Theme)
        SelectTheme(LightIndex);
        AssertCloseGlyph(light: true);
        AssertTitleBarIcon();
        Assert.IsTrue(TrayMenuBrightness() > 160, "The tray menu didn't turn light after choosing Light.");
        SelectTheme(DarkIndex);
        AssertCloseGlyph(light: false);
        Assert.IsTrue(TrayMenuBrightness() < 96, "The tray menu didn't turn dark after choosing Dark.");

        // Switches; Start With Windows Writes The Test Marker
        Toggle("Settings.Notifications", ToggleState.Off);
        Toggle("Settings.NotifyViewedChat", ToggleState.On);
        Toggle("Settings.StartWithWindows", ToggleState.On);
        Wait.Until(() => File.Exists(marker), AppDriver.DefaultTimeout, () => "Start with Windows didn't write the startup-enabled marker.");

        // Snooze Length: The Card's Choices Are The Old Toast Picker's
        ComboBox snooze = App.Find("Settings.SnoozeLength").AsComboBox();
        snooze.Select(2);
        snooze.Collapse();
        Wait.Until(() => SnoozeLength() == ChatCopy.Hour1, AppDriver.DefaultTimeout, () => $"Snooze length shows '{SnoozeLength()}', not 1 hour.");

        // Restart: Everything Comes Back
        await App.RestartAsync();
        OpenSettings();
        Wait.Until(() => ThemeIndex() == DarkIndex, AppDriver.DefaultTimeout, () => $"Theme came back as index {ThemeIndex()}, not Dark.");
        WaitForToggle("Settings.Notifications", ToggleState.Off);
        WaitForToggle("Settings.NotifyViewedChat", ToggleState.On);
        WaitForToggle("Settings.StartWithWindows", ToggleState.On);
        Wait.Until(() => SnoozeLength() == ChatCopy.Hour1, AppDriver.DefaultTimeout, () => $"Snooze length came back as '{SnoozeLength()}', not 1 hour.");
    }

    /// <summary>
    /// Settings covers pairing, keeping its sign-in alive; Back returns to the same sign-in.
    /// </summary>
    [TestMethod]
    public void Settings_Back_ReturnsToPairing()
    {
        WaitForSignInButton();
        int page_loads = Server.RequestedPaths.Count(p => p == "/web/authentication");

        // Settings Over Pairing
        OpenSettings();
        WaitForGone("Pairing.Cancel");

        // Back: The Same Sign-In, Not A New One
        App.Find("Settings.Back").Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Settings.Back");
        WaitForPairing();
        WaitForSignInButton();
        Assert.AreEqual(page_loads, Server.RequestedPaths.Count(p => p == "/web/authentication"), "Back started a new sign-in.");
    }

    /// <summary>
    /// Unpair asks first; confirming closes settings, deletes the session and switches the main window to pairing.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Settings_Unpair_Confirm_ReturnsToPairing()
    {
        await PairAsync();
        OpenSettings();

        // Confirm Dialog
        App.Find("Settings.Unpair").Patterns.Invoke.Pattern.Invoke();
        AutomationElement confirm = App.Find("Settings.UnpairConfirm");
        Assert.IsTrue(confirm.Patterns.Invoke.IsSupported, "Settings.UnpairConfirm can't be invoked.");
        confirm.Patterns.Invoke.Pattern.Invoke();

        // Back To Pairing
        WaitForGone("Settings.Back");
        WaitForPairing();
        Wait.Until(() => !File.Exists(SessionFile), AppDriver.DefaultTimeout, () => "Unpair left session.bin behind.");
    }

    /// <summary>
    /// Unpairing deletes the message database the opened chat left behind.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Unpair_DeletesMessageDatabase()
    {
        string database = Path.Combine(App.DataDir, "messages.db");

        // Opening A Chat Saves It
        await OpenMainAsync();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => File.Exists(database), AppDriver.DefaultTimeout, () => "Opening a chat left no messages.db.");
        CloseMainWindow();

        // Unpair
        OpenSettings();
        App.Find("Settings.Unpair").Patterns.Invoke.Pattern.Invoke();
        App.Find("Settings.UnpairConfirm").Patterns.Invoke.Pattern.Invoke();
        WaitForPairing();

        Wait.Until(() => !File.Exists(database), AppDriver.DefaultTimeout, () => "Unpair left messages.db behind.");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private void OpenSettings()
    {
        InvokeTrayMenu("TrayMenu.Settings");
        WaitForSettings().Focus();
    }

    // The Selected Item's Text, Mapped Back To Its Index
    private int ThemeIndex() => Array.IndexOf(ThemeNames, AppDriver.SafeName(App.Find("Settings.Theme").AsComboBox().SelectedItem));

    private string SnoozeLength() => AppDriver.SafeName(App.Find("Settings.SnoozeLength").AsComboBox().SelectedItem);

    private void SelectTheme(int index)
    {
        ComboBox theme = App.Find("Settings.Theme").AsComboBox();
        theme.Select(index);
        theme.Collapse();
        Wait.Until(() => ThemeIndex() == index, AppDriver.DefaultTimeout, () => $"The theme ComboBox didn't take index {index}.");
    }

    private void Toggle(string automationId, ToggleState wanted)
    {
        ToggleButton toggle = App.Find(automationId).AsToggleButton();
        if (toggle.ToggleState != wanted)
        {
            toggle.Toggle();
        }

        WaitForToggle(automationId, wanted);
    }

    private void WaitForToggle(string automationId, ToggleState wanted)
    {
        ToggleState last = ToggleState.Indeterminate;
        Wait.Until(() => (last = App.Find(automationId).AsToggleButton().ToggleState) == wanted, AppDriver.DefaultTimeout,
            () => $"'{automationId}' is {last}, not {wanted}.");
    }

    // Opens The Tray Menu, Measures Its Open Item's Average Brightness (0-255) On Screen, Then Closes The Menu.
    // UIA Has No Theme Property, So The Pixels Are The Honest Check: The Light Menu Is Near White, The Dark Near Black
    private double TrayMenuBrightness()
    {
        Tray.RightClick();
        AutomationElement item = App.Find("TrayMenu.Open");

        // The Menu Fades In; Wait Until Two Captures Agree, So The Animation Is Over
        double last    = -1;
        double current = Brightness(item);
        Wait.Until(() =>
        {
            last    = current;
            current = Brightness(item);
            return Math.Abs(current - last) < 1;
        }, TimeSpan.FromSeconds(5), () => "The tray menu never stopped changing.");

        // Escape Closes The Menu (Its Host Window Has The Foreground)
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Wait.Until(() => App.TryFind("TrayMenu.Open") is null, AppDriver.DefaultTimeout, () => "Escape did not close the tray menu.");
        Tray.CloseOverflow();
        TestContext.WriteLine($"Tray menu brightness: {current:0}");
        return current;
    }

    // The System-Drawn Close Button Must Contrast With Its Background In The App Theme: A Dark Glyph On Light, A Light
    // Glyph On Dark (A Mismatch With The Windows Theme Would Leave It Invisible). The Button Is Found By Position,
    // Since UIA Gives Pixels No Theme: A Strip Inside The Window's Top-Right, Clear Of The Invisible Resize Border
    private void AssertCloseGlyph(bool light)
    {
        Window window = App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout);
        window.Focus();
        double background = 0;
        double glyph      = 0;
        Wait.Until(() =>
        {
            Rectangle box = window.BoundingRectangle;
            (background, glyph) = Extremes(new Rectangle(box.Right - 48, box.Top + 8, 34, 44), light);
            return light ? background - glyph > 80 : glyph - background > 80;
        }, TimeSpan.FromSeconds(5), () => $"The close glyph doesn't contrast in {(light ? "Light" : "Dark")}: background {background:0}, glyph {glyph:0}.");
        TestContext.WriteLine($"Close glyph ({(light ? "Light" : "Dark")}): background {background:0}, glyph {glyph:0}");
    }

    // The Title Bar Icon Area Must Show An Icon: Its Brightness Varies Instead Of Being One Flat Background
    private void AssertTitleBarIcon()
    {
        Window window = App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout);
        double spread = 0;
        Wait.Until(() =>
        {
            Rectangle box = window.BoundingRectangle;
            (double low, double high) = Range(new Rectangle(box.Left + 12, box.Top + 8, 30, 44));
            spread = high - low;
            return spread > 40;
        }, TimeSpan.FromSeconds(5), () => $"The title bar icon area looks blank (brightness spread {spread:0}).");
    }

    // Background Is The Strip's Corner Pixel; Glyph Is Its Darkest (Light) Or Lightest (Dark) Pixel
    private static (double Background, double Glyph) Extremes(Rectangle area, bool light)
    {
        using CaptureImage capture = Capture.Rectangle(area);
        Bitmap bitmap     = capture.Bitmap;
        double background = Luma(bitmap.GetPixel(0, 0));
        double glyph      = background;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                double value = Luma(bitmap.GetPixel(x, y));
                glyph        = light ? Math.Min(glyph, value) : Math.Max(glyph, value);
            }
        }

        return (background, glyph);
    }

    private static (double Low, double High) Range(Rectangle area)
    {
        using CaptureImage capture = Capture.Rectangle(area);
        Bitmap bitmap = capture.Bitmap;
        double low    = 255;
        double high   = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                double value = Luma(bitmap.GetPixel(x, y));
                low          = Math.Min(low, value);
                high         = Math.Max(high, value);
            }
        }

        return (low, high);
    }

    private static double Luma(Color pixel) => (0.299 * pixel.R) + (0.587 * pixel.G) + (0.114 * pixel.B);

    private static double Brightness(AutomationElement element)
    {
        using CaptureImage capture = Capture.Element(element);
        Bitmap bitmap = capture.Bitmap;
        double total  = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                total      += (0.299 * pixel.R) + (0.587 * pixel.G) + (0.114 * pixel.B);
            }
        }

        return total / (bitmap.Width * bitmap.Height);
    }
}
