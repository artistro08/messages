using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using WinRT;

namespace GMessages.App.Views;

/// <summary>
/// Dims the main window's text while the window is inactive, the way the title bar's own text dims, and brings it back
/// when the window is active again.
/// </summary>
/// <remarks>
/// <para>
/// Every text in the window (the chat list, the chat header, the bubbles, the message box) draws with Fluent's shared
/// text brushes, so their opacity is eased (150 ms, eased out) rather than each element's: photos, videos and avatars
/// keep their look. Bubble text on the accent fill dims to its own shade the same way. With Windows' animation effects
/// off the change is instant.
/// </para>
/// <para>
/// The brushes are the theme's own, shared by the app's other window (the tray menu), which keeps its text bright with
/// brushes of its own (see <c>TrayMenuWindow</c>). <see cref="Restore"/> puts them back when the main window closes. High
/// contrast draws with other brushes, which stay as they are.
/// </para>
/// </remarks>
internal sealed class WindowTextDimmer
{
    // Inactive Text Is About As Faint As The Title Bar's
    private const double DimmedOpacity = 0.5;

    // The Shared Text Brushes Every Text Draws With
    private static readonly string[] TextKeys =
    [
        "TextFillColorPrimaryBrush",
        "TextFillColorSecondaryBrush",
        "TextFillColorTertiaryBrush",
        "TextOnAccentFillColorPrimaryBrush",
        "TextOnAccentFillColorSecondaryBrush",
    ];

    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150);
    private static readonly UISettings Ui         = new();

    private readonly FrameworkElement _root;

    // The Brushes Of The Window's Theme, Where Their Opacity Is And Where It Goes, And The Running Fade
    private List<SolidColorBrush> _brushes = [];
    private ElementTheme _theme;
    private double _now  = 1;
    private double _from = 1;
    private double _to   = 1;
    private DateTimeOffset _fadeStart;
    private bool _fading;

    /// <summary>
    /// Creates the dimmer for a window's root element.
    /// </summary>
    /// <param name="root">The window's root; its theme picks the brushes.</param>
    public WindowTextDimmer(FrameworkElement root)
    {
        _root = root;
        _root.ActualThemeChanged += (_, _) =>
        {
            // A New Theme Has Its Own Brushes: The Old Ones Go Back, The New Ones Take The Current Look
            foreach (SolidColorBrush brush in _brushes)
            {
                brush.Opacity = 1;
            }

            _brushes = [];
            SetOpacity(_now);
        };
    }

    /// <summary>
    /// Shows the window's text bright (active) or dimmed (inactive).
    /// </summary>
    /// <param name="active">True while the window is active.</param>
    public void Show(bool active)
    {
        double target = active ? 1 : DimmedOpacity;
        if (target == _to && (_fading || _now == target))
        {
            return;
        }

        _to = target;
        if (!Ui.AnimationsEnabled)
        {
            StopFade();
            SetOpacity(target);
            return;
        }

        _from      = _now;
        _fadeStart = DateTimeOffset.UtcNow;
        if (!_fading)
        {
            _fading                      = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    /// <summary>
    /// Puts the brushes back to full: the window is closing and the tray menu may still draw with them.
    /// </summary>
    public void Restore()
    {
        StopFade();
        _to = 1;
        SetOpacity(1);
    }

    // Each Frame Of The Fade: Eased Out From Where It Was To Where It Goes
    private void OnRendering(object? sender, object e)
    {
        double t = Math.Clamp((DateTimeOffset.UtcNow - _fadeStart) / FadeDuration, 0, 1);
        double k = 1 - Math.Pow(1 - t, 3);
        SetOpacity(_from + ((_to - _from) * k));
        if (t >= 1)
        {
            StopFade();
        }
    }

    private void StopFade()
    {
        if (_fading)
        {
            CompositionTarget.Rendering -= OnRendering;
            _fading                      = false;
        }
    }

    private void SetOpacity(double opacity)
    {
        _now = opacity;
        foreach (SolidColorBrush brush in Brushes())
        {
            brush.Opacity = opacity;
        }
    }

    /// <summary>
    /// Finds one of the theme's shared brushes, as an element of that theme draws with it.
    /// </summary>
    /// <remarks>
    /// Looked up in the controls' theme dictionaries (Dark is "Default"), else the app's own lookup (the app's theme). Read
    /// with WinRT casts, never type tests (Native AOT).
    /// </remarks>
    /// <param name="theme">The element's theme.</param>
    /// <param name="key">The brush's resource key, such as <c>TextFillColorPrimaryBrush</c>.</param>
    /// <returns>The brush, or null when there is none (or it isn't a solid brush).</returns>
    internal static SolidColorBrush? FindBrush(ElementTheme theme, string key)
    {
        string[] names = theme == ElementTheme.Light ? ["Light"] : ["Default", "Dark"];
        foreach (ResourceDictionary merged in Application.Current.Resources.MergedDictionaries)
        {
            foreach (string name in names)
            {
                if (merged.ThemeDictionaries.TryGetValue(name, out object? found) && found is not null
                    && found.As<ResourceDictionary>().TryGetValue(key, out object? themed) && themed is not null)
                {
                    return SolidOf(themed);
                }
            }
        }

        return Application.Current.Resources.TryGetValue(key, out object? brush) && brush is not null ? SolidOf(brush) : null;
    }

    private static SolidColorBrush? SolidOf(object brush)
    {
        try
        {
            return brush.As<SolidColorBrush>();
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    // The Window Theme's Shared Text Brushes, Found Once Per Theme
    private List<SolidColorBrush> Brushes()
    {
        ElementTheme theme = _root.ActualTheme;
        if (_brushes.Count > 0 && theme == _theme)
        {
            return _brushes;
        }

        _theme   = theme;
        _brushes = [];
        foreach (string key in TextKeys)
        {
            if (FindBrush(theme, key) is { } brush)
            {
                _brushes.Add(brush);
            }
        }

        return _brushes;
    }
}
