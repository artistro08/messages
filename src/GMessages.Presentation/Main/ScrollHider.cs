namespace GMessages.Presentation.Main;

/// <summary>
/// Decides when a floating button, such as Start chat over the list, hides during a scroll. A scroll down hides it, and
/// a scroll up or the top shows it.
/// </summary>
/// <remarks>
/// A change of direction counts only after the list moves <c>threshold</c> pixels from the farthest point in the
/// current direction. Thus small moves back and forth never flip it. Such moves come from the jitter of a touchpad, or
/// from the list as it corrects its position for measured rows. The top always shows the button.
/// </remarks>
/// <param name="threshold">How far, in pixels, the list must move against the current direction to flip it.</param>
public sealed class ScrollHider(double threshold)
{
    // The mark is the farthest point in the current direction: the lowest while hidden, the highest while shown.
    private double _mark;

    /// <summary>Gets whether the button shows.</summary>
    public bool IsShown { get; private set; } = true;

    /// <summary>
    /// Takes the new scroll position of the list.
    /// </summary>
    /// <param name="offset">The scroll offset of the list from its top, in pixels.</param>
    /// <returns>True when <see cref="IsShown"/> changed.</returns>
    public bool Scrolled(double offset)
    {
        bool show = IsShown switch
        {
            _ when offset <= 0 => true,
            true               => offset - _mark < threshold,
            false              => _mark - offset >= threshold,
        };

        // A flip starts the measure from here. Else the mark follows the current direction.
        bool changed = show != IsShown;
        IsShown      = show;
        _mark        = changed ? offset : IsShown ? Math.Min(_mark, offset) : Math.Max(_mark, offset);

        return changed;
    }
}
