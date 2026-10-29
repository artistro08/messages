using System.Numerics;
using GMessages.Presentation.Main;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Fades a scroller's content out toward its top and bottom edges while there is more to scroll to past them
/// (<see cref="ScrollEdges"/> decides which).
/// </summary>
/// <remarks>
/// <para>
/// A real alpha fade, so the window's Mica or translucent layer shows through and no color is drawn over the list. XAML
/// has no opacity mask, so the content is drawn through Composition, as the Windows Community Toolkit's
/// <c>OpacityMaskView</c> does: a visual surface copies the scroller's content presenter, a sprite paints that copy
/// through a mask brush whose linear gradient is clear at a fading edge, and the presenter's own visual is hidden (its
/// XAML stays, so clicks, focus and UI Automation still reach the rows).
/// </para>
/// <para>
/// The sprite goes on the empty layer the scroller's template keeps between its content and its scroll bars, so the
/// scroll bar stays above it and unfaded. Elements outside the scroller (floating buttons, rings) aren't touched. Each
/// fade animates its gradient stop's alpha, so nothing moves and the layout doesn't change.
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

    // The Gradient, Top To Bottom: The Top Edge, Where Its Fade Ends, Where The Bottom Fade Starts, The Bottom Edge
    private readonly CompositionColorGradientStop _topEdge;
    private readonly CompositionColorGradientStop _topInner;
    private readonly CompositionColorGradientStop _bottomInner;
    private readonly CompositionColorGradientStop _bottomEdge;

    private bool _topFaded;
    private bool _bottomFaded;

    private ScrollFade(ScrollViewer scroller, FrameworkElement content, UIElement layer)
    {
        _scroller = scroller;
        _content  = content;
        _layer    = layer;

        // The Content's Copy
        Visual content_visual = ElementCompositionPreview.GetElementVisual(content);
        _compositor           = content_visual.Compositor;
        _surface              = _compositor.CreateVisualSurface();
        _surface.SourceVisual = content_visual;

        // The Mask: Opaque Everywhere Until An Edge Fades
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

        // The Copy Is Shown In Place Of The Content
        _sprite       = _compositor.CreateSpriteVisual();
        _sprite.Brush = mask;
        ElementCompositionPreview.SetElementChildVisual(layer, _sprite);
        content_visual.Opacity = 0;

        Resize();

        // Bind To Changes: Scrolling, Resizing And The Content Growing Or Shrinking
        _scroller.ViewChanged += (_, _) => Apply(true);
        _content.SizeChanged  += (_, _) => Resize();
        _scroller.RegisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, (_, _) => Apply(true));

        CompositionColorGradientStop Stop(float offset) => _compositor.CreateColorGradientStop(offset, Shown);
    }

    /// <summary>
    /// Starts fading a scroller's edges.
    /// </summary>
    /// <remarks>Call once per scroller, after its template is applied.</remarks>
    /// <param name="scroller">The list's scroller.</param>
    /// <returns>The fade, or null when the scroller has no content presenter yet.</returns>
    public static ScrollFade? Attach(ScrollViewer scroller)
    {
        if (Find<ScrollContentPresenter>(scroller) is not { } content || VisualTreeHelper.GetParent(content) is not Panel parent)
        {
            return null;
        }

        // The Template's Empty Layer Right Above The Content (Below The Scroll Bars), Else The Scroller Itself
        int at = parent.Children.IndexOf(content);
        UIElement layer = at + 1 < parent.Children.Count && parent.Children[at + 1] is Panel { Children.Count: 0 } empty ? empty : scroller;

        return new ScrollFade(scroller, content, layer);
    }

    // The Copy Matches The Content's Size And Place, And The Fades Keep Their Height
    private void Resize()
    {
        Vector2 size = _content.ActualSize;
        Point at     = _content.TransformToVisual(_layer).TransformPoint(default);

        _surface.SourceSize = size;
        _sprite.Size        = size;
        _sprite.Offset      = new Vector3((float)at.X, (float)at.Y, 0);

        float fade = size.Y > 0 ? Math.Min(0.5f, (float)ScrollEdges.FadeHeight / size.Y) : 0;
        _topInner.Offset    = fade;
        _bottomInner.Offset = 1 - fade;

        Apply(false);
    }

    // Each Edge Fades While There Is More To Scroll To Past It
    private void Apply(bool animate)
    {
        (bool top, bool bottom) = ScrollEdges.Fades(_scroller.VerticalOffset, _scroller.ScrollableHeight);

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

    private static T? Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if ((child as T ?? Find<T>(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
