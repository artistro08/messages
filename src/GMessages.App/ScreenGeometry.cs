using System.Runtime.InteropServices;
using GMessages.Presentation.Placement;

namespace GMessages.App;

/// <summary>
/// Reads the monitor under a tray click and makes a menu anchor from it.
/// </summary>
/// <remarks>
/// Ported from <c>ScreenGeometry.GetMenuAnchor</c> of Magic Buds, without the flyout placement. The math is in
/// <see cref="MenuPlacement"/>, so tests can check it. This class only does the Win32 lookups.
/// </remarks>
internal static class ScreenGeometry
{
    /// <summary>
    /// Gets the place where the tray menu opens for a click at a screen point. The anchor moves off the taskbar to the
    /// edge of the work area, plus <see cref="MenuPlacement.MarginDip"/>.
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
