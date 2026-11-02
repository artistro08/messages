using System.Diagnostics;
using GMessages.App.Startup;
using GMessages.App.Views;
using GMessages.Presentation;
using GMessages.Presentation.Connection;
using GMessages.Presentation.Logging;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Notifications;
using GMessages.Presentation.Pairing;
using GMessages.Presentation.Settings;
using GMessages.Protocol.Http;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.System.Power;
using Windows.Networking.Connectivity;
using Windows.Storage;

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

            // Close The Menu Window Once It Hides (A Hidden WinUI Window Keeps Its XAML Island And D3D Device), Then Trim.
            // Deferred, So The Click Handler That Hid It Finishes First
            var menu = _trayMenu;
            menu.AppWindow.Changed += (sender, e) =>
            {
                if (!e.DidVisibilityChange || sender.IsVisible || _quitting)
                {
                    return;
                }

                _dispatcher?.TryEnqueue(() =>
                {
                    if (!_quitting && ReferenceEquals(_trayMenu, menu) && !menu.AppWindow.IsVisible)
                    {
                        _trayMenu = null;
                        menu.Close();
                        _memoryTrim?.Schedule();
                    }
                });
            };
        }

        var (x, y, edge) = ScreenGeometry.GetMenuAnchor(pointX, pointY);
        _trayMenu.ShowAt(x, y, edge);
    }
}
