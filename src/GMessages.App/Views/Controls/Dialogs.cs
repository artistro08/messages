using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using WinRT;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Gives each dialog the built-in Fluent look of WinUI: rounded corners, the title and the button row.
/// </summary>
/// <remarks>
/// The implicit dialog style of WinUI reaches only a plain <see cref="ContentDialog"/> in the visual tree. A dialog
/// from code, or a subclass such as <see cref="UnpairDialog"/>, gets the old square style unless its style is set. Each
/// dialog from code goes through <see cref="Create"/>. The XAML subclasses call <see cref="UseFluentStyle"/> in their
/// constructors. A WinRT cast reads the resource, which is safe under Native AOT.
/// </remarks>
internal static class Dialogs
{
    /// <summary>
    /// Creates a dialog with the Fluent style on the root of a window, in the current theme of the window.
    /// </summary>
    /// <param name="root">The XAML root that the dialog opens on.</param>
    /// <returns>The dialog. The caller sets its title, content and buttons.</returns>
    public static ContentDialog Create(XamlRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var dialog = new ContentDialog
        {
            XamlRoot       = root,
            RequestedTheme = root.Content.As<FrameworkElement>().ActualTheme,
        };
        UseFluentStyle(dialog);

        return dialog;
    }

    /// <summary>
    /// Sets the style of a dialog to the <c>DefaultContentDialogStyle</c> of WinUI.
    /// </summary>
    /// <param name="dialog">The dialog.</param>
    public static void UseFluentStyle(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        dialog.Style = Application.Current.Resources["DefaultContentDialogStyle"].As<Style>();
    }
}
