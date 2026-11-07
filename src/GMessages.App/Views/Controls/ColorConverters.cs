using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml.Media;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Gives <c>x:Bind</c> functions that turn view model values into brushes and font styles.
/// </summary>
/// <remarks>
/// The XAML compiler calls these plain static methods directly. Thus Native AOT needs no <c>IValueConverter</c>
/// reflection.
/// </remarks>
public static class ColorConverters
{
    // Holds the brushes by color. All avatars and sender names of a color share 1 brush. Only the UI thread calls.
    private static readonly Dictionary<uint, SolidColorBrush> Brushes = [];

    /// <summary>
    /// Gives a solid brush for an opaque ARGB color. Each call for a color gives the same brush.
    /// </summary>
    /// <remarks>Callers share the brush, so they must never change it.</remarks>
    /// <param name="argb">The color as <c>0xAARRGGBB</c>.</param>
    /// <returns>The brush.</returns>
    public static SolidColorBrush ToBrush(uint argb)
    {
        if (!Brushes.TryGetValue(argb, out SolidColorBrush? brush))
        {
            brush         = new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            Brushes[argb] = brush;
        }

        return brush;
    }

    /// <summary>
    /// Gives bold for unread rows and normal for the rest.
    /// </summary>
    /// <param name="bold">True for bold.</param>
    /// <returns>The font weight.</returns>
    public static FontWeight Weight(bool bold) => bold ? FontWeights.Bold : FontWeights.Normal;

    /// <summary>
    /// Gives italic for drafts and normal for the rest.
    /// </summary>
    /// <param name="italic">True for italic.</param>
    /// <returns>The font style.</returns>
    public static FontStyle Style(bool italic) => italic ? FontStyle.Italic : FontStyle.Normal;
}
