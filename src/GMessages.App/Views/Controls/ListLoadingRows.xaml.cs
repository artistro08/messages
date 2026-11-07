using GMessages.Presentation.Messaging;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Shapes;
using WinRT;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows placeholder rows at the end of the conversation list while the next page loads.
/// </summary>
/// <remarks>
/// The band sweeps only while the rows show and Windows animation effects are on. Otherwise the rows stay still.
/// </remarks>
public sealed partial class ListLoadingRows : UserControl
{
    private const double SweepBand = 120;

    // The bars take random widths from these 50 widths, 60 px to 231.5 px. The widest bar fits beside the avatar in
    // the narrowest list.
    private static readonly double[] BarWidths = [.. Enumerable.Range(0, 50).Select(i => 60 + (i * 3.5))];

    // The band starts left of the shapes and ends right of the widest bar.
    private static readonly double SweepEnd = BarWidths[^1] + SweepBand;

    private static readonly UISettings Ui = new();

    private readonly Storyboard _sweep = new() { Duration = TimeSpan.FromMilliseconds(1500), RepeatBehavior = RepeatBehavior.Forever };

    /// <summary>
    /// Initializes a new instance of the <see cref="ListLoadingRows"/> class.
    /// </summary>
    public ListLoadingRows()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, ChatCopy.LoadingMoreConversations);

        var move = new DoubleAnimation
        {
            From                     = -SweepBand,
            To                       = SweepEnd,
            Duration                 = TimeSpan.FromMilliseconds(1200),
            EasingFunction           = new SineEase { EasingMode = EasingMode.EaseInOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(move, Resources["LoadingFill"].As<LinearGradientBrush>().Transform);
        Storyboard.SetTargetProperty(move, nameof(TranslateTransform.X));
        _sweep.Children.Add(move);

        _ = RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Sweep());
        Loaded   += (_, _) => Sweep();
        Unloaded += (_, _) => _sweep.Stop();
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    // Each show picks new bar widths, then sweeps the band.
    private void Sweep()
    {
        if (Visibility != Visibility.Visible || !IsLoaded)
        {
            _sweep.Stop();
            return;
        }

        PickWidths();
        if (Ui.AnimationsEnabled)
        {
            _sweep.Begin();
        }
    }

    // A name is usually shorter than its snippet. Thus each row takes 2 random widths, the shorter for the name.
    private void PickWidths()
    {
        (Rectangle Name, Rectangle Snippet)[] rows = [(Name0, Snippet0), (Name1, Snippet1), (Name2, Snippet2), (Name3, Snippet3)];
        foreach ((Rectangle name, Rectangle snippet) in rows)
        {
            double a      = BarWidths[Random.Shared.Next(BarWidths.Length)];
            double b      = BarWidths[Random.Shared.Next(BarWidths.Length)];
            name.Width    = Math.Min(a, b);
            snippet.Width = Math.Max(a, b);
        }
    }
}
