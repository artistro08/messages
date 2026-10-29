namespace GMessages.Presentation.Main;

/// <summary>
/// The scroll edge fades of the message and conversation lists: an edge fades while there is more to scroll to past it.
/// </summary>
/// <remarks>Values are the list scroller's, in pixels. Fractions of a pixel are ignored, since scaled displays can stop a scroller just short of either end.</remarks>
public static class ScrollEdges
{
    /// <summary>The height of each fade, in pixels.</summary>
    public const double FadeHeight = 28;

    // Closer Than This To An End Counts As At It
    private const double Slack = 1;

    /// <summary>
    /// Says which edges fade: the top while the list can scroll further up, the bottom while it can scroll further down.
    /// </summary>
    /// <param name="offset">How far down the list is scrolled.</param>
    /// <param name="scrollable">How far down the list can scroll (0 when it can't scroll).</param>
    /// <returns>Whether the top and the bottom edges fade.</returns>
    public static (bool Top, bool Bottom) Fades(double offset, double scrollable) =>
        (offset >= Slack, scrollable - offset >= Slack);
}
