namespace GMessages.Presentation.Placement;

/// <summary>
/// Where the tray menu opens on the monitor that holds the tray icon.
/// </summary>
/// <remarks>
/// Ported from Magic Buds: the edge detection and margin are <c>FlyoutPlacement.DetectEdge</c> and
/// <c>FlyoutPlacement.MarginDip</c>, and the anchor math is <c>ScreenGeometry.GetMenuAnchor</c> with the Win32
/// monitor lookup left in the App. The flyout rectangle math was dropped; this app has no flyout.
/// </remarks>
public static class MenuPlacement
{
    /// <summary>
    /// Gap between the menu and the taskbar, in device-independent pixels.
    /// </summary>
    public const int MarginDip = 12;

    /// <summary>
    /// Finds the taskbar edge by comparing the monitor to its work area.
    /// </summary>
    /// <remarks>
    /// An auto-hidden taskbar takes no work area, so it counts as bottom.
    /// </remarks>
    /// <param name="monitor">Full bounds of the monitor.</param>
    /// <param name="workArea">That monitor's work area.</param>
    /// <returns>The edge the taskbar sits on.</returns>
    public static TaskbarEdge DetectEdge(PixelRect monitor, PixelRect workArea)
    {
        if (workArea.Bottom < monitor.Bottom)
        {
            return TaskbarEdge.Bottom;
        }
        if (workArea.Top > monitor.Top)
        {
            return TaskbarEdge.Top;
        }
        if (workArea.Left > monitor.Left)
        {
            return TaskbarEdge.Left;
        }
        if (workArea.Right < monitor.Right)
        {
            return TaskbarEdge.Right;
        }
        return TaskbarEdge.Bottom;
    }

    /// <summary>
    /// Moves a tray click point off the taskbar to the work area's edge plus the margin.
    /// </summary>
    /// <remarks>
    /// The coordinate along the taskbar stays where the click was; the other one moves to the work area edge.
    /// </remarks>
    /// <param name="x">Click X in screen pixels.</param>
    /// <param name="y">Click Y in screen pixels.</param>
    /// <param name="monitor">Full bounds of the monitor under the click.</param>
    /// <param name="workArea">That monitor's work area.</param>
    /// <param name="scale">Monitor DPI divided by 96.</param>
    /// <returns>The anchor point and the taskbar edge the menu grows away from.</returns>
    public static (int X, int Y, TaskbarEdge Edge) GetAnchor(int x, int y, PixelRect monitor, PixelRect workArea, double scale)
    {
        var edge   = DetectEdge(monitor, workArea);
        var margin = (int)Math.Round(MarginDip * scale);

        return edge switch
        {
            TaskbarEdge.Top   => (x, workArea.Top + margin, edge),
            TaskbarEdge.Left  => (workArea.Left + margin, y, edge),
            TaskbarEdge.Right => (workArea.Right - margin, y, edge),
            _                 => (x, workArea.Bottom - margin, edge),
        };
    }
}
