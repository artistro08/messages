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
    // The items of the theme ComboBox, in order.
    private static readonly string[] ThemeNames = ["Use system setting", "Light", "Dark"];
    private const int LightIndex                = 1;
    private const int DarkIndex                 = 2;

    /// <summary>
    /// The theme, both notification switches, the snooze length and Start with Windows survive a quit and a relaunch.
    /// The test marker file shows the Start with Windows value. The tray menu follows a theme change live.
    /// </summary>
    [TestMethod]
    public async Task Settings_ToggleAndRestart_Persists()
    {
        string marker = Path.Combine(App.DataDir, "startup-enabled");
        OpenSettings();
        AssertTitleBar("Main.TitleBar", AppInfo.WindowTitle);

        Assert.AreEqual(0, ThemeIndex(), "Theme should start at 'Use system setting'.");
        WaitForToggle("Settings.Notifications", ToggleState.On);
        WaitForToggle("Settings.NotifyViewedChat", ToggleState.Off);
        WaitForToggle("Settings.ReceivedLinkPreviews", ToggleState.On);
        WaitForToggle("Settings.StartWithWindows", ToggleState.Off);
        Assert.AreEqual(ChatCopy.Minutes15, SnoozeLength(), "Snooze length should start at 15 minutes.");
        // The "Windows default" item can carry the name of the current default microphone after it.
        Wait.Until(() => Microphone().StartsWith(ChatCopy.WindowsDefault, StringComparison.Ordinal), AppDriver.DefaultTimeout, () => $"Microphone shows '{Microphone()}', not Windows default.");

        // The test checks both directions, so the result does not depend on the system theme.
        SelectTheme(LightIndex);
        AssertCloseGlyph(light: true);
        AssertTitleBarIcon();
        Assert.IsTrue(TrayMenuBrightness() > 160, "The tray menu didn't turn light after choosing Light.");
        SelectTheme(DarkIndex);
        AssertCloseGlyph(light: false);
        Assert.IsTrue(TrayMenuBrightness() < 96, "The tray menu didn't turn dark after choosing Dark.");

        Toggle("Settings.Notifications", ToggleState.Off);
        Toggle("Settings.NotifyViewedChat", ToggleState.On);
        Toggle("Settings.ReceivedLinkPreviews", ToggleState.Off);
        Toggle("Settings.StartWithWindows", ToggleState.On);
        Wait.Until(() => File.Exists(marker), AppDriver.DefaultTimeout, () => "Start with Windows didn't write the startup-enabled marker.");

        ComboBox snooze = App.Find("Settings.SnoozeLength").AsComboBox();
        snooze.Select(2);
        snooze.Collapse();
        Wait.Until(() => SnoozeLength() == ChatCopy.Hour1, AppDriver.DefaultTimeout, () => $"Snooze length shows '{SnoozeLength()}', not 1 hour.");

        // The restart is a launch by the user. It opens the main window on pairing and brings it forward.
        // The tray menu waits for that, so the window cannot close the menu when it comes forward.
        await App.RestartAsync();
        WaitForPairing();
        OpenSettings();
        Wait.Until(() => ThemeIndex() == DarkIndex, AppDriver.DefaultTimeout, () => $"Theme came back as index {ThemeIndex()}, not Dark.");
        WaitForToggle("Settings.Notifications", ToggleState.Off);
        WaitForToggle("Settings.NotifyViewedChat", ToggleState.On);
        WaitForToggle("Settings.ReceivedLinkPreviews", ToggleState.Off);
        WaitForToggle("Settings.StartWithWindows", ToggleState.On);
        Wait.Until(() => SnoozeLength() == ChatCopy.Hour1, AppDriver.DefaultTimeout, () => $"Snooze length came back as '{SnoozeLength()}', not 1 hour.");
    }

    /// <summary>
    /// A second open of settings, after the test picks a microphone, shows the theme, the snooze length and that
    /// microphone. The page also saves a new pick. The reopened page once threw an error on the saved microphone,
    /// left the dropdowns blank and saved no pick.
    /// </summary>
    [TestMethod]
    public void Settings_Reopen_AfterPickingMicrophone_ShowsEveryValue()
    {
        OpenSettings();

        // The test picks the first named microphone. This PC needs one.
        ComboBox microphone = App.Find("Settings.Microphone").AsComboBox();
        microphone.Expand();
        Wait.Until(() => microphone.Items.Length > 1, AppDriver.DefaultTimeout, () => "This PC has no microphone to pick.");
        string picked = AppDriver.SafeName(microphone.Items[1]);
        microphone.Select(1);
        microphone.Collapse();
        Wait.Until(() => Microphone() == picked, AppDriver.DefaultTimeout, () => $"Microphone shows '{Microphone()}', not the one picked.");

        App.Find("Settings.Back").Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Settings.Back");
        OpenSettings();

        Wait.Until(() => Microphone() == picked, AppDriver.DefaultTimeout, () => $"Microphone came back as '{Microphone()}', not the one picked.");
        Wait.Until(() => ThemeIndex() == 0, AppDriver.DefaultTimeout, () => $"Theme came back as index {ThemeIndex()}, not 'Use system setting'.");
        Wait.Until(() => SnoozeLength() == ChatCopy.Minutes15, AppDriver.DefaultTimeout, () => $"Snooze length came back as '{SnoozeLength()}', not 15 minutes.");
        WaitForToggle("Settings.Notifications", ToggleState.On);

        // The reopened page must save a pick too. It once did not: the failed binding pass left the write-backs off,
        // so the recorder kept the old microphone.
        microphone = App.Find("Settings.Microphone").AsComboBox();
        microphone.Expand();
        Wait.Until(() => microphone.Items.Length > 2, AppDriver.DefaultTimeout, () => "This PC has no second microphone to pick.");
        string repicked = AppDriver.SafeName(microphone.Items[2]);
        microphone.Select(2);
        microphone.Collapse();
        Wait.Until(() => Microphone() == repicked, AppDriver.DefaultTimeout, () => $"Microphone shows '{Microphone()}', not the one picked again.");
        App.Find("Settings.Back").Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Settings.Back");
        OpenSettings();
        Wait.Until(() => Microphone() == repicked, AppDriver.DefaultTimeout, () => $"The reopened page's pick wasn't saved: Microphone shows '{Microphone()}'.");
    }

    /// <summary>
    /// Settings covers pairing and keeps the sign-in alive. Back returns to the same sign-in.
    /// </summary>
    [TestMethod]
    public void Settings_Back_ReturnsToPairing()
    {
        WaitForSignInButton();
        int pageLoads = Server.RequestedPaths.Count(p => p == "/web/authentication");

        OpenSettings();
        WaitForGone("Pairing.Cancel");

        App.Find("Settings.Back").Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Settings.Back");
        WaitForPairing();
        WaitForSignInButton();
        Assert.AreEqual(pageLoads, Server.RequestedPaths.Count(p => p == "/web/authentication"), "Back started a new sign-in.");
    }

    /// <summary>
    /// Unpair asks for confirmation first. A confirm closes settings, deletes the session and switches the main window
    /// to pairing.
    /// </summary>
    [TestMethod]
    public async Task Settings_Unpair_Confirm_ReturnsToPairing()
    {
        await PairAsync();
        OpenSettings();

        App.Find("Settings.Unpair").Patterns.Invoke.Pattern.Invoke();
        AutomationElement confirm = App.Find("Settings.UnpairConfirm");
        Assert.IsTrue(confirm.Patterns.Invoke.IsSupported, "Settings.UnpairConfirm can't be invoked.");
        confirm.Patterns.Invoke.Pattern.Invoke();

        WaitForGone("Settings.Back");
        WaitForPairing();
        Wait.Until(() => !File.Exists(SessionFile), AppDriver.DefaultTimeout, () => "Unpair left session.bin behind.");
    }

    /// <summary>
    /// Unpairing deletes the message database that an opened chat creates.
    /// </summary>
    [TestMethod]
    public async Task Unpair_DeletesMessageDatabase()
    {
        string database = Path.Combine(App.DataDir, "messages.db");

        await OpenMainAsync();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => File.Exists(database), AppDriver.DefaultTimeout, () => "Opening a chat left no messages.db.");
        CloseMainWindow();

        OpenSettings();
        App.Find("Settings.Unpair").Patterns.Invoke.Pattern.Invoke();
        App.Find("Settings.UnpairConfirm").Patterns.Invoke.Pattern.Invoke();
        WaitForPairing();

        Wait.Until(() => !File.Exists(database), AppDriver.DefaultTimeout, () => "Unpair left messages.db behind.");
    }

    private void OpenSettings()
    {
        InvokeTrayMenu("TrayMenu.Settings");
        WaitForSettings().Focus();
    }

    // Maps the text of the selected item back to its index.
    private int ThemeIndex() => Array.IndexOf(ThemeNames, AppDriver.SafeName(App.Find("Settings.Theme").AsComboBox().SelectedItem));

    private string SnoozeLength() => AppDriver.SafeName(App.Find("Settings.SnoozeLength").AsComboBox().SelectedItem);

    private string Microphone() => AppDriver.SafeName(App.Find("Settings.Microphone").AsComboBox().SelectedItem);

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

    // Opens the tray menu, measures the average brightness (0 to 255) of its Open item on screen and closes the menu.
    // UI Automation has no theme property, so the pixels are the check.
    // A light menu is near white, and a dark menu is near black.
    private double TrayMenuBrightness()
    {
        Tray.RightClick();
        AutomationElement item = App.Find("TrayMenu.Open");

        // The menu fades in. Two captures that agree show that the animation is over.
        double last    = -1;
        double current = Brightness(item);
        Wait.Until(() =>
        {
            last    = current;
            current = Brightness(item);
            return Math.Abs(current - last) < 1;
        }, TimeSpan.FromSeconds(5), () => "The tray menu never stopped changing.");

        // Escape closes the menu because its host window has the foreground.
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Wait.Until(() => App.TryFind("TrayMenu.Open") is null, AppDriver.DefaultTimeout, () => "Escape did not close the tray menu.");
        Tray.CloseOverflow();
        TestContext.WriteLine($"Tray menu brightness: {current:0}");
        return current;
    }

    // The system draws the close button. In the app theme, it must contrast with its background: a dark glyph on
    // light, a light glyph on dark. A mismatch with the Windows theme makes the glyph invisible.
    // The method finds the button by position, because UI Automation gives pixels no theme. The area is a strip
    // inside the top right of the window, clear of the invisible resize border.
    private void AssertCloseGlyph(bool light)
    {
        Window window = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);
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

    // The title bar icon area must show an icon: its brightness varies instead of one flat background.
    private void AssertTitleBarIcon()
    {
        Window window = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);
        double spread = 0;
        Wait.Until(() =>
        {
            Rectangle box = window.BoundingRectangle;
            (double low, double high) = Range(new Rectangle(box.Left + 12, box.Top + 8, 30, 44));
            spread = high - low;
            return spread > 40;
        }, TimeSpan.FromSeconds(5), () => $"The title bar icon area looks blank (brightness spread {spread:0}).");
    }

    // The background is the corner pixel of the strip. The glyph is its darkest pixel (light) or lightest pixel (dark).
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
