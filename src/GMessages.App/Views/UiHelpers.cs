using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using WinRT;

namespace GMessages.App.Views;

/// <summary>
/// Small helpers that several views and XAML bindings share.
/// </summary>
internal static class UiHelpers
{
    /// <summary>
    /// Tells whether a key is held down right now, on the UI thread.
    /// </summary>
    /// <remarks>Win32's thread key state (the high bit is down); an island's CoreWindow is hidden, so it can't answer.</remarks>
    /// <param name="key">The key.</param>
    /// <returns>True while the key is down.</returns>
    public static bool IsDown(VirtualKey key) => NativeMethods.GetKeyState((int)key) < 0;

    /// <summary>
    /// Shows an element only when there is text.
    /// </summary>
    /// <param name="text">The text, or null.</param>
    /// <returns>Visible for non-empty text.</returns>
    public static Visibility ShownIfText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Puts text on the clipboard, on the UI thread.
    /// </summary>
    /// <remarks>
    /// Kept private, it stays out of Windows' clipboard history and isn't synced to other devices (a one-time code).
    /// Another app holding the clipboard throws, or Windows refuses it; the copy then does nothing, logged by type.
    /// </remarks>
    /// <param name="text">The text.</param>
    /// <param name="keepPrivate">True to keep it out of clipboard history and sync.</param>
    public static void ToClipboard(string text, bool keepPrivate = false)
    {
        var package = new DataPackage();
        package.SetText(text);

        try
        {
            if (!keepPrivate)
            {
                Clipboard.SetContent(package);
            }
            else if (!Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false }))
            {
                AppLog.CopyFailed(App.ShellLog, "Refused");
            }
        }
        catch (COMException ex)
        {
            AppLog.CopyFailed(App.ShellLog, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Finds the first <see cref="ScrollViewer"/> under an element (a list's template holds one).
    /// </summary>
    /// <remarks>
    /// In the Native AOT build, the visual tree hands a template's scroller back as a base type (a
    /// <see cref="ContentControl"/>), so a plain <c>as ScrollViewer</c> finds nothing there. The template part is
    /// found by its name instead and cast with a WinRT cast (a COM QueryInterface), which needs no type lookup.
    /// </remarks>
    /// <param name="root">The element to search under.</param>
    /// <returns>The scroller, or null before the element has its template.</returns>
    public static ScrollViewer? FindScroller(DependencyObject root) => FindPart<ScrollViewer>(root, "ScrollViewer");

    /// <summary>
    /// Finds the first element under an element that has the given name, as a type (AOT safe; see
    /// <see cref="FindScroller"/>).
    /// </summary>
    /// <typeparam name="T">The element's type.</typeparam>
    /// <param name="root">The element to search under.</param>
    /// <param name="partName">The element's or template part's name, such as "ScrollViewer".</param>
    /// <returns>The element, or null when there is none (yet).</returns>
    public static T? FindPart<T>(DependencyObject root, string partName)
        where T : class
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            T? part = child is FrameworkElement element && element.Name == partName ? element as T ?? element.As<T>() : null;
            if ((part ?? FindPart<T>(child, partName)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
