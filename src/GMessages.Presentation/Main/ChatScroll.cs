namespace GMessages.Presentation.Main;

/// <summary>
/// The message list's scroll rules: when older messages start loading, and when the scroll-to-bottom button shows.
/// </summary>
/// <remarks>All values are the list scroller's, in pixels: how far down it is, how far it can go, and its height.</remarks>
public static class ChatScroll
{
    /// <summary>
    /// Says whether to load the older page: the list is too short to scroll, or is within half a screen of its top, so
    /// the older page is on its way before the top is reached.
    /// </summary>
    /// <param name="offset">How far down the list is scrolled.</param>
    /// <param name="scrollable">How far down the list can scroll.</param>
    /// <param name="viewport">The list's visible height.</param>
    /// <returns>True to load the older page.</returns>
    public static bool ShouldLoadOlder(double offset, double scrollable, double viewport) =>
        scrollable <= 0 || offset < viewport / 2;

    /// <summary>
    /// Says whether the scroll-to-bottom button shows: the newest messages are more than a screen below.
    /// </summary>
    /// <param name="offset">How far down the list is scrolled.</param>
    /// <param name="scrollable">How far down the list can scroll.</param>
    /// <param name="viewport">The list's visible height.</param>
    /// <returns>True to show the button.</returns>
    public static bool ShowScrollToBottom(double offset, double scrollable, double viewport) =>
        viewport > 0 && scrollable - offset > viewport;
}
