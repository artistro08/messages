using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace GMessages.App.Views.Controls;

/// <summary>
/// The icon buttons' press feedback (owner): pressing shrinks the icon, releasing springs it slightly larger, then it
/// settles back to its normal size.
/// </summary>
/// <remarks>
/// <para>
/// Turned on once, by <c>IconButtonStyle</c>'s setter for <see cref="IsEnabledProperty"/>, so every icon button gets it.
/// It watches the button's <see cref="ButtonBase.IsPressed"/> (pointer, touch, pen and Space or Enter alike) and runs a
/// Composition scale animation on the template's content presenter, centered on its middle. The presenter's own
/// background and border are clear in that style, so only the icon looks smaller; the focus rectangle isn't scaled.
/// </para>
/// <para>
/// Runs only while Windows' animation effects are on (<see cref="UISettings.AnimationsEnabled"/>); with them off the
/// icon keeps its size.
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
    /// Sets whether a button runs the press animation. Turning it on starts watching the button's pressed state; it
    /// isn't turned off again (the style sets it once).
    /// </summary>
    /// <param name="element">The button.</param>
    /// <param name="value">True to animate.</param>
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    // Watch The Pressed State Once The Style Turns It On
    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        d.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, OnIsPressedChanged);
    }

    // Shrink On Press; On Release Spring Slightly Larger, Then Settle At Normal Size, Eased. The Content Presenter Is The
    // Template's Root (Fluent's Button Template), Found By Name As AOT Requires
    private static void OnIsPressedChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (!Ui.AnimationsEnabled || ChatPane.FindPart<UIElement>(sender, "ContentPresenter") is not { } content)
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
