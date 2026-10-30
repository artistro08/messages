using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Gives every dialog WinUI's built-in Fluent look: rounded corners, the title and the button row.
/// </summary>
/// <remarks>
/// WinUI's implicit dialog style only reaches a plain <see cref="ContentDialog"/> in the visual tree. One built in code,
/// or a subclass such as <see cref="UnpairDialog"/>, falls back to the old square style unless its style is set. Every
/// dialog built in code goes through <see cref="Create"/>, and the XAML subclasses call <see cref="UseFluentStyle"/> in their constructors; the resource is read with a WinRT cast (Native AOT safe).
/// </remarks>
internal static class Dialogs
{
    /// <summary>
    /// Creates a Fluent-styled dialog on a window's root, in the window's current theme.
    /// </summary>
    /// <param name="root">The XAML root the dialog opens on.</param>
    /// <returns>The dialog; the caller sets its title, content and buttons.</returns>
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
    /// Sets a dialog's style to WinUI's <c>DefaultContentDialogStyle</c>.
    /// </summary>
    /// <param name="dialog">The dialog.</param>
    public static void UseFluentStyle(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        dialog.Style = Application.Current.Resources["DefaultContentDialogStyle"].As<Style>();
    }
}
