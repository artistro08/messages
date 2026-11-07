using GMessages.Presentation.Settings;
using Windows.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Asks "Unpair this PC?" for the settings page and for the profile menu of the main window.
/// </summary>
/// <remarks>
/// Place the dialog in the tree of the window, which gives it the <c>XamlRoot</c> of the window. Before it shows, set
/// its <c>RequestedTheme</c> to the theme of the window.
/// </remarks>
public sealed partial class UnpairDialog : ContentDialog
{
    /// <summary>
    /// Creates the dialog.
    /// </summary>
    public UnpairDialog()
    {
        InitializeComponent();
        Dialogs.UseFluentStyle(this);
    }

    /// <summary>
    /// Asks first, then unpairs through the settings view model when the user confirms.
    /// </summary>
    /// <remarks>After the unpair, the App switches the main window to pairing.</remarks>
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
