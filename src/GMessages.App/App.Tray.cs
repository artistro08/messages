using GMessages.Presentation.Settings;

namespace GMessages.App;

/// <summary>
/// The tray icon: which icon identity this run uses, and the right-click menu window made on first use.
/// </summary>
/// <remarks>
/// Part of <see cref="App"/>; the other members are in App.xaml.cs.
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

    // The right-click menu's window is made on first use, since most sessions never open it
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
            _trayMenu.OpenRequested     += (_, _) => ShowMainWindowRequested?.Invoke(this, "menu");
            _trayMenu.SettingsRequested += (_, _) => ShowSettings();
            _trayMenu.QuitRequested     += (_, _) => Quit();

            // Kept Once Made, Hidden Between Uses: Closing A Menu Window (Its XAML Island) Stopped XAML Drawing Anything
            // In The Next Window Opened. Trimmed Once It Hides
            _trayMenu.Hidden += (_, _) =>
            {
                if (!_quitting)
                {
                    _memoryTrim?.Schedule();
                }
            };
        }

        var (x, y, edge) = ScreenGeometry.GetMenuAnchor(pointX, pointY);
        _trayMenu.ShowAt(x, y, edge);
    }
}
