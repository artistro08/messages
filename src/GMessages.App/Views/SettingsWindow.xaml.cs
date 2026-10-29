using GMessages.Presentation;
using GMessages.Presentation.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace GMessages.App.Views;

/// <summary>
/// The settings window: theme, Start with Windows, notification switches, unpair and the logs folder.
/// </summary>
/// <remarks>
/// Adapted from Magic Buds' <c>SettingsWindow</c>. Made when asked for and destroyed on close (the App then trims
/// memory). Unpair asks first in a ContentDialog; once unpaired the window closes and the App opens the pairing
/// window.
/// </remarks>
public sealed partial class SettingsWindow : Window
{
    private bool _closed;

    /// <summary>
    /// Creates the window and reads the Start with Windows state.
    /// </summary>
    /// <param name="viewModel">The settings state.</param>
    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Window
        Title                      = $"{AppInfo.DisplayName} Settings";
        AppTitleBar.Title          = Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 620));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // Behavior
        Closed += (_, _) =>
        {
            _closed = true;
            Bindings.StopTracking();
        };
        _ = ViewModel.LoadAsync();
    }

    /// <summary>
    /// Gets the settings state the view binds to.
    /// </summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// Sets the window's light or dark look from the app theme. The confirm dialog follows its parent grid.
    /// </summary>
    /// <param name="theme">The app theme setting.</param>
    public void ApplyTheme(AppTheme theme)
    {
        // Page Colors
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default,
        };

        // Caption Buttons: Drawn By The System, So Tell Them The App Theme (Or They Follow Windows' Instead)
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark  => TitleBarTheme.Dark,
            _              => TitleBarTheme.UseDefaultAppMode,
        };
    }

    // Confirm, Unpair, Then Close: The App Opens The Pairing Window On NotPaired
    private async void OnUnpairClick(object sender, RoutedEventArgs e)
    {
        UnpairDialog.RequestedTheme = RootGrid.RequestedTheme;
        if (await UnpairDialog.ConfirmAndUnpairAsync(ViewModel) && !_closed)
        {
            Close();
        }
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) => ViewModel.OpenLogsFolder();
}
