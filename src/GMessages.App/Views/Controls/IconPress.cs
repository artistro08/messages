using System.Numerics;
using Windows.UI.Composition;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Hosting;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Gives the icon buttons the press feedback that the owner asked for. A press shrinks the icon with an ease. A release
/// springs it a little larger, then returns it to its normal size.
/// </summary>
/// <remarks>
/// <para>
/// The setter of <c>IconButtonStyle</c> for <see cref="IsEnabledProperty"/> turns it on once, so each icon button gets
/// it. It watches <see cref="ButtonBase.IsPressed"/> of the button. A pointer, touch, a pen, Space and Enter all set
/// that state. It then runs a Composition scale animation on the content presenter of the template, centered on its
/// middle. That style keeps the background and border of the presenter clear, so only the icon looks smaller. The
/// animation does not scale the focus rectangle.
/// </para>
/// <para>
/// The icon buttons have no hover fill. Back and ☰ in the title bar have their own style, so they keep the default
/// feedback.
/// </para>
/// <para>
/// The animation runs only while the Windows animation effects are on, as <see cref="UISettings.AnimationsEnabled"/>
/// tells. With the effects off, the icon keeps its size.
/// </para>
/// </remarks>
public static class IconPress
{
    private const float PressedScale  = 0.9f;
    private const float ReleasedScale = 1.05f;

    private static readonly TimeSpan PressDuration   = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReleaseDuration = TimeSpan.FromMilliseconds(300);
    private static readonly UISettings Ui            = new();

    /// <summary>
    /// Identifies the <c>IsEnabled</c> attached property.
    /// </summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(IconPress),
        new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>
    /// Gets whether a button runs the press animation.
    /// </summary>
    /// <param name="element">The button.</param>
    /// <returns>True when it animates.</returns>
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>
    /// Sets whether a button runs the press animation.
    /// </summary>
    /// <remarks>
    /// A true value starts a watch on the pressed state of the button. The class never stops the watch, because the
    /// style sets the value once.
    /// </remarks>
    /// <param name="element">The button.</param>
    /// <param name="value">True to animate.</param>
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        d.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, OnIsPressedChanged);
    }

    // A press shrinks the icon. A release springs it a little larger, then returns it to its normal size, with an ease.
    // The content presenter is the root of the Fluent Button template. The code finds it by name, as Native AOT
    // requires.
    private static void OnIsPressedChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (!Ui.AnimationsEnabled || UiHelpers.FindPart<UIElement>(sender, "ContentPresenter") is not { } content)
        {
            return;
        }

        bool pressed                   = (bool)sender.GetValue(ButtonBase.IsPressedProperty);
        Visual visual                  = ElementCompositionPreview.GetElementVisual(content);
        Compositor compositor          = visual.Compositor;
        CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1));
        visual.CenterPoint             = new Vector3(content.ActualSize / 2, 0);

        Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
        if (pressed)
        {
            scale.InsertKeyFrame(1, new Vector3(PressedScale, PressedScale, 1), ease);
            scale.Duration = PressDuration;
        }
        else
        {
            scale.InsertKeyFrame(0.4f, new Vector3(ReleasedScale, ReleasedScale, 1), ease);
            scale.InsertKeyFrame(1, Vector3.One, ease);
            scale.Duration = ReleaseDuration;
        }

        visual.StartAnimation(nameof(Visual.Scale), scale);
    }
}
