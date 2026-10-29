using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GMessages.App.Views.Controls;

/// <summary>
/// A round avatar: a photo, or a colored circle with an initial, or two stacked circles for a group.
/// </summary>
/// <remarks>
/// A nonzero <see cref="SecondColor"/> draws the group look: a smaller back circle at the top right and the front
/// circle shrunk to the bottom left. <see cref="Image"/>, when set, covers the front circle's initial.
/// </remarks>
public sealed partial class Avatar : UserControl
{
    /// <summary>Identifies the <see cref="Initial"/> property.</summary>
    public static readonly DependencyProperty InitialProperty =
        DependencyProperty.Register(nameof(Initial), typeof(string), typeof(Avatar), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Color"/> property.</summary>
    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(nameof(Color), typeof(uint), typeof(Avatar), new PropertyMetadata(0u));

    /// <summary>Identifies the <see cref="Image"/> property.</summary>
    public static readonly DependencyProperty ImageProperty =
        DependencyProperty.Register(nameof(Image), typeof(ImageSource), typeof(Avatar), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="SecondInitial"/> property.</summary>
    public static readonly DependencyProperty SecondInitialProperty =
        DependencyProperty.Register(nameof(SecondInitial), typeof(string), typeof(Avatar), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="SecondColor"/> property.</summary>
    public static readonly DependencyProperty SecondColorProperty =
        DependencyProperty.Register(nameof(SecondColor), typeof(uint), typeof(Avatar), new PropertyMetadata(0u));

    // Circle Sizes: One Avatar Fills The 40 px Square; A Group's Two Circles Overlap Inside It
    private const double FullSize  = 40;
    private const double GroupSize = 28;

    /// <summary>
    /// Creates the avatar.
    /// </summary>
    public Avatar() => InitializeComponent();

    /// <summary>Gets or sets the front circle's letter.</summary>
    public string Initial
    {
        get => (string)GetValue(InitialProperty);
        set => SetValue(InitialProperty, value);
    }

    /// <summary>Gets or sets the front circle's color as opaque ARGB.</summary>
    public uint Color
    {
        get => (uint)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>Gets or sets the photo that covers the front circle, or null for the initial.</summary>
    public ImageSource? Image
    {
        get => (ImageSource?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    /// <summary>Gets or sets the back circle's letter (groups only).</summary>
    public string SecondInitial
    {
        get => (string)GetValue(SecondInitialProperty);
        set => SetValue(SecondInitialProperty, value);
    }

    /// <summary>Gets or sets the back circle's color as opaque ARGB; 0 means no back circle.</summary>
    public uint SecondColor
    {
        get => (uint)GetValue(SecondColorProperty);
        set => SetValue(SecondColorProperty, value);
    }

    /// <summary>
    /// Shows the back circle only when it has a color.
    /// </summary>
    /// <param name="color">The back circle's color.</param>
    /// <returns>Visible for a nonzero color.</returns>
    public static Visibility ShownIfColor(uint color) => color != 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Shows the photo only when there is one.
    /// </summary>
    /// <param name="image">The photo, or null.</param>
    /// <returns>Visible when set.</returns>
    public static Visibility ShownIfImage(ImageSource? image) => image is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gives the front circle's size: full for one avatar, smaller when a back circle shares the square.
    /// </summary>
    /// <param name="secondColor">The back circle's color.</param>
    /// <returns>The size in effective pixels.</returns>
    public static double FrontSize(uint secondColor) => secondColor != 0 ? GroupSize : FullSize;

    /// <summary>
    /// Gives the front initial's font size to match the circle.
    /// </summary>
    /// <param name="secondColor">The back circle's color.</param>
    /// <returns>The font size.</returns>
    public static double InitialSize(uint secondColor) => secondColor != 0 ? 12 : 16;
}
