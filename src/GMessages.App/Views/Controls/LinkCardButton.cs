using GMessages.App.Windowing;
using Windows.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows the hand cursor over a button, as a link does. The link preview card, the quote of a reply and the
/// scroll-to-bottom arrow of the chat use it.
/// </summary>
/// <remarks>
/// System XAML has no cursor property, so <see cref="PointerCursor"/> sets the hand. In all other ways, this control is
/// a plain <see cref="Button"/> with the Button styles.
/// </remarks>
public sealed partial class LinkCardButton : Button
{
    /// <summary>
    /// Creates the button with the hand cursor.
    /// </summary>
    public LinkCardButton()
    {
        PointerCursor.Use(this, PointerCursor.Hand);
    }
}
