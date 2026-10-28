namespace GMessages.Presentation.Placement;

/// <summary>
/// Screen edge the taskbar sits on. Ported from Magic Buds' <c>MagicBuds.Presentation.Placement</c>.
/// </summary>
public enum TaskbarEdge
{
    /// <summary>
    /// Along the bottom, the Windows default. Also used for an auto-hidden taskbar.
    /// </summary>
    Bottom,

    /// <summary>
    /// Along the top.
    /// </summary>
    Top,

    /// <summary>
    /// Down the left side.
    /// </summary>
    Left,

    /// <summary>
    /// Down the right side.
    /// </summary>
    Right,
}

/// <summary>
/// Screen rectangle in physical pixels. Right and bottom are exclusive. Ported from Magic Buds.
/// </summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge, exclusive.</param>
/// <param name="Bottom">Bottom edge, exclusive.</param>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);
