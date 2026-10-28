using GMessages.App.UITests.Driver;
using GMessages.Presentation;

namespace GMessages.App.UITests;

/// <summary>
/// The tray icon: its right-click menu and its left-click, clicked through the real taskbar.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class TrayTests : UiTest
{
    /// <summary>
    /// A right-click opens the menu with Open, Settings and Quit; each does its job, and Quit ends the process and
    /// removes the icon.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task TrayRightClick_ShowsFlyoutMenu_OpenSettingsQuit()
    {
        // Unpaired, So Open Brings Back The Pairing Window Once It's Closed
        App.WaitForWindow(PairingTitle, AppDriver.DefaultTimeout);
        App.Find("Pairing.Cancel").Patterns.Invoke.Pattern.Invoke();
        App.WaitForNoWindow(PairingTitle, AppDriver.DefaultTimeout);

        // The Menu Shows All Three Items
        Tray.RightClick();
        App.Find("TrayMenu.Open");
        App.Find("TrayMenu.Settings");
        App.Find("TrayMenu.Quit");

        // Open
        App.Find("TrayMenu.Open").Patterns.Invoke.Pattern.Invoke();
        Tray.CloseOverflow();
        App.WaitForWindow(PairingTitle, AppDriver.DefaultTimeout);

        // Settings
        InvokeTrayMenu("TrayMenu.Settings");
        App.WaitForWindow(SettingsTitle, AppDriver.DefaultTimeout);

        // Quit
        InvokeTrayMenu("TrayMenu.Quit");
        await App.WaitForExitAsync();
        Tray.WaitForNoIcon(AppDriver.DefaultTimeout);
    }

    /// <summary>
    /// Once paired, a left-click on the icon opens the main window, which shows Connected.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task TrayLeftClick_OpensMainWindow_ShowsConnected()
    {
        await PairAsync();

        Tray.LeftClick();
        App.WaitForText("Main.Status", "Connected");
        Assert.AreEqual(AppInfo.DisplayName, AppDriver.SafeName(App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout)));
    }
}
