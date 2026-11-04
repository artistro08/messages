using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace GMessages.App.Views.Controls;

/// <summary>
/// <c>x:Bind</c> function helpers that turn view model values into brushes and font weights.
/// </summary>
/// <remarks>
/// Plain static methods the XAML compiler calls directly, so Native AOT needs no <c>IValueConverter</c> reflection.
/// </remarks>
public static class ColorConverters
{
    // Brushes Made So Far, By Color: Every Avatar And Sender Name Of One Color Shares One (Only The UI Thread Calls)
    private static readonly Dictionary<uint, SolidColorBrush> Brushes = [];

    /// <summary>
    /// Gives a solid brush for an opaque ARGB color, the same one each time for a color.
    /// </summary>
    /// <remarks>Shared, so callers never change it.</remarks>
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
    /// Italic or normal text, for <c>x:Bind</c>.
    /// </summary>
    /// <param name="italic">True for italic.</param>
    /// <returns>The font style.</returns>
    public static FontStyle Style(bool italic) => italic ? FontStyle.Italic : FontStyle.Normal;
}
