namespace GMessages.Presentation.Main;

/// <summary>
/// Gives the scroll rules of the message list: when older messages start to load, and when the scroll-to-bottom button
/// shows.
/// </summary>
/// <remarks>
/// All values come from the scroller of the list, in pixels: the offset, the scrollable height and the viewport height.
/// </remarks>
public static class ChatScroll
{
    /// <summary>
    /// Tells whether to load the older page: the list is too short to scroll, or within half a screen of its top.
    /// </summary>
    /// <remarks>Thus the older page loads before the scroll gets to the top.</remarks>
    /// <param name="offset">The scroll offset of the list from its top.</param>
    /// <param name="scrollable">How far down the list can scroll.</param>
    /// <param name="viewport">The visible height of the list.</param>
    /// <returns>True to load the older page.</returns>
    public static bool ShouldLoadOlder(double offset, double scrollable, double viewport) =>
        scrollable <= 0 || offset < viewport / 2;

    /// <summary>
    /// Tells whether the scroll-to-bottom button shows: the newest messages are more than 1 screen below.
    /// </summary>
    /// <param name="offset">The scroll offset of the list from its top.</param>
    /// <param name="scrollable">How far down the list can scroll.</param>
    /// <param name="viewport">The visible height of the list.</param>
    /// <returns>True to show the button.</returns>
    public static bool ShowScrollToBottom(double offset, double scrollable, double viewport) =>
        viewport > 0 && scrollable - offset > viewport;

    /// <summary>
    /// Tells whether the list stays pinned to its newest message after its view changed.
    /// </summary>
    /// <remarks>
    /// A chat opens pinned. While the content still changes size, the pin holds, and the view puts the list back at the
    /// bottom. The size changes when the bubbles measure their real heights, or when a page arrives. Else the person
    /// moved the list, and it stays pinned only when they left it at the bottom.
    /// </remarks>
    /// <param name="pinned">True when the list was pinned.</param>
    /// <param name="contentChanged">True when the content's height changed since the last check.</param>
    /// <param name="offset">The scroll offset of the list from its top.</param>
    /// <param name="scrollable">How far down the list can scroll.</param>
    /// <returns>True when it stays pinned.</returns>
    public static bool StaysPinned(bool pinned, bool contentChanged, double offset, double scrollable) =>
        contentChanged ? pinned : IsAtBottom(offset, scrollable);

    /// <summary>
    /// Tells whether the list is at its newest message, within 1 pixel for layout rounding.
    /// </summary>
    /// <param name="offset">The scroll offset of the list from its top.</param>
    /// <param name="scrollable">How far down the list can scroll.</param>
    /// <returns>True at the bottom.</returns>
    public static bool IsAtBottom(double offset, double scrollable) => scrollable - offset <= 1;
}
