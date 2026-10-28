using GMessages.Presentation;
using GMessages.Presentation.Connection;
using GMessages.Presentation.Settings;
using Microsoft.UI.Xaml;

namespace GMessages.App.Views;

/// <summary>
/// Placeholder main window: shows where the app stands with the phone. Plan 3 replaces its content.
/// </summary>
/// <remarks>
/// Made when asked for and destroyed on close (the App then trims memory). Follows the controller's
/// <see cref="ConnectionController.StateChanged"/>, which always arrives on the UI thread.
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly ConnectionController _connection;

    // TODO: Plan 3 shows the paired account here once the client exposes the Config email

    /// <summary>
    /// Creates the window and shows the current connection state.
    /// </summary>
    /// <param name="connection">The connection whose state shows.</param>
    public MainWindow(ConnectionController connection)
    {
        _connection = connection;
        InitializeComponent();

        // Window
        Title = AppInfo.DisplayName;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(480, 360));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // Behavior
        _connection.StateChanged += OnStateChanged;
        Closed                   += (_, _) => _connection.StateChanged -= OnStateChanged;
        ShowState();
    }

    /// <summary>
    /// Sets the window's light or dark look from the app theme.
    /// </summary>
    /// <param name="theme">The app theme setting.</param>
    public void ApplyTheme(AppTheme theme) => RootGrid.RequestedTheme = theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark  => ElementTheme.Dark,
        _              => ElementTheme.Default,
    };

    /// <summary>
    /// Gives the friendly text for a connection state.
    /// </summary>
    /// <param name="state">The connection state.</param>
    /// <returns>The text to show.</returns>
    public static string TextFor(ConnectionState state) => state switch
    {
        ConnectionState.Connecting   => "Connecting…",
        ConnectionState.Connected    => "Connected",
        ConnectionState.Reconnecting => "Reconnecting…",
        ConnectionState.SignedOut    => "Signed out",
        _                            => "Not paired",
    };

    private void OnStateChanged(object? sender, EventArgs e) => ShowState();

    private void ShowState() => StatusText.Text = TextFor(_connection.State);
}
