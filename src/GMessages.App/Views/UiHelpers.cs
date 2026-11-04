using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
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
    /// <param name="key">The key.</param>
    /// <returns>True while the key is down.</returns>
    public static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// Shows an element only when there is text.
    /// </summary>
    /// <param name="text">The text, or null.</param>
    /// <returns>Visible for non-empty text.</returns>
    public static Visibility ShownIfText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Shows an element only while a flag is off, for <c>x:Bind</c>.
    /// </summary>
    /// <param name="value">The flag.</param>
    /// <returns>Collapsed while on, else visible.</returns>
    public static Visibility ShownIfOff(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

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
