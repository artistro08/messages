using GMessages.Presentation.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GMessages.App.Views;

/// <summary>
/// The settings page, shown over the whole main window below the title bar: theme, Start with Windows, notification
/// switches, unpair and the logs folder, with a Back button.
/// </summary>
/// <remarks>
/// Adapted from Magic Buds' <c>SettingsWindow</c>. Made when opened and dropped on Back (or with the window). The view
/// model belongs to the main window, so the page never disposes it. Unpair asks the window, which owns the confirm
/// dialog; once unpaired the App switches the window to pairing.
/// </remarks>
public sealed partial class SettingsPage : UserControl
{
    /// <summary>
    /// Creates the page and reads the Start with Windows state.
    /// </summary>
    /// <param name="viewModel">The settings state (owned by the main window).</param>
    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Keyboard Focus Moves Into The Page, So Tab Doesn't Start In The Hidden Chats
        Loaded += (_, _) => BackButton.Focus(FocusState.Programmatic);
        _ = ViewModel.LoadAsync();
    }

    /// <summary>
    /// Asks the window to close the page (Back).
    /// </summary>
    public event EventHandler? BackRequested;

    /// <summary>
    /// Asks the window to confirm and unpair (Unpair this PC).
    /// </summary>
    public event EventHandler? UnpairRequested;

    /// <summary>
    /// Gets the settings state the view binds to.
    /// </summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// Flips a flag for a binding (the Unpair button is enabled while no unpair runs).
    /// </summary>
    /// <param name="value">The flag.</param>
    /// <returns>The opposite.</returns>
    public static bool Not(bool value) => !value;

    /// <summary>
    /// Stops the page's bindings once the window drops it, so the long-lived view model no longer updates it.
    /// </summary>
    public void Close() => Bindings.StopTracking();

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void OnUnpairClick(object sender, RoutedEventArgs e) => UnpairRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) => ViewModel.OpenLogsFolder();
}
