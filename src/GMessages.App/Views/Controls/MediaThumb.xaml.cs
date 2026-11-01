using GMessages.App.Media;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GMessages.App.Views.Controls;

/// <summary>
/// A small rounded square standing for a quoted or replied-to message's media, in a bubble's quote and in the reply chip
/// over the message box.
/// </summary>
/// <remarks>
/// <para>
/// A photo or video shows its thumbnail, decoded 80 px wide (sharp at 200% scale), cropped to fill the 40 px square; a
/// GIF shows still (a brush draws its first frame). Until the picture arrives, the square shows a neutral fill, and the
/// picture swaps in when it does: the media store reads it from memory, or downloads it in the background, so nothing
/// waits on it. A video shows the bubble's still frame (the phone's thumbnail, else one made here and kept; see
/// <see cref="GMessages.App.Media.VideoStill"/>). A file or voice message shows a file icon.
/// </para>
/// <para>
/// A test run puts its state in its item status: <c>Thumb</c> once the picture shows, <c>Placeholder</c> while it waits
/// or when none came, <c>Icon</c> for a file.
/// </para>
/// </remarks>
public sealed partial class MediaThumb : UserControl
{
    /// <summary>Identifies the <see cref="Media"/> property.</summary>
    public static readonly DependencyProperty MediaProperty =
        DependencyProperty.Register(nameof(Media), typeof(MediaPart), typeof(MediaThumb), new PropertyMetadata(null, OnMediaChanged));

    // The Thumbnail's Decode Width: The 40 px Square At 200% Scale
    private const int DecodeWidth = 80;

    /// <summary>
    /// Creates the square, empty.
    /// </summary>
    public MediaThumb()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gives the square an automation peer, so its AutomationId and state reach UI Automation (a UserControl has none of
    /// its own, which left it out of the tree).
    /// </summary>
    /// <returns>The peer.</returns>
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    /// <summary>
    /// Gets or sets the media the square stands for, or null.
    /// </summary>
    public MediaPart? Media
    {
        get => (MediaPart?)GetValue(MediaProperty);
        set => SetValue(MediaProperty, value);
    }

    // Another Media: Its Icon, Or Its Picture Once It Arrives (A Recycled Quote May Have Moved On By Then)
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

        // A Picture That Won't Load Keeps The Fill
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
