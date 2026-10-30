using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

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

    private readonly Rectangle[] _bars = new Rectangle[VoiceWave.BarCount];
    private readonly float[] _peaks    = new float[VoiceWave.BarCount];
    private double _progress;

    /// <summary>
    /// Creates the waveform: flat bars, nothing played.
    /// </summary>
    public Waveform()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = BarGap, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle { Width = BarWidth, RadiusX = BarWidth / 2, RadiusY = BarWidth / 2, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(_bars[i]);
        }

        // Transparent, So A Click Between The Bars Still Seeks
        Content           = new Grid { Height = WaveHeight, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { row } };
        IsTabStop         = false;
        Tapped           += OnTapped;
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
