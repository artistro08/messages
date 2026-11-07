namespace GMessages.Presentation.Main;

/// <summary>
/// Gives the scroll edge fades of the message and conversation lists. An edge fades while more content is past it.
/// </summary>
/// <remarks>
/// The values come from the scroller of the list, in pixels. The rules ignore fractions of a pixel, because a scaled
/// display can stop a scroller just short of an end.
/// </remarks>
public static class ScrollEdges
{
    /// <summary>Each fade has this height, in pixels.</summary>
    public const double FadeHeight = 28;

    // A position closer than this to an end counts as at the end.
    private const double Slack = 1;

    /// <summary>
    /// Tells which edges fade: the top while the list can scroll up, the bottom while it can scroll down.
    /// </summary>
    /// <param name="offset">The scroll offset of the list from its top.</param>
    /// <param name="scrollable">How far down the list can scroll, or 0 when it cannot scroll.</param>
    /// <returns>Whether the top and the bottom edges fade.</returns>
    public static (bool Top, bool Bottom) Fades(double offset, double scrollable) =>
        (offset >= Slack, scrollable - offset >= Slack);
}
