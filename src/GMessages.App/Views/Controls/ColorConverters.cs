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
    /// <summary>
    /// Makes a solid brush from an opaque ARGB color.
    /// </summary>
    /// <param name="argb">The color as <c>0xAARRGGBB</c>.</param>
    /// <returns>The brush.</returns>
    public static SolidColorBrush ToBrush(uint argb) =>
        new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    /// <summary>
    /// Gives bold for unread rows and normal for the rest.
    /// </summary>
    /// <param name="bold">True for bold.</param>
    /// <returns>The font weight.</returns>
    public static FontWeight Weight(bool bold) => bold ? FontWeights.Bold : FontWeights.Normal;
}
