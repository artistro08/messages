using GMessages.App.Windowing;
using Windows.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// A button that shows the hand cursor while the pointer is over it, as a link does: the link preview card, a reply's
/// quote and the chat's scroll-to-bottom arrow.
/// </summary>
/// <remarks>
/// System XAML has no cursor property; the hand is set through <see cref="PointerCursor"/>. Otherwise a plain
/// <see cref="Button"/>, with Button's own styles.
/// </remarks>
public sealed partial class LinkCardButton : Button
{
    /// <summary>
    /// Creates the button, with the hand cursor.
    /// </summary>
    public LinkCardButton()
    {
        PointerCursor.Use(this, PointerCursor.Hand);
    }
}
