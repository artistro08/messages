using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace GMessages.App.Views;

/// <summary>
/// Dims the main window's content while the window is inactive, the way the title bar's own text dims, and brings it
/// back when the window is active again.
/// </summary>
/// <remarks>
/// <para>
/// The layers below the title bar (the body and the settings page) fade to <see cref="DimmedOpacity"/> over 150 ms
/// (eased out), with a Composition opacity animation on each layer's visual; with Windows' animation effects off the
/// change is instant. The title bar dims itself, and the photo viewer's toolbar dims itself (its own states).
/// </para>
/// <para>
/// The layers, not the text brushes: Fluent's theme brushes are shared and refuse changes (E_ACCESSDENIED on
/// <c>Brush.Opacity</c>, seen in the UI tests), and a brush defined lower in the tree doesn't reach text whose look was
/// resolved when its own control loaded. So photos and avatars dim with the text. The layers have no implicit XAML
/// transitions, so Windows hands out their visuals (see the 0.6.2 crash note in the implementer rules).
/// </para>
/// </remarks>
internal sealed class WindowTextDimmer
{
    // Inactive Content Is About As Faint As The Title Bar's Text
    private const float DimmedOpacity = 0.6f;

    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150);
    private static readonly UISettings Ui         = new();

    private readonly UIElement[] _layers;
    private bool _dimmed;

    /// <summary>
    /// Creates the dimmer for the window's content layers.
    /// </summary>
    /// <param name="layers">The layers that dim together.</param>
    public WindowTextDimmer(params UIElement[] layers) => _layers = layers;

    /// <summary>
    /// Shows the window's content bright (active) or dimmed (inactive).
    /// </summary>
    /// <param name="active">True while the window is active.</param>
    public void Show(bool active)
    {
        if (_dimmed == !active)
        {
            return;
        }

        _dimmed      = !active;
        float target = active ? 1f : DimmedOpacity;
        foreach (UIElement layer in _layers)
        {
            // Guarded: A Layer Windows Refuses The Visual Of (E_ACCESSDENIED) Just Stays Bright
            Visual visual;
            try
            {
                visual = ElementCompositionPreview.GetElementVisual(layer);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                continue;
            }

            visual.StopAnimation(nameof(Visual.Opacity));
            if (!Ui.AnimationsEnabled)
            {
                visual.Opacity = target;
                continue;
            }

            Compositor compositor          = visual.Compositor;
            ScalarKeyFrameAnimation fade   = compositor.CreateScalarKeyFrameAnimation();
            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));
            fade.InsertKeyFrame(1, target, ease);
            fade.Duration = FadeDuration;
            visual.StartAnimation(nameof(Visual.Opacity), fade);
        }

        TestHooks.NoteState("window-dim", _dimmed ? "Dimmed" : "Bright");
    }
}
