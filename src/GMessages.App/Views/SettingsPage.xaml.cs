using GMessages.Presentation;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GMessages.App.Views;

/// <summary>
/// The settings page, shown over the whole main window below the title bar: theme, Start with Windows, notification
/// switches and the snooze length, unpair, the logs folder and About, each on a Windows Settings card.
/// </summary>
/// <remarks>
/// Adapted from Magic Buds' <c>SettingsWindow</c>, its cards and its About page. Made when opened and dropped on the
/// title bar's Back (or with the window). The view model belongs to the main window, so the page never disposes it.
/// Unpair asks the window, which owns the confirm dialog; once unpaired the App switches the window to pairing.
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

        // Copy
        SnoozeHeader.Text      = ChatCopy.SnoozeLength;
        SnoozeDescription.Text = ChatCopy.SnoozeLengthDescription;
        AutomationProperties.SetName(SnoozeBox, ChatCopy.SnoozeLength);

        // Snooze Choices: Added Here, Before The Bindings Select One (A .NET List Isn't A XAML ItemsSource Under AOT)
        foreach (string choice in ViewModel.SnoozeChoices)
        {
            SnoozeBox.Items.Add(choice);
        }

        _ = ViewModel.LoadAsync();
    }

    /// <summary>
    /// Asks the window to confirm and unpair (Unpair this PC).
    /// </summary>
    public event EventHandler? UnpairRequested;

    /// <summary>
    /// Gets the settings state the view binds to.
    /// </summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>Gets the app's name for About.</summary>
    public static string AppName => AppInfo.DisplayName;

    /// <summary>Gets "Version 1.2.3" for About.</summary>
    public static string VersionText => $"Version {AppInfo.Version}";

    /// <summary>Gets the repository card's text for About.</summary>
    public static string RepositoryText => $"{AppInfo.DisplayName} source code, releases and issues.";

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

    private void OnUnpairClick(object sender, RoutedEventArgs e) => UnpairRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) => ViewModel.OpenLogsFolder();
}
