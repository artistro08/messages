using GMessages.Presentation.Settings;

namespace GMessages.App;

/// <summary>
/// Holds the tray icon side of the app: the icon identity of this run, and the right-click menu window.
/// </summary>
/// <remarks>
/// This part of <see cref="App"/> holds the tray icon side. App.xaml.cs holds the other members.
/// </remarks>
public sealed partial class App
{
    private static Guid SelectTrayGuid()
    {
#if EnableTestHooks
        if (Program.IsTestRun)
        {
            return TestTrayGuid;
        }
#endif
        return AppIdentity.IsPackaged ? PackagedTrayGuid : ClassicTrayGuid;
    }

    // The app makes the menu window on the first right-click, because most sessions never open the menu.
    private void ShowTrayMenu(int pointX, int pointY)
    {
        if (_quitting)
        {
            return;
        }

        if (_trayMenu is null)
        {
            _trayMenu = new TrayMenuWindow();
            _trayMenu.ApplyTheme(_settings?.Theme ?? AppTheme.System);
            _trayMenu.OpenRequested     += (_, _) => OnShowMainWindowRequested("menu");
            _trayMenu.SettingsRequested += (_, _) => ShowSettings();
            _trayMenu.QuitRequested     += (_, _) => Quit();

            // The app keeps the menu window and hides it between uses. A close of the window also closes its XAML
            // island, and XAML then draws nothing in the next window. The memory trims after each hide.
            _trayMenu.Hidden += (_, _) => ScheduleTrim();
        }

        var (x, y, edge) = ScreenGeometry.GetMenuAnchor(pointX, pointY);
        _trayMenu.ShowAt(x, y, edge);
    }
}
