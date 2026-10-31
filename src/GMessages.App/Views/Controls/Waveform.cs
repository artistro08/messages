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

    private readonly Rectangle[] _bars = new Rectangle[VoiceWave.BarCount];
    private readonly float[] _peaks    = new float[VoiceWave.BarCount];
    private double _progress;
    private TimeSpan _length;

    /// <summary>
    /// Creates the waveform: flat bars, nothing played.
    /// </summary>
    public Waveform()
    {
        // Bars: The Row Is Always The Full Height, So The Tallest Bar Never Moves It, And Each Bar Is Centered Unrounded, So
        // Every Bar Grows Up And Down From The Same Center Line (Snapping To Whole Pixels Nudged Each By Up To One)
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = BarGap, Height = WaveHeight };
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle
            {
                Width             = BarWidth,
                RadiusX           = BarWidth / 2,
                RadiusY           = BarWidth / 2,
                VerticalAlignment = VerticalAlignment.Center,
                UseLayoutRounding = false,
            };
            row.Children.Add(_bars[i]);
        }

        // Transparent, So A Click Between The Bars Still Seeks
        Content               = new Grid { Height = WaveHeight, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { row } };
        IsTabStop             = false;
        UseSystemFocusVisuals = true;
        Tapped               += OnTapped;
        KeyDown              += OnKeyDown;
        Draw();
    }

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

    private static void OnBarBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Waveform)d).Draw();

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
