using GMessages.App.Media;
using GMessages.Presentation.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows a small rounded square for the media of a quoted message. The square sits in the quote of a bubble and in the
/// reply chip over the message box.
/// </summary>
/// <remarks>
/// <para>
/// A photo or a video shows its thumbnail, cropped to fill the 40 px square. The thumbnail decodes at 80 px wide, so it
/// is sharp at 200% scale. A GIF shows still, because a brush draws only its first frame. Until the picture arrives,
/// the square shows a neutral fill. The media store reads the picture from memory or downloads it in the background,
/// so nothing waits for it. A video shows the same still frame as its bubble. That frame is the thumbnail of the phone,
/// or else a frame that <see cref="GMessages.App.Media.VideoStill"/> makes and keeps. A file or a voice message shows a
/// file icon.
/// </para>
/// <para>
/// In a test run, the item status holds the state of the square. The state is <c>Thumb</c> after the picture shows,
/// and <c>Placeholder</c> while it waits or when no picture came. It is <c>Icon</c> for a file.
/// </para>
/// </remarks>
public sealed partial class MediaThumb : UserControl
{
    /// <summary>Identifies the <see cref="Media"/> property.</summary>
    public static readonly DependencyProperty MediaProperty =
        DependencyProperty.Register(nameof(Media), typeof(MediaPart), typeof(MediaThumb), new PropertyMetadata(null, OnMediaChanged));

    // The decode width fits the 40 px square at 200% scale.
    private const int DecodeWidth = 80;

    /// <summary>
    /// Creates an empty square.
    /// </summary>
    public MediaThumb()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gives the square an automation peer, so its AutomationId and state reach UI Automation. A UserControl has no
    /// peer, and without a peer the square is not in the tree.
    /// </summary>
    /// <returns>The peer.</returns>
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    /// <summary>
    /// Gets or sets the media that the square shows, or null.
    /// </summary>
    public MediaPart? Media
    {
        get => (MediaPart?)GetValue(MediaProperty);
        set => SetValue(MediaProperty, value);
    }

    // Shows the icon of the new media, or its picture after it arrives. By then, a recycled quote can show other media.
    private static async void OnMediaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view                = (MediaThumb)d;
        var part                = (MediaPart?)e.NewValue;
        bool visual             = part is { Kind: MessageKind.Photo or MessageKind.Video };
        view.Picture.Background = null;
        view.Icon.Visibility    = part is not null && !visual ? Visibility.Visible : Visibility.Collapsed;
        TestHooks.ShowState(view, visual ? "Placeholder" : "Icon");
        if (part is null || !visual || part.IsPending || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        // When the picture does not load, the square keeps the fill.
        try
        {
            byte[] bytes      = part.Kind == MessageKind.Video ? await VideoStill.GetAsync(media, part) : await media.GetAsync(part, thumbnail: true, CancellationToken.None);
            ImageSource image = await Avatar.ToImageAsync(bytes, DecodeWidth) ?? throw new InvalidDataException();
            if (ReferenceEquals(part, view.Media))
            {
                view.Picture.Background = new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
                TestHooks.ShowState(view, "Thumb");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "quote thumbnail", string.Empty, ex.GetType().Name);
        }
    }
}
