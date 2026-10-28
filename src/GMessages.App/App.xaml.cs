using Microsoft.UI.Xaml;

namespace GMessages.App;

/// <summary>
/// Tray app composition root. No window opens at launch; later tasks add the tray icon and services here.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Creates the app. Closing a window must never end a tray app, so it only shuts down on request.
    /// </summary>
    public App()
    {
        InitializeComponent();

        // Closing the last window must not end a tray app
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }
}
