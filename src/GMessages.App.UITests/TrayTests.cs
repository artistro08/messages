using FlaUI.Core.AutomationElements;
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
        App.WaitForNoWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);

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
        Assert.AreEqual(AppInfo.WindowTitle, AppDriver.SafeName(App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout)));
        AssertTitleBar("Main.TitleBar", AppInfo.WindowTitle);
    }

    /// <summary>
    /// Turning the tray icon off in Settings takes it away at once and after a restart, and the account menu's Quit then
    /// ends the app.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task TrayIconOff_StaysOff_QuitsFromAccountMenu()
    {
        await PairAsync();

        // Off In Settings: The Icon Goes At Once
        InvokeTrayMenu("TrayMenu.Settings");
        WaitForSettings();
        App.Find("Settings.ShowTrayIcon").AsToggleButton().Toggle();
        Tray.WaitForNoIcon(AppDriver.DefaultTimeout);

        // Restarted (A User Launch Opens The Window): Still No Icon
        await App.RestartAsync();
        App.Find("Main.ConversationList");
        Tray.WaitForNoIcon(AppDriver.DefaultTimeout);

        // The Account Menu Quits
        App.Find("Main.ProfileButton").Patterns.Invoke.Pattern.Invoke();
        App.Find("Main.Profile.Quit").Patterns.Invoke.Pattern.Invoke();
        await App.WaitForExitAsync();
    }

    /// <summary>
    /// With running in the background off, closing the window quits, and the tray icon stays until then (the two
    /// settings are separate).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task RunInBackgroundOff_ClosingTheWindowQuits()
    {
        await PairAsync();

        InvokeTrayMenu("TrayMenu.Settings");
        WaitForSettings();
        App.Find("Settings.RunInBackground").AsToggleButton().Toggle();
        Tray.FindIcon(AppDriver.DefaultTimeout);

        CloseMainWindow();
        await App.WaitForExitAsync();
        Tray.WaitForNoIcon(AppDriver.DefaultTimeout);
    }
}
