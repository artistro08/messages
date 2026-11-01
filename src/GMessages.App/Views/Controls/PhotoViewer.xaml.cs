using System.Runtime.InteropServices.WindowsRuntime;
using GMessages.App.Media;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace GMessages.App.Views.Controls;

/// <summary>
/// The in-app photo viewer: a clicked photo over the whole window, title bar row included, on a dark scrim (ruling R7-3:
/// an in-window overlay, not a separate window).
/// </summary>
/// <remarks>
/// <para>
/// The toolbar sits in the title bar row, beside the caption buttons (owner, Plan 7). The window lets clicks through to
/// it there, keeps the rest of the row dragging the window, and turns the caption buttons dark to read on the scrim, all
/// while <see cref="IsOpenChanged"/> says the viewer is open (see <c>MainWindow</c>).
/// </para>
/// <para>
/// The photo is fitted and centered at 1x in a zoomable <see cref="ScrollViewer"/> (<c>PhotoViewer.Photo</c>, named by
/// the photo's caption or "Photo"): Ctrl + mouse wheel and pinch zoom it up to 8x, and a double-click zooms in to 2x at
/// the pointer, or back out to 1x. The toolbar has Save as (<c>PhotoViewer.SaveAs</c>), Download
/// (<c>PhotoViewer.Download</c>), Open in Photos (<c>PhotoViewer.OpenInPhotos</c>, the old click's default app) and
/// Close (<c>PhotoViewer.Close</c>).
/// </para>
/// <para>
/// Escape (handled by the main window before its own shortcuts, so it doesn't also close the chat), Close, or a click
/// beside the photo closes it, and focus goes back to the photo in the chat. Tab cycles inside the viewer. The decrypted
/// bytes come from the media store's memory and are never written for the viewer; closing drops the image.
/// </para>
/// <para>
/// A test run puts the zoom in the photo's item status (<c>Zoom1</c>, <c>Zoom2</c>, ...) for the tests to read.
/// </para>
/// <para>
/// A video (<see cref="ShowVideoAsync"/>) plays in place of the photo (<c>PhotoViewer.Video</c>) with Windows' full
/// transport controls, from where the bubble's player got to; Open in Photos reads Open in default app. Opening the
/// viewer stops a video playing in a bubble, and closing it stops its own.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Close disposes the video's player; a control has no Dispose of its own.")]
public sealed partial class PhotoViewer : UserControl
{
    // The Space Kept Around The Photo At 1x, In Effective Pixels
    private const double PhotoMargin = 24;

    // The Shown Photo, Its Message, And Where Focus Goes On Close
    private MediaPart? _part;
    private string _messageId = string.Empty;
    private Control? _returnFocus;

    // How The Photo In The Chat Had Focus When The Viewer Opened (Pointer For A Click), So Closing Gives It Back The Same
    // Way: A Clicked Photo Gets No Focus Ring Back, Even When Escape Closes The Viewer
    private FocusState _returnState = FocusState.Programmatic;

    // The Playing Video, While One Shows
    private VideoPlayback? _video;

    /// <summary>
    /// Initializes a new instance of the <see cref="PhotoViewer"/> class, hidden.
    /// </summary>
    public PhotoViewer()
    {
        InitializeComponent();
    }

    /// <summary>Gets the Save as button's name and tooltip.</summary>
    public static string SaveAsText => ChatCopy.SaveAs;

    /// <summary>Gets the Download button's name and tooltip.</summary>
    public static string DownloadText => ChatCopy.Download;

    /// <summary>Gets the Open in Photos button's name and tooltip.</summary>
    public static string OpenInPhotosText => ChatCopy.OpenInPhotos;

    /// <summary>Gets the Close button's name and tooltip.</summary>
    public static string CloseText => ChatCopy.Close;

    /// <summary>
    /// Gets a value indicating whether the viewer is showing a photo.
    /// </summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>
    /// Gets the toolbar, which sits in the window's title bar row; the window lets clicks through to it there.
    /// </summary>
    public FrameworkElement Toolbar => ToolbarPanel;

