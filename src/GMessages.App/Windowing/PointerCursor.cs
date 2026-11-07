using System.Runtime.InteropServices;
using Windows.UI.Xaml;

namespace GMessages.App.Windowing;

/// <summary>
/// Sets the shape of the pointer over an element, as UIElement.ProtectedCursor of WinUI 3 does. System XAML does not
/// have that property.
/// </summary>
/// <remarks>
/// The class calls Win32 SetCursor each time the pointer moves over the element. The island sets the arrow again on
/// the next WM_SETCURSOR, so the class sets the cursor again on each move. When the pointer leaves the element, the
/// arrow stays.
/// </remarks>
internal static partial class PointerCursor
{
    /// <summary>
    /// The left-right resize arrows (IDC_SIZEWE).
    /// </summary>
    public const int SizeWestEast = 32644;

    /// <summary>
    /// The pointing hand (IDC_HAND).
    /// </summary>
    public const int Hand = 32649;

    /// <summary>
    /// Shows a cursor while the pointer is over the element.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="cursor">A system cursor ID (<see cref="SizeWestEast"/>, <see cref="Hand"/>).</param>
    public static void Use(UIElement element, int cursor)
    {
        ArgumentNullException.ThrowIfNull(element);

        nint handle            = WindowInterop.LoadCursorW(0, cursor);
        element.PointerEntered += (_, _) => _ = SetCursor(handle);
        element.PointerMoved   += (_, _) => _ = SetCursor(handle);
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetCursor(nint cursor);
}
