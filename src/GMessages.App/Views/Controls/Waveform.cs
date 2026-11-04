using GMessages.Presentation.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Path = Windows.UI.Xaml.Shapes.Path;
using Windows.System;

namespace GMessages.App.Views.Controls;

/// <summary>
/// A voice message's waveform: a row of rounded bars, the played part bright and the rest dim.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Show"/> draws a message's bars (see <see cref="VoiceWave"/>) and <see cref="ShowProgress"/> fills them
/// as it plays; a click raises <see cref="SeekRequested"/> with where it landed. While recording, <see cref="Push"/>
/// adds the newest input level on the right and moves the rest left, for a live waveform.
/// </para>
/// <para>
/// Once a message's <see cref="Length"/> is set, the waveform takes keyboard focus: Left and Right seek 5 seconds back
/// or on, and Home and End jump to the start and the end. Its UI Automation name is its value, how far it has played
/// over its length (<c>0:01 / 0:03</c>), named when shown, paused or sought (not on every tick while it plays).
/// </para>
/// <para>
/// The bars take <see cref="BarBrush"/>: the primary text brush, or the on-accent text brush on an SMS bubble (so they read on
/// every bubble color and in high contrast).
/// </para>
/// <para>
/// The bars are drawn, not made of elements: each is a line with round ends in one of two paths, the played one bright and
/// the rest dim, so a waveform is two elements however many bars it has. Playing moves only the bars it passes from the
/// dim path to the bright one. Each path has its own geometry (Windows can't share one between paths).
/// </para>
/// </remarks>
public sealed partial class Waveform : UserControl
{
    /// <summary>Identifies the <see cref="BarBrush"/> property.</summary>
    public static readonly DependencyProperty BarBrushProperty =
        DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(Waveform), new PropertyMetadata(null, OnBarBrushChanged));

    // A Bar: Its Width And The Gap After It, And The Shortest A Bar Gets (A Share Of The Height), In Effective Pixels
    private const double BarWidth   = 3;
    private const double BarGap     = 2;
    private const double MinBar     = 0.1;
    private const double WaveHeight = 28;

    // The Part Not Played Yet Shows This Dim
    private const double Unplayed = 0.4;

    // The Shortest Line A Bar Draws: Its Round Ends Make Up The Rest. Never Quite Zero, So No Line Is Empty
    private const double MinLine = 0.01;

    // How Far Left And Right Seek
    private static readonly TimeSpan KeyStep = TimeSpan.FromSeconds(5);

    // Bars: Two Paths The Full Height (So The Tallest Bar Never Moves Them), The Played Bars In The Bright One. Every Bar Is
    // Centered Unrounded, So It Grows Up And Down From The Same Center Line (Snapping To Whole Pixels Nudged Each By Up To One)
    private readonly GeometryGroup _playedBars   = new();
    private readonly GeometryGroup _unplayedBars = new();
    private readonly Path _played;
    private readonly Path _unplayed;
    private LineGeometry[] _bars = [];
    private float[] _peaks       = [];
    private int _playedCount;
    private double _progress;
    private TimeSpan _length;

    // A Click Or Key Asked For A Seek: The Next Progress Names The Waveform's New Value
    private bool _seeking;

    /// <summary>
    /// Creates the waveform: flat bars, nothing played.
    /// </summary>
    public Waveform()
    {
        _played   = NewPath(_playedBars, 1);
        _unplayed = NewPath(_unplayedBars, Unplayed);
        SetBarCount(VoiceWave.BarCount);

        // Transparent, So A Click Between The Bars Still Seeks
        Content               = new Grid { Height = WaveHeight, Background = new SolidColorBrush(Windows.UI.Colors.Transparent), Children = { _unplayed, _played } };
        IsTabStop             = false;
        UseSystemFocusVisuals = true;
        Tapped               += OnTapped;
        KeyDown              += OnKeyDown;
        SizeChanged          += OnSizeChanged;
    }

    /// <summary>
    /// Gets or sets whether the bars fill the waveform's width (the live waveform while recording): as many bars as fit,
    /// added or dropped on the left as the width changes, the newest kept on the right. Off, it draws a message's
    /// <see cref="VoiceWave.BarCount"/> bars at their own width.
    /// </summary>
    public bool FillsWidth { get; set; }

    /// <summary>
    /// Raised by a click on the waveform, with where it landed: from 0 (the start) to 1 (the end).
    /// </summary>
    public event EventHandler<double>? SeekRequested;

    /// <summary>Gets or sets the bars' brush.</summary>
    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <summary>
    /// Gets or sets how long the message plays: zero (the live waveform, or still loading) leaves it out of keyboard
    /// focus; a length lets the keys seek, and names its value.
    /// </summary>
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
    /// Draws a message's bars, nothing played; null draws flat bars (still loading).
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
    /// Fills the bars up to how much has played.
    /// </summary>
    /// <remarks>
    /// The waveform's name (its value) follows only while it isn't playing, or right after a seek: a name changed every
    /// tick would have a screen reader read it on and on.
    /// </remarks>
    /// <param name="share">How much has played, from 0 to 1.</param>
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
    /// Adds the newest input level on the right, moving the others left (the live waveform while recording).
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
    /// Measures how far the bars' centers sit from the waveform's center line, as last laid out.
    /// </summary>
    /// <remarks>
    /// Every bar grows up and down from one fixed line, so this stays at zero however loud it gets. The UI tests read it
    /// (through a test hook) while recording; the bars themselves aren't in the UI Automation tree.
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
    /// Asks for the bars' height only when <see cref="FillsWidth"/> is on, so the slot's width decides how many bars fit
    /// (asking for the bars' own width would keep a shrinking window from ever dropping one).
    /// </summary>
    /// <param name="availableSize">The space on offer.</param>
    /// <returns>The size wanted.</returns>
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        Windows.Foundation.Size wanted = base.MeasureOverride(availableSize);
        return FillsWidth ? new Windows.Foundation.Size(0, wanted.Height) : wanted;
    }

    private static void OnBarBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var wave              = (Waveform)d;
        wave._played.Stroke   = wave.BarBrush;
        wave._unplayed.Stroke = wave.BarBrush;
    }

    // A Path Of Bars: Lines As Thick As A Bar, With Round Ends, Left-Aligned In The Row At Its Full Height
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

    // Filling The Width: As Many Bars As Fit Once The Width Changes
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!FillsWidth)
        {
            return;
        }

        SetBarCount(Math.Max(1, (int)((e.NewSize.Width + BarGap) / (BarWidth + BarGap))));
    }

    // Rebuilds The Bars With This Many, Keeping The Newest Peaks On The Right, All Dim Until Drawn. The Paths Are As Wide
    // As The Bars With Their Gaps
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

    // Each Bar's Height From Its Peak (Never Below The Floor), Grown Up And Down From The Center Line: A Line Through Its
    // Middle, Short Of Its Height By The Round Ends (Half A Bar's Width Each). Then The Played Ones Bright
    private void Draw()
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            double height       = Math.Max(MinBar, _peaks[i]) * WaveHeight;
            double reach        = Math.Max(MinLine, height - BarWidth) / 2;
            double x            = (i * (BarWidth + BarGap)) + (BarWidth / 2);
            _bars[i].StartPoint = new Windows.Foundation.Point(x, (WaveHeight / 2) - reach);
            _bars[i].EndPoint   = new Windows.Foundation.Point(x, (WaveHeight / 2) + reach);
        }

        DrawPlayed();
    }

    // Moves Only The Bars Whose Played State Changed Between The Dim Path And The Bright One
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

    // Its Value For Screen Readers: Played Over Length
    private void ShowName()
    {
        if (_length > TimeSpan.Zero)
        {
            AutomationProperties.SetName(this, VoiceWave.LengthText(_length * _progress) + " / " + VoiceWave.LengthText(_length));
        }
    }

    // Left And Right Seek 5 Seconds; Home And End Jump To The Start And The End
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

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ActualWidth <= 0)
        {
            return;
        }

        e.Handled = true;
        _seeking  = true;
        SeekRequested?.Invoke(this, Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1));
    }
}
