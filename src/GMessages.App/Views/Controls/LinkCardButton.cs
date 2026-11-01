using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// A button that shows the hand cursor while the pointer is over it, as a link does: the link preview card, a reply's
/// quote and the chat's scroll-to-bottom arrow.
/// </summary>
/// <remarks>
/// WinUI 3 has no public cursor property; <c>ProtectedCursor</c> is reachable only from a subclass. Otherwise a plain
/// <see cref="Button"/>, with Button's own styles.
/// </remarks>
public sealed partial class LinkCardButton : Button
{
    /// <summary>
    /// Creates the button, with the hand cursor.
    /// </summary>
    public LinkCardButton()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}
