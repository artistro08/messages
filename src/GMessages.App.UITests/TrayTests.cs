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
        // Unpaired, So Open Brings Back The Window On Pairing Once It's Closed
        WaitForPairing();
        App.Find("Pairing.Cancel").Patterns.Invoke.Pattern.Invoke();
        App.WaitForNoWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout);

        // The Menu Shows All Three Items
        Tray.RightClick();
        App.Find("TrayMenu.Open");
        App.Find("TrayMenu.Settings");
        App.Find("TrayMenu.Quit");

        // Open
        App.Find("TrayMenu.Open").Patterns.Invoke.Pattern.Invoke();
        Tray.CloseOverflow();
        WaitForPairing();

        // Settings
        InvokeTrayMenu("TrayMenu.Settings");
        WaitForSettings();

        // Quit
        InvokeTrayMenu("TrayMenu.Quit");
        await App.WaitForExitAsync();
        Tray.WaitForNoIcon(AppDriver.DefaultTimeout);
    }

    /// <summary>
    /// Once paired, a left-click on the icon opens the main window with its conversation list.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task TrayLeftClick_OpensMainWindow_ShowsConnected()
    {
        await PairAsync();

        Tray.LeftClick();
        App.Find("Main.ConversationList");
        Assert.AreEqual(AppInfo.DisplayName, AppDriver.SafeName(App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout)));
        AssertTitleBar("Main.TitleBar", AppInfo.DisplayName);
    }
}
