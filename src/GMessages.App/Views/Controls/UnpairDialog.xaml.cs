using GMessages.Presentation.Settings;
using Microsoft.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// The "Unpair this PC?" confirm, shared by the settings page and the main window's profile menu.
/// </summary>
/// <remarks>
/// Place it in the window's tree (that gives it the window's <c>XamlRoot</c>) and set its <c>RequestedTheme</c> to the
/// window's before showing it.
/// </remarks>
public sealed partial class UnpairDialog : ContentDialog
{
    /// <summary>
    /// Creates the dialog.
    /// </summary>
    public UnpairDialog() => InitializeComponent();

    /// <summary>
    /// Asks first, then unpairs through the settings view model when the user confirms.
    /// </summary>
    /// <remarks>Once unpaired, the App switches the main window to pairing.</remarks>
    /// <param name="settings">Does the unpair.</param>
    /// <returns>True when the user confirmed and the PC is unpaired.</returns>
    public async Task<bool> ConfirmAndUnpairAsync(SettingsViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (await ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        await settings.UnpairAsync();
        return true;
    }
}