    /// <summary>
    /// Raised when the viewer opens or closes, so the window can give the title bar row to the toolbar and back.
    /// </summary>
    public event EventHandler? IsOpenChanged;

    /// <summary>
    /// Keeps the toolbar just left of the window's caption buttons, with the toolbar's own spacing between them.
    /// </summary>
    /// <param name="captionWidth">The caption buttons' width, in effective pixels.</param>
    public void FitToolbar(double captionWidth)
    {
        var margin = new Thickness(0, 0, captionWidth + ToolbarPanel.Spacing, 0);
        if (ToolbarPanel.Margin != margin)
        {
            ToolbarPanel.Margin = margin;
        }
    }

    /// <summary>
    /// Shows a photo, fitted at 1x, and moves focus to it.
    /// </summary>
    /// <remarks>
    /// The full image is read from the media store (memory, or a download when it was evicted) and decoded at its own
    /// size. A photo that won't load closes the viewer again and logs the error type only.
    /// </remarks>
    /// <param name="part">The photo's media part.</param>
    /// <param name="messageId">The message's ID, for the log and the actions.</param>
    /// <param name="name">What Narrator calls the photo: its caption, or "Photo".</param>
    /// <param name="returnFocus">The photo in the chat, focused again on close.</param>
    /// <returns>A task that ends once the photo shows, or failed.</returns>
    public async Task ShowAsync(MediaPart part, string messageId, string name, Control returnFocus)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(returnFocus);

        Open(part, messageId, returnFocus, returnFocus.FocusState, isVideo: false);
        AutomationProperties.SetName(Zoomer, name);
        PhotoImage.Source = null;
        Zoomer.ChangeView(0, 0, 1, disableAnimation: true);

