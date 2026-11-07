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
    /// Tells whether a key is down now, on the UI thread.
    /// </summary>
    /// <remarks>
    /// The method reads the Win32 key state of the thread, where the high bit means down. The CoreWindow of an island
    /// is hidden, so it cannot answer.
    /// </remarks>
    /// <param name="key">The key.</param>
    /// <returns>True while the key is down.</returns>
    public static bool IsDown(VirtualKey key) => NativeMethods.GetKeyState((int)key) < 0;

    /// <summary>
    /// Shows an element only when there is text.
    /// </summary>
    /// <param name="text">The text, or null.</param>
    /// <returns>Visible for text that is not empty, else Collapsed.</returns>
    public static Visibility ShownIfText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Puts text on the clipboard, on the UI thread.
    /// </summary>
    /// <remarks>
    /// Private text such as a one-time code stays out of the clipboard history of Windows. It does not sync to other
    /// devices. When another app holds the clipboard, the call throws or Windows refuses it. The copy then does
    /// nothing, and the log names only the error type.
    /// </remarks>
    /// <param name="text">The text.</param>
    /// <param name="keepPrivate">True to keep the text out of the clipboard history and the sync.</param>
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
    /// Finds the first <see cref="ScrollViewer"/> under an element, such as the scroller in the template of a list.
    /// </summary>
    /// <remarks>
    /// In the Native AOT build, the visual tree gives back the scroller of a template as a base type, a
    /// <see cref="ContentControl"/>. Thus a plain <c>as ScrollViewer</c> finds nothing there. The method finds the
    /// template part by its name instead. It then uses a WinRT cast, a COM QueryInterface that needs no type lookup.
    /// </remarks>
    /// <param name="root">The element to search under.</param>
    /// <returns>The scroller, or null before the element has its template.</returns>
    public static ScrollViewer? FindScroller(DependencyObject root) => FindPart<ScrollViewer>(root, "ScrollViewer");

    /// <summary>
    /// Finds the first element with the given name under an element, as a type. The search is AOT safe. See
    /// <see cref="FindScroller"/>.
    /// </summary>
    /// <typeparam name="T">The type of the element.</typeparam>
    /// <param name="root">The element to search under.</param>
    /// <param name="partName">The name of the element or template part, such as "ScrollViewer".</param>
    /// <returns>The element, or null while there is none.</returns>
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
