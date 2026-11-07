using GMessages.Presentation.Messaging;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Path = Windows.UI.Xaml.Shapes.Path;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Draws the waveform of a voice message as a row of rounded bars. The played part is bright, and the rest is dim.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Show"/> draws the bars of a message from <see cref="VoiceWave"/>. <see cref="ShowProgress"/> fills them
/// as the message plays. A click raises <see cref="SeekRequested"/> with its place. During a recording,
/// <see cref="Push"/> adds the newest input level on the right and moves the rest left for a live waveform.
/// </para>
/// <para>
/// After the <see cref="Length"/> of a message is set, the waveform takes keyboard focus. Left and Right seek 5 seconds
/// back or forward. Home and End jump to the start and the end. The UI Automation name of the waveform is its value:
/// the played time over the length, such as <c>0:01 / 0:03</c>. The name changes on show, pause and seek, but not on
/// each tick of the playback.
/// </para>
/// <para>
/// The bars take <see cref="BarBrush"/>: the primary text brush, or the on-accent text brush on an SMS bubble. Thus
/// they read on each bubble color and in high contrast.
/// </para>
/// <para>
/// The bars are lines in paths, not elements. Each bar is a round-ended line in the bright played path or in the dim
/// path. Thus a waveform is 2 elements for any number of bars. The playback moves only the bars that it
/// passes from the dim path to the bright path. Each path has its own geometry, because Windows cannot share a geometry
/// between paths.
/// </para>
/// </remarks>
public sealed partial class Waveform : UserControl
{
    /// <summary>Identifies the <see cref="BarBrush"/> property.</summary>
    public static readonly DependencyProperty BarBrushProperty =
        DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(Waveform), new PropertyMetadata(null, OnBarBrushChanged));

    // A bar has a width and a gap after it in effective pixels. MinBar is the shortest bar as a share of the height.
    private const double BarWidth   = 3;
    private const double BarGap     = 2;
    private const double MinBar     = 0.1;
    private const double WaveHeight = 28;

    // The part that did not play yet shows at this opacity.
    private const double Unplayed = 0.4;

    // MinLine is the shortest line of a bar, and the round ends make the rest of the bar. The value is not zero, so no
    // line is empty.
    private const double MinLine = 0.01;

    // Left and Right seek by this step.
    private static readonly TimeSpan KeyStep = TimeSpan.FromSeconds(5);

    // The 2 paths have the full height, so the tallest bar never moves them. The bright path holds the played bars.
    // Each bar centers with no rounding, so it grows up and down from the same center line. A snap to whole pixels
    // moved each bar by up to 1 px.
    private readonly GeometryGroup _playedBars   = new();
    private readonly GeometryGroup _unplayedBars = new();
    private readonly Path _played;
    private readonly Path _unplayed;
    private LineGeometry[] _bars = [];
    private float[] _peaks       = [];
    private int _playedCount;
    private double _progress;
    private TimeSpan _length;

    // A click or a key asked for a seek, so the next progress names the new value of the waveform.
    private bool _seeking;

    /// <summary>
    /// Creates the waveform with flat bars and nothing played.
    /// </summary>
    public Waveform()
    {
        _played   = NewPath(_playedBars, 1);
        _unplayed = NewPath(_unplayedBars, Unplayed);
        SetBarCount(VoiceWave.BarCount);

        // The transparent background lets a click between the bars seek.
        Content               = new Grid { Height = WaveHeight, Background = new SolidColorBrush(Windows.UI.Colors.Transparent), Children = { _unplayed, _played } };
        IsTabStop             = false;
        UseSystemFocusVisuals = true;
        Tapped               += OnTapped;
        KeyDown              += OnKeyDown;
        SizeChanged          += OnSizeChanged;
    }

    /// <summary>
    /// Gets or sets whether the bars fill the width of the waveform, as the live waveform does during a recording.
    /// </summary>
    /// <remarks>
    /// When the value is true, the waveform draws as many bars as fit. A width change adds or drops bars on the left
    /// and keeps the newest bar on the right. When the value is false, the waveform draws the
    /// <see cref="VoiceWave.BarCount"/> bars of a message at their own width.
    /// </remarks>
    public bool FillsWidth { get; set; }

    /// <summary>
    /// Raised by a click or a seek key with the new place: 0 at the start, 1 at the end.
    /// </summary>
    public event EventHandler<double>? SeekRequested;

    /// <summary>Gets or sets the brush of the bars.</summary>
    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <summary>
    /// Gets or sets the play length of the message.
    /// </summary>
    /// <remarks>
    /// Zero keeps the waveform out of keyboard focus, as for the live waveform or during a load. A length lets the keys
    /// seek and names the value.
    /// </remarks>
    public TimeSpan Length
    {
        get => _length;
        set
        {
            _length   = value;
            IsTabStop = value > TimeSpan.Zero;
            ShowName();
        }
    }

    /// <summary>
    /// Draws the bars of a message with nothing played. Null draws flat bars during a load.
    /// </summary>
    /// <param name="peaks">The bars, from 0 to 1, or null.</param>
    public void Show(float[]? peaks)
    {
        Array.Clear(_peaks);
        peaks?.AsSpan(0, Math.Min(peaks.Length, _peaks.Length)).CopyTo(_peaks);
        _progress = 0;
        Draw();
        ShowName();
    }

    /// <summary>
    /// Fills the bars up to the played share.
    /// </summary>
    /// <remarks>
    /// The name of the waveform is its value. The name changes only while the message does not play, or directly after
    /// a seek. A screen reader reads each name change. Thus a change on each tick makes it read the name all the time.
    /// </remarks>
    /// <param name="share">The played share, from 0 to 1.</param>
    /// <param name="playing">True while it plays.</param>
    public void ShowProgress(double share, bool playing)
    {
        _progress = Math.Clamp(share, 0, 1);
        DrawPlayed();
        if (!playing || _seeking)
        {
            _seeking = false;
            ShowName();
        }
    }

    /// <summary>
    /// Adds the newest input level on the right and moves the others left, for the live waveform during a recording.
    /// </summary>
    /// <param name="level">The level, from 0 to 1.</param>
    public void Push(float level)
    {
        Array.Copy(_peaks, 1, _peaks, 0, _peaks.Length - 1);
        _peaks[^1] = Math.Clamp(level, 0, 1);
        _progress  = 1;
        Draw();
    }

    /// <summary>
    /// Measures the distance of the bar centers from the center line of the waveform in the last layout.
    /// </summary>
    /// <remarks>
    /// Each bar grows up and down from 1 fixed line, so the distance stays at zero for all levels. The UI tests read it
    /// through a test hook during a recording. The bars are not in the UI Automation tree.
    /// </remarks>
    /// <returns>The largest distance, in effective pixels.</returns>
    internal double CenterDrift()
    {
        double top   = _played.TransformToVisual(this).TransformPoint(default).Y;
        double drift = 0;
        foreach (LineGeometry bar in _bars)
        {
            double center = top + ((bar.StartPoint.Y + bar.EndPoint.Y) / 2);
            drift         = Math.Max(drift, Math.Abs(center - (WaveHeight / 2)));
        }

        return drift;
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    /// <summary>
    /// Asks only for the height of the bars when <see cref="FillsWidth"/> is on. Thus the width of the slot sets the
    /// number of bars.
    /// </summary>
    /// <remarks>
    /// A request for the width of the bars keeps the slot as wide as the bars. Then a smaller window never drops a bar.
    /// </remarks>
    /// <param name="availableSize">The available space.</param>
    /// <returns>The desired size.</returns>
    protected override Size MeasureOverride(Size availableSize)
    {
        Size wanted = base.MeasureOverride(availableSize);
        return FillsWidth ? new Size(0, wanted.Height) : wanted;
    }

    private static void OnBarBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var wave              = (Waveform)d;
        wave._played.Stroke   = wave.BarBrush;
        wave._unplayed.Stroke = wave.BarBrush;
    }

    // Creates a path of bars. Its lines are as thick as a bar and have round ends. The path aligns left in the row at
    // the full height.
    private static Path NewPath(GeometryGroup bars, double opacity) => new()
    {
        Data                = bars,
        Height              = WaveHeight,
        HorizontalAlignment = HorizontalAlignment.Left,
        Opacity             = opacity,
        StrokeThickness     = BarWidth,
        StrokeStartLineCap  = PenLineCap.Round,
        StrokeEndLineCap    = PenLineCap.Round,
        UseLayoutRounding   = false,
    };

    // When the bars fill the width, a width change sets as many bars as fit.
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!FillsWidth)
        {
            return;
        }

        SetBarCount(Math.Max(1, (int)((e.NewSize.Width + BarGap) / (BarWidth + BarGap))));
    }

    // Builds the bars again with this count and keeps the newest peaks on the right. All bars are dim until the next
    // draw. The paths are as wide as the bars with their gaps.
    private void SetBarCount(int count)
    {
        if (count == _bars.Length)
        {
            return;
        }

        var peaks = new float[count];
        int kept  = Math.Min(count, _peaks.Length);
        Array.Copy(_peaks, _peaks.Length - kept, peaks, count - kept, kept);
        _peaks = peaks;

        _playedBars.Children.Clear();
        _unplayedBars.Children.Clear();
        _playedCount = 0;
        _bars        = new LineGeometry[count];
        for (int i = 0; i < count; i++)
        {
            _bars[i] = new LineGeometry();
            _unplayedBars.Children.Add(_bars[i]);
        }

        _played.Width = _unplayed.Width = (count * (BarWidth + BarGap)) - BarGap;
        Draw();
    }

    // Sets the height of each bar from its peak, never below the floor. Each bar grows up and down from the center
    // line. Its line runs through its middle and is shorter than its height by the round ends. Each round end is half
    // the bar width. Then the played bars become bright.
    private void Draw()
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            double height       = Math.Max(MinBar, _peaks[i]) * WaveHeight;
            double reach        = Math.Max(MinLine, height - BarWidth) / 2;
            double x            = (i * (BarWidth + BarGap)) + (BarWidth / 2);
            _bars[i].StartPoint = new Point(x, (WaveHeight / 2) - reach);
            _bars[i].EndPoint   = new Point(x, (WaveHeight / 2) + reach);
        }

        DrawPlayed();
    }

    // Moves only the bars with a changed played state between the dim path and the bright path.
    private void DrawPlayed()
    {
        int played = (int)Math.Round(_progress * _bars.Length);
        while (_playedCount < played)
        {
            LineGeometry bar = _bars[_playedCount++];
            _unplayedBars.Children.Remove(bar);
            _playedBars.Children.Add(bar);
        }

        while (_playedCount > played)
        {
            LineGeometry bar = _bars[--_playedCount];
            _playedBars.Children.Remove(bar);
            _unplayedBars.Children.Add(bar);
        }
    }

    // Sets the value for screen readers: the played time over the length.
    private void ShowName()
    {
        if (_length > TimeSpan.Zero)
        {
            AutomationProperties.SetName(this, VoiceWave.LengthText(_length * _progress) + " / " + VoiceWave.LengthText(_length));
        }
    }

    // Left and Right seek 5 seconds. Home and End jump to the start and the end.
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_length <= TimeSpan.Zero)
        {
            return;
        }

        double? share = e.Key switch
        {
            VirtualKey.Left  => _progress - (KeyStep / _length),
            VirtualKey.Right => _progress + (KeyStep / _length),
            VirtualKey.Home  => 0,
            VirtualKey.End   => 1,
            _                => null,
        };
        if (share is not { } to)
        {
            return;
        }

        e.Handled = true;
        _seeking  = true;
        SeekRequested?.Invoke(this, Math.Clamp(to, 0, 1));
    }

    // A quote or a caption can make the bubble wider than the bars. The bars stay at the left, so the share is of their
    // width, not of the control width.
    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        double x = e.GetPosition(this).X;
        if (_played.Width is not > 0 || x > _played.Width)
        {
            return;
        }

        e.Handled = true;
        _seeking  = true;
        SeekRequested?.Invoke(this, Math.Clamp(x / _played.Width, 0, 1));
    }
}