        // Focus Moves In Once The Viewer Is Laid Out (Before That It Can't Take It), After The Click Has Finished With
        // The Photo In The Chat
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Zoomer.Focus(FocusState.Programmatic));

        // The Full Image, Decoded From Memory
        try
        {
            if ((Application.Current as App)?.Media is not { } media)
            {
                throw new InvalidOperationException("No media store.");
            }

            byte[] bytes     = await media.GetAsync(part, thumbnail: false, CancellationToken.None);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            if (ReferenceEquals(part, _part))
            {
                PhotoImage.Source = image;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "view", messageId, ex.GetType().Name);
            if (ReferenceEquals(part, _part))
            {
                Close();
            }
        }
    }

    /// <summary>
    /// Shows a video and plays it with Windows' full transport controls, from where the bubble's player got to.
    /// </summary>
    /// <remarks>
    /// The bytes come from the media store's memory (or a download) and play from memory, as media, like the bubble's
    /// player (see <see cref="VideoPlayback"/>). A video that won't load closes the viewer again and logs the error type
    /// only. Escape or Close stops it and lets go of the player.
    /// </remarks>
    /// <param name="part">The video's media part.</param>
    /// <param name="messageId">The message's ID, for the log and the actions.</param>
    /// <param name="name">What Narrator calls the video: its caption, or "Video".</param>
    /// <param name="returnFocus">The video's frame in the chat, focused again on close.</param>
    /// <param name="returnState">How the clicked button had focus, so closing gives it back the same way.</param>
    /// <param name="start">Where to start playing.</param>
    /// <returns>A task that ends once the video plays, or failed.</returns>
    public async Task ShowVideoAsync(MediaPart part, string messageId, string name, Control returnFocus, FocusState returnState, TimeSpan start)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(returnFocus);

        Open(part, messageId, returnFocus, returnState, isVideo: true);
        try
        {
            if ((Application.Current as App)?.Media is not { } media)
            {
                throw new InvalidOperationException("No media store.");
            }

            byte[] bytes = await media.GetAsync(part, thumbnail: false, CancellationToken.None);
            if (!ReferenceEquals(part, _part))
            {
                return;
            }

            _video = new VideoPlayback(DispatcherQueue, bytes, part.Mime, start, compact: false, "PhotoViewer.Video", name);
            VideoHost.Children.Add(_video.Element);
            _video.Player.Play();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _video?.Element.Focus(FocusState.Programmatic));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "view", messageId, ex.GetType().Name);
            if (ReferenceEquals(part, _part))
            {
                Close();
            }
        }
    }

    /// <summary>
    /// Hides the viewer, stops a playing video, drops the image, and focuses the photo or video in the chat again.
    /// </summary>
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        Visibility        = Visibility.Collapsed;
        PhotoImage.Source = null;
        _part             = null;
        StopVideo();
        _returnFocus?.Focus(_returnState);
        _returnFocus = null;
        IsOpenChanged?.Invoke(this, EventArgs.Empty);
    }

    // Shows The Viewer For A Photo Or A Video: Any Video Playing (Here Or In A Bubble) Stops, The Right Layer Shows, And
    // Open In Photos Becomes Open In Default App For A Video
    private void Open(MediaPart part, string messageId, Control returnFocus, FocusState returnState, bool isVideo)
    {
        StopVideo();
        MessageBubble.StopInlineVideo();
        _part        = part;
        _messageId   = messageId;
        _returnFocus = returnFocus;
        _returnState = returnState == FocusState.Unfocused ? FocusState.Programmatic : returnState;

        string open = isVideo ? ChatCopy.OpenInDefaultApp : ChatCopy.OpenInPhotos;
        AutomationProperties.SetName(OpenInPhotosButton, open);
        ToolTipService.SetToolTip(OpenInPhotosButton, open);
        Zoomer.Visibility    = isVideo ? Visibility.Collapsed : Visibility.Visible;
        VideoHost.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        if (!IsOpen)
        {
            Visibility = Visibility.Visible;
            IsOpenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Stops The Viewer's Video And Lets Go Of Its Player And Bytes
    private void StopVideo()
    {
        if (_video is not { } video)
        {
            return;
        }

        _video = null;
        VideoHost.Children.Clear();
        video.Dispose();
    }

    // =========================================================================
    // TOOLBAR
    // =========================================================================

    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        if (_part is { } part)
        {
            _ = MediaActions.SaveAsAsync(XamlRoot, part, _messageId);
        }
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_part is { } part)
        {
            _ = MediaActions.DownloadAsync(part, _messageId);
        }
    }

    private void OnOpenInPhotosClick(object sender, RoutedEventArgs e)
    {
        if (_part is { } part)
        {
            // A Video Pauses Here While Its Default App Plays It
            _video?.Player.Pause();
            _ = MediaActions.OpenAsync(XamlRoot, part, _messageId);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // =========================================================================
    // ZOOM
    // =========================================================================

    // At 1x The Photo Fits The Viewport, Inside A 24 px Margin (A Smaller Photo Shows At Its Own Size); Zooming Scales It
    // From There. The Image Is Only As Big As The Photo, So A Click Beside It Isn't On It
    private void OnZoomerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PhotoImage.MaxWidth  = Math.Max(0, e.NewSize.Width - (PhotoMargin * 2));
        PhotoImage.MaxHeight = Math.Max(0, e.NewSize.Height - (PhotoMargin * 2));
    }

    // Double-Click: 2x At The Pointer, Or Back To 1x When Zoomed
    private void OnPhotoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (Zoomer.ZoomFactor > 1.01f)
        {
            Zoomer.ChangeView(0, 0, 1);
            return;
        }

        Windows.Foundation.Point at = e.GetPosition(PhotoImage);
        Zoomer.ChangeView((at.X * 2) - (Zoomer.ViewportWidth / 2), (at.Y * 2) - (Zoomer.ViewportHeight / 2), 2);
    }

    // A Click Beside The Photo Closes The Viewer; On The Photo It Doesn't (Its Double-Click Zooms)
    private void OnZoomerTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PhotoImage))
        {
            Close();
        }
    }

    private void OnZoomerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate)
        {
            TestHooks.ShowState(Zoomer, "Zoom" + Math.Round(Zoomer.ZoomFactor).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
