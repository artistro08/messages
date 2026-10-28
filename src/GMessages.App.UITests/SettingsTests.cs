using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using GMessages.App.UITests.Driver;

namespace GMessages.App.UITests;

/// <summary>
/// The settings window: values that survive a restart, the live tray menu theme, and unpairing.
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
    /// Theme, both notification switches and Start with Windows (the test marker file) survive a quit and relaunch.
    /// The tray menu follows a theme change live.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Settings_ToggleAndRestart_Persists()
    {
        string marker = Path.Combine(App.DataDir, "startup-enabled");
        OpenSettings();

        // Defaults
        Assert.AreEqual(0, ThemeIndex(), "Theme should start at 'Use system setting'.");
        WaitForToggle("Settings.Notifications", ToggleState.On);
        WaitForToggle("Settings.NotifyViewedChat", ToggleState.Off);
        WaitForToggle("Settings.StartWithWindows", ToggleState.Off);

        // Theme Flips The Tray Menu Live, Both Ways (So It Doesn't Depend On The System Theme)
        SelectTheme(LightIndex);
        Assert.IsTrue(TrayMenuBrightness() > 160, "The tray menu didn't turn light after choosing Light.");
        SelectTheme(DarkIndex);
        Assert.IsTrue(TrayMenuBrightness() < 96, "The tray menu didn't turn dark after choosing Dark.");

        // Switches; Start With Windows Writes The Test Marker
        Toggle("Settings.Notifications", ToggleState.Off);
        Toggle("Settings.NotifyViewedChat", ToggleState.On);
        Toggle("Settings.StartWithWindows", ToggleState.On);
        Wait.Until(() => File.Exists(marker), AppDriver.DefaultTimeout, () => "Start with Windows didn't write the startup-enabled marker.");

        // Restart: Everything Comes Back
        await App.RestartAsync();
        OpenSettings();
        Wait.Until(() => ThemeIndex() == DarkIndex, AppDriver.DefaultTimeout, () => $"Theme came back as index {ThemeIndex()}, not Dark.");
        WaitForToggle("Settings.Notifications", ToggleState.Off);
        WaitForToggle("Settings.NotifyViewedChat", ToggleState.On);
        WaitForToggle("Settings.StartWithWindows", ToggleState.On);
    }

    /// <summary>
    /// Unpair asks first; confirming closes settings, deletes the session and brings back the pairing window.
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
        App.WaitForNoWindow(SettingsTitle, AppDriver.DefaultTimeout);
        App.WaitForWindow(PairingTitle, AppDriver.DefaultTimeout);
        Wait.Until(() => !File.Exists(SessionFile), AppDriver.DefaultTimeout, () => "Unpair left session.bin behind.");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private void OpenSettings()
    {
        InvokeTrayMenu("TrayMenu.Settings");
        App.WaitForWindow(SettingsTitle, AppDriver.DefaultTimeout).Focus();
    }

    // The Selected Item's Text, Mapped Back To Its Index
    private int ThemeIndex() => Array.IndexOf(ThemeNames, AppDriver.SafeName(App.Find("Settings.Theme").AsComboBox().SelectedItem));

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

        // Settings Is Already Open, So This Only Closes The Menu
        App.Find("TrayMenu.Settings").Patterns.Invoke.Pattern.Invoke();
        Tray.CloseOverflow();
        TestContext.WriteLine($"Tray menu brightness: {current:0}");
        return current;
    }

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
