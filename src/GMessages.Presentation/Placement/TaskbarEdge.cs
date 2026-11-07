namespace GMessages.Presentation.Placement;

/// <summary>
/// Specifies the screen edge of the taskbar. Ported from <c>MagicBuds.Presentation.Placement</c> of Magic Buds.
/// </summary>
public enum TaskbarEdge
{
    /// <summary>
    /// The bottom edge, which is the Windows default. An auto-hidden taskbar also uses this value.
    /// </summary>
    Bottom,

    /// <summary>
    /// The top edge.
    /// </summary>
    Top,

    /// <summary>
    /// The left edge.
    /// </summary>
    Left,

    /// <summary>
    /// The right edge.
    /// </summary>
    Right,
}

/// <summary>
/// Holds a screen rectangle in physical pixels. The right and bottom edges are exclusive. Ported from Magic Buds.
/// </summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Right">The right edge, exclusive.</param>
/// <param name="Bottom">The bottom edge, exclusive.</param>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);
