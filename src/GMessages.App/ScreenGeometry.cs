using System.Runtime.InteropServices;
using GMessages.Presentation.Placement;

namespace GMessages.App;

/// <summary>
/// Reads the monitor under a tray click and turns it into a menu anchor.
/// </summary>
/// <remarks>
/// Ported from Magic Buds' <c>ScreenGeometry.GetMenuAnchor</c>; the flyout placement was dropped. The math
/// lives in <see cref="MenuPlacement"/> so it can be tested; this class only does the Win32 lookups.
/// </remarks>
internal static class ScreenGeometry
{
    /// <summary>
    /// Where the tray menu opens for a click at a screen point: moved off the taskbar to the work area's
    /// edge plus <see cref="MenuPlacement.MarginDip"/>.
    /// </summary>
    /// <param name="x">Click X in screen pixels.</param>
    /// <param name="y">Click Y in screen pixels.</param>
    /// <returns>The anchor point and the taskbar edge the menu grows away from.</returns>
    public static (int X, int Y, TaskbarEdge Edge) GetMenuAnchor(int x, int y)
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info    = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfoW(monitor, ref info);
        var scale = NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        return MenuPlacement.GetAnchor(x, y, ToPixelRect(info.rcMonitor), ToPixelRect(info.rcWork), scale);
    }

    private static PixelRect ToPixelRect(NativeMethods.RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
