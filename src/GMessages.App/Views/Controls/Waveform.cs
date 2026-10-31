using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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
/// over its length (<c>0:01 / 0:03</c>).
/// </para>
/// <para>
/// The bars take <see cref="BarBrush"/>: the primary text brush, or the on-accent text brush on an SMS bubble (so they read on
/// every bubble color and in high contrast).
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

    // How Far Left And Right Seek
    private static readonly TimeSpan KeyStep = TimeSpan.FromSeconds(5);

    // Bars: The Row Is Always The Full Height, So The Tallest Bar Never Moves It
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = BarGap, Height = WaveHeight };
    private Rectangle[] _bars = [];
    private float[] _peaks    = [];
    private double _progress;
    private TimeSpan _length;

    /// <summary>
    /// Creates the waveform: flat bars, nothing played.
    /// </summary>
    public Waveform()
    {
        SetBarCount(VoiceWave.BarCount);

        // Transparent, So A Click Between The Bars Still Seeks
        Content               = new Grid { Height = WaveHeight, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { _row } };
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
            Draw();
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
    }

    /// <summary>
    /// Fills the bars up to how much has played.
    /// </summary>
    /// <param name="share">How much has played, from 0 to 1.</param>
    public void ShowProgress(double share)
    {
        _progress = Math.Clamp(share, 0, 1);
        Draw();
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
        double drift = 0;
        foreach (Rectangle bar in _bars)
        {
            double center = bar.TransformToVisual(this).TransformPoint(default).Y + (bar.ActualHeight / 2);
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

    private static void OnBarBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Waveform)d).Draw();

    // Filling The Width: As Many Bars As Fit Once The Width Changes
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!FillsWidth)
        {
            return;
        }

        SetBarCount(Math.Max(1, (int)((e.NewSize.Width + BarGap) / (BarWidth + BarGap))));
    }

    // Rebuilds The Row With This Many Bars, Keeping The Newest Peaks On The Right. Each Bar Is Centered Unrounded, So Every
    // Bar Grows Up And Down From The Same Center Line (Snapping To Whole Pixels Nudged Each By Up To One)
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

        _bars = new Rectangle[count];
        _row.Children.Clear();
        for (int i = 0; i < count; i++)
        {
            _bars[i] = new Rectangle
            {
                Width             = BarWidth,
                RadiusX           = BarWidth / 2,
                RadiusY           = BarWidth / 2,
                VerticalAlignment = VerticalAlignment.Center,
                UseLayoutRounding = false,
            };
            _row.Children.Add(_bars[i]);
        }

        Draw();
    }

    // Each Bar's Height From Its Peak (Never Below The Floor), Bright Up To The Played Share
    private void Draw()
    {
        int played = (int)Math.Round(_progress * _bars.Length);
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i].Height  = Math.Max(MinBar, _peaks[i]) * WaveHeight;
            _bars[i].Fill    = BarBrush;
            _bars[i].Opacity = i < played ? 1 : Unplayed;
        }

        // Its Value For Screen Readers: Played Over Length
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
        SeekRequested?.Invoke(this, Math.Clamp(to, 0, 1));
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ActualWidth <= 0)
        {
            return;
        }

        e.Handled = true;
        SeekRequested?.Invoke(this, Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1));
    }
}
