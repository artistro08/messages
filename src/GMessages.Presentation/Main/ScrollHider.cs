namespace GMessages.Presentation.Main;

/// <summary>
/// Decides when a floating button (Start chat over the list) hides while the list scrolls: scrolling down hides it,
/// scrolling up or reaching the top shows it.
/// </summary>
/// <remarks>
/// A change of direction only counts once the list moved <c>threshold</c> pixels from the furthest point reached in
/// the current direction, so small back-and-forth nudges never flip it: a touchpad's jitter, or the list correcting its
/// scroll position while it measures rows mid-scroll. The top always shows it.
/// </remarks>
/// <param name="threshold">How far, in pixels, the list must move against the current direction to flip it.</param>
public sealed class ScrollHider(double threshold)
{
    // The Furthest Point Reached In The Current Direction: The Lowest While Hidden, The Highest While Shown
    private double _mark;

    /// <summary>Gets whether the button shows.</summary>
    public bool IsShown { get; private set; } = true;

    /// <summary>
    /// Takes the list's new scroll position.
    /// </summary>
    /// <param name="offset">How far down the list is scrolled, in pixels.</param>
    /// <returns>True when <see cref="IsShown"/> changed.</returns>
    public bool Scrolled(double offset)
    {
        bool show = IsShown switch
        {
            _ when offset <= 0 => true,
            true               => offset - _mark < threshold,
            false              => _mark - offset >= threshold,
        };

        // A Flip Starts Measuring From Here; Otherwise The Mark Follows The Current Direction
        bool changed = show != IsShown;
        IsShown      = show;
        _mark        = changed ? offset : IsShown ? Math.Min(_mark, offset) : Math.Max(_mark, offset);

        return changed;
    }
}
