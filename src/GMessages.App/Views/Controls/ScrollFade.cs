using System.Numerics;
using GMessages.Presentation.Main;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
using WinRT;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Fades the content of a scroller toward its top and bottom edges while more content lies past them.
/// <see cref="ScrollEdges"/> picks the edges.
/// </summary>
/// <remarks>
/// <para>
/// The fade is a real alpha fade. Thus the Mica or translucent layer of the window shows through, and no color covers
/// the list. XAML has no opacity mask, so Composition draws the content, as the <c>OpacityMaskView</c> of the Windows
/// Community Toolkit does. A visual surface copies the content presenter of the scroller. A sprite paints that copy
/// through a mask brush. The linear gradient of the mask is clear at a fading edge. The class hides the visual of the
/// presenter. Its XAML stays, so clicks, focus and UI Automation still reach the rows.
/// </para>
/// <para>
/// The sprite goes on the empty layer of the scroller template between its content and its scroll bars. Thus the
/// scroll bar stays above the sprite and does not fade. The fade does not touch elements outside the scroller,
/// such as floating buttons and rings. Each fade animates the alpha of its gradient stop, so nothing moves and the
/// layout does not change.
/// </para>
/// </remarks>
internal sealed class ScrollFade
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(167);
    private static readonly Color Shown = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color Clear = Color.FromArgb(0, 255, 255, 255);

    private readonly ScrollViewer _scroller;
    private readonly FrameworkElement _content;
    private readonly UIElement _layer;
    private readonly Compositor _compositor;
    private readonly CompositionVisualSurface _surface;
    private readonly SpriteVisual _sprite;

    // The gradient has 4 stops from top to bottom. _topEdge and _bottomEdge are at the edges. _topInner ends the top
    // fade, and _bottomInner starts the bottom fade.
    private readonly CompositionColorGradientStop _topEdge;
    private readonly CompositionColorGradientStop _topInner;
    private readonly CompositionColorGradientStop _bottomInner;
    private readonly CompositionColorGradientStop _bottomEdge;

    // _bottomAlways makes the bottom fade at all scroll positions. _bottomFade is the height of the bottom fade.
    private readonly bool _bottomAlways;
    private double _bottomFade = ScrollEdges.FadeHeight;

    private bool _topFaded;
    private bool _bottomFaded;

    private ScrollFade(ScrollViewer scroller, FrameworkElement content, UIElement layer, bool bottomAlways)
    {
        _scroller     = scroller;
        _content      = content;
        _layer        = layer;
        _bottomAlways = bottomAlways;

        Visual contentVisual  = ElementCompositionPreview.GetElementVisual(content);
        _compositor           = contentVisual.Compositor;
        _surface              = _compositor.CreateVisualSurface();
        _surface.SourceVisual = contentVisual;

        // The mask is opaque at all points until an edge fades.
        CompositionLinearGradientBrush gradient = _compositor.CreateLinearGradientBrush();
        gradient.StartPoint = Vector2.Zero;
        gradient.EndPoint   = new Vector2(0, 1);
        _topEdge            = Stop(0);
        _topInner           = Stop(0);
        _bottomInner        = Stop(1);
        _bottomEdge         = Stop(1);
        gradient.ColorStops.Add(_topEdge);
        gradient.ColorStops.Add(_topInner);
        gradient.ColorStops.Add(_bottomInner);
        gradient.ColorStops.Add(_bottomEdge);

        CompositionMaskBrush mask = _compositor.CreateMaskBrush();
        mask.Source = _compositor.CreateSurfaceBrush(_surface);
        mask.Mask   = gradient;

        // The sprite shows the copy in place of the content.
        _sprite       = _compositor.CreateSpriteVisual();
        _sprite.Brush = mask;
        ElementCompositionPreview.SetElementChildVisual(layer, _sprite);
        contentVisual.Opacity = 0;

        Resize();

        // A scroll, a resize and a change of the content size update the fade.
        _scroller.ViewChanged += (_, _) => Apply(true);
        _content.SizeChanged  += (_, _) => Resize();
        _scroller.RegisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, (_, _) => Apply(true));

        CompositionColorGradientStop Stop(float offset) => _compositor.CreateColorGradientStop(offset, Shown);
    }

    /// <summary>
    /// Starts the fade on the edges of a scroller.
    /// </summary>
    /// <remarks>Call this method once for each scroller, after WinUI applies the template of the scroller.</remarks>
    /// <param name="scroller">The scroller of the list.</param>
    /// <param name="bottomAlways">True to fade the bottom edge at all scroll positions, such as behind an element that
    /// floats over it.</param>
    /// <returns>The fade, or null when the scroller has no content presenter yet.</returns>
    public static ScrollFade? Attach(ScrollViewer scroller, bool bottomAlways = false)
    {
        if (UiHelpers.FindPart<ScrollContentPresenter>(scroller, "ScrollContentPresenter") is not { } content
            || VisualTreeHelper.GetParent(content) is not { } parentObject)
        {
            return null;
        }

        // The parent is the unnamed grid of the template that holds the content. The code uses a WinRT cast, not a type
        // test. The Native AOT build returns the grid as a base type, as UiHelpers.FindScroller tells.
        Panel parent = parentObject.As<Panel>();

        // The layer is the empty layer of the template directly above the content and below the scroll bars. Without
        // that layer, the scroller itself is the layer.
        int at = parent.Children.IndexOf(content);
        UIElement layer = at + 1 < parent.Children.Count && VisualTreeHelper.GetChildrenCount(parent.Children[at + 1]) == 0
            ? parent.Children[at + 1]
            : scroller;

        return new ScrollFade(scroller, content, layer, bottomAlways);
    }

    /// <summary>
    /// Sets the height of the bottom fade in effective pixels. An example is the height of an element over the list
    /// bottom.
    /// </summary>
    /// <param name="height">The height. The default is the height of the standard edge fade.</param>
    public void SetBottomFade(double height)
    {
        _bottomFade = Math.Max(0, height);
        Resize();
    }

    // The copy matches the size and place of the content, and the fades keep their height.
    private void Resize()
    {
        Vector2 size = _content.ActualSize;
        Point at     = _content.TransformToVisual(_layer).TransformPoint(default);

        _surface.SourceSize = size;
        _sprite.Size        = size;
        _sprite.Offset      = new Vector3((float)at.X, (float)at.Y, 0);

        float top    = size.Y > 0 ? Math.Min(0.5f, (float)ScrollEdges.FadeHeight / size.Y) : 0;
        float bottom = size.Y > 0 ? Math.Min(0.5f, (float)_bottomFade / size.Y) : 0;
        _topInner.Offset    = top;
        _bottomInner.Offset = 1 - bottom;

        Apply(false);
    }

    // Each edge fades while more content lies past it. The bottom edge always fades when _bottomAlways is true.
    private void Apply(bool animate)
    {
        (bool top, bool bottom) = ScrollEdges.Fades(_scroller.VerticalOffset, _scroller.ScrollableHeight);
        bottom |= _bottomAlways;

        if (top != _topFaded || !animate)
        {
            _topFaded = top;
            Set(_topEdge, top, animate);
        }

        if (bottom != _bottomFaded || !animate)
        {
            _bottomFaded = bottom;
            Set(_bottomEdge, bottom, animate);
        }
    }

    private void Set(CompositionColorGradientStop stop, bool faded, bool animate)
    {
        Color color = faded ? Clear : Shown;
        if (!animate)
        {
            stop.StopAnimation(nameof(stop.Color));
            stop.Color = color;
            return;
        }

        ColorKeyFrameAnimation fade = _compositor.CreateColorKeyFrameAnimation();
        fade.InsertKeyFrame(1, color);
        fade.Duration = FadeDuration;
        stop.StartAnimation(nameof(stop.Color), fade);
    }
}
