namespace GMessages.Presentation.Placement;

/// <summary>
/// Finds the place where the tray menu opens on the monitor of the tray icon.
/// </summary>
/// <remarks>
/// Ported from Magic Buds. The edge detection and the margin come from <c>FlyoutPlacement.DetectEdge</c> and
/// <c>FlyoutPlacement.MarginDip</c>. The anchor math comes from <c>ScreenGeometry.GetMenuAnchor</c>. The Win32
/// monitor lookup stays in the App. This app has no flyout, so the port leaves out the flyout rectangle math.
/// </remarks>
public static class MenuPlacement
{
    /// <summary>
    /// The gap between the menu and the taskbar, in device-independent pixels.
    /// </summary>
    public const int MarginDip = 12;

    /// <summary>
    /// Finds the taskbar edge from a comparison of the monitor with its work area.
    /// </summary>
    /// <remarks>
    /// An auto-hidden taskbar takes no work area, so the method returns the bottom edge for it.
    /// </remarks>
    /// <param name="monitor">The full bounds of the monitor.</param>
    /// <param name="workArea">The work area of that monitor.</param>
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
    /// Moves a tray click point from the taskbar to the edge of the work area plus the margin.
    /// </summary>
    /// <remarks>
    /// The coordinate along the taskbar stays at the click. The other coordinate moves to the edge of the work area.
    /// </remarks>
    /// <param name="x">The X of the click, in screen pixels.</param>
    /// <param name="y">The Y of the click, in screen pixels.</param>
    /// <param name="monitor">The full bounds of the monitor under the click.</param>
    /// <param name="workArea">The work area of that monitor.</param>
    /// <param name="scale">The DPI of the monitor divided by 96.</param>
    /// <returns>The anchor point and the taskbar edge. The menu opens on the work area side of that edge.</returns>
    public static (int X, int Y, TaskbarEdge Edge) GetAnchor(int x, int y, PixelRect monitor, PixelRect workArea, double scale)
    {
        TaskbarEdge edge = DetectEdge(monitor, workArea);
        int margin       = (int)Math.Round(MarginDip * scale);

        return edge switch
        {
            TaskbarEdge.Top   => (x, workArea.Top + margin, edge),
            TaskbarEdge.Left  => (workArea.Left + margin, y, edge),
            TaskbarEdge.Right => (workArea.Right - margin, y, edge),
            _                 => (x, workArea.Bottom - margin, edge),
        };
    }
}
