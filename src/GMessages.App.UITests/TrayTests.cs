using FlaUI.Core.AutomationElements;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;

namespace GMessages.App.UITests;

/// <summary>
/// The tray icon right-click menu and left-click. The tests click through the real taskbar.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class TrayTests : UiTest
{
    /// <summary>
    /// A right-click opens the menu with Open, Settings and Quit. Each item does its job. Quit ends the process and
    /// removes the icon.
    /// </summary>
    [TestMethod]
    public async Task TrayRightClick_ShowsFlyoutMenu_OpenSettingsQuit()
    {
        // The app is not paired, so Open shows the pairing window again after the test closes it.
        WaitForPairing();
        App.Find("Pairing.Cancel").Patterns.Invoke.Pattern.Invoke();
        App.WaitForNoWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);

        Tray.RightClick();
        App.Find("TrayMenu.Open");
        App.Find("TrayMenu.Settings");
        App.Find("TrayMenu.Quit");

        App.Find("TrayMenu.Open").Patterns.Invoke.Pattern.Invoke();
        Tray.CloseOverflow();
        WaitForPairing();

        InvokeTrayMenu("TrayMenu.Settings");
        WaitForSettings();

        InvokeTrayMenu("TrayMenu.Quit");
        await App.WaitForExitAsync();
        Tray.WaitForNoIcon(AppDriver.DefaultTimeout);
    }

    /// <summary>
    /// After pairing, a left-click on the icon opens the main window with its conversation list.
    /// </summary>
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
    /// Turning the tray icon off in Settings removes it at once and after a restart. The Quit item of the account menu
    /// then ends the app.
    /// </summary>
    [TestMethod]
    public async Task TrayIconOff_StaysOff_QuitsFromAccountMenu()
    {
        await PairAsync();

        InvokeTrayMenu("TrayMenu.Settings");
        WaitForSettings();
        App.Find("Settings.ShowTrayIcon").AsToggleButton().Toggle();
        Tray.WaitForNoIcon(AppDriver.DefaultTimeout);

        // A launch by the user opens the window, so the restart shows the conversation list.
        await App.RestartAsync();
        App.Find("Main.ConversationList");
        Tray.WaitForNoIcon(AppDriver.DefaultTimeout);

        App.Find("Main.ProfileButton").Patterns.Invoke.Pattern.Invoke();
        App.Find("Main.Profile.Quit").Patterns.Invoke.Pattern.Invoke();
        await App.WaitForExitAsync();
    }

    /// <summary>
    /// With run in the background off, closing the window quits the app. The tray icon stays until then, because the
    /// two settings are separate.
    /// </summary>
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
