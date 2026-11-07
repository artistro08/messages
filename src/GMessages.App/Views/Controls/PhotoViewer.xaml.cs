using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using GMessages.App.Media;
using GMessages.App.Windowing;
using GMessages.Presentation.Messaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.Composition;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows a clicked photo over the whole window, with the title bar row, on a dark scrim.
/// </summary>
/// <remarks>
/// <para>
/// Ruling R7-3 made the viewer an overlay in the window, not a separate window.
/// </para>
/// <para>
/// The toolbar sits in the top-left corner of the title bar row. Close comes first, then Open in Photos, Open with,
/// Download and Save as. The owner asked for Download and Save as in Plan 8. Open with <c>PhotoViewer.OpenWith</c>
/// opens the app picker of Windows, as the owner asked on 2026-11-01. Close is a plain back arrow with no plate or
/// fill, as the owner asked in Plan 9. The icons dim while the window is inactive. See <see cref="ShowActive"/>.
/// </para>
/// <para>
/// While <see cref="IsOpenChanged"/> says that the viewer is open, the window lets clicks through to the toolbar. The
/// rest of the row still drags the window. The caption buttons turn dark, so they read on the scrim. See
/// <c>MainWindow</c>.
/// </para>
/// <para>
/// The photo fits and centers at 1x in a zoomable <see cref="ScrollViewer"/> <c>PhotoViewer.Photo</c>. Its name is the
/// caption of the photo or "Photo". Ctrl + mouse wheel and pinch zoom it up to 8x. A double-click zooms in to 2x at the
/// pointer, or back out to 1x. The toolbar has Save as <c>PhotoViewer.SaveAs</c>, Download
/// <c>PhotoViewer.Download</c> and Close <c>PhotoViewer.Close</c>. Open in Photos <c>PhotoViewer.OpenInPhotos</c>
/// opens the default app, as the old click did.
/// </para>
/// <para>
/// Escape, Close or a click beside the photo closes the viewer. Focus then returns to the photo in the chat. The main
/// window handles Escape before its own shortcuts, so Escape does not also close the chat. Tab cycles inside the
/// viewer. The decrypted bytes come from the memory of the media store, and the viewer never writes them to disk. A
/// close drops the image.
/// </para>
/// <para>
/// A test run puts the zoom in the item status of the photo for the tests: <c>Zoom1</c>, <c>Zoom2</c> and so on.
/// </para>
/// <para>
/// An open grows the photo or video out of its bubble while the backdrop and the toolbar fade in. It goes from the
/// place and size of the bubble to its own in 300 ms. The grow uses the decelerate curve of Fluent. Escape, Close or a
/// click beside the photo shrinks it back in 200 ms on the accelerate curve. The backdrop and the toolbar then fade
/// out. The owner asked for this in Plan 8.
/// </para>
/// <para>
/// A uniform Composition scale and a translation move the stage that holds both. An inset clip opens from the crop of
/// the bubble to the whole photo. Thus a cropped photo shows with no distortion, and no layout moves. With the
/// animation effects of Windows off, the viewer shows and hides at once.
/// </para>
/// <para>
/// A video grows and shrinks as the still frame of the bubble. The picture of a playing video does not scale smoothly.
/// Its player replaces the still when it plays. A test run notes <c>Grew</c>, <c>GrewStill</c>, <c>Player</c>,
/// <c>Shown</c> or <c>Shrank</c> in <c>viewer-motion.txt</c>. <c>Shown</c> means that no bubble was there to grow
/// from.
/// </para>
/// <para>
/// A video plays in place of the photo in <c>PhotoViewer.Video</c>, with the full transport controls of Windows. See
/// <see cref="ShowVideoAsync"/>. It starts where the player of the bubble stopped. Open in Photos then reads Open in
/// default app. An open of the viewer stops a video that plays in a bubble. A close stops the own video of the viewer.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Close disposes the player of the video. A control has no Dispose.")]
public sealed partial class PhotoViewer : UserControl
{
    // The space around the photo at 1x, in effective pixels.
    private const double PhotoMargin = 24;

    // The gap of the toolbar from the left edge of the window.
    private const double ToolbarInset = 8;

    // The widest decode of a photo in physical pixels, so that a 30000 px photo cannot exhaust the memory. Past the
    // zoom in the second field, the photo decodes again at its own width.
    private const int MaxDecodeWidth  = 8192;
    private const float FullDecodeZoom = 2;

    // An open grows the photo out of its bubble on the decelerate curve of Fluent. A close shrinks it back faster on
    // the accelerate curve.
    private static readonly TimeSpan GrowTime   = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ShrinkTime = TimeSpan.FromMilliseconds(200);
    private static readonly UISettings Ui       = new();

    // Each open and close adds 1 to the run. Thus a shrink that ends after the viewer opens again changes nothing.
    private int _closeRun;
    private bool _closing;

    // The shown photo, its message, and the control that gets focus on close.
    private MediaPart? _part;
    private string _messageId = string.Empty;
    private Control? _returnFocus;

    // The own size of the shown photo and its decode width, in physical pixels. The size is 0 x 0 when unknown.
    private int _photoWidth;
    private int _photoHeight;
    private int _decodedWidth;

    // The focus state that the photo in the chat had when the viewer opened, such as Pointer for a click. A close gives
    // focus back in the same way. Thus a clicked photo gets no focus ring back, also when Escape closes the viewer.
    private FocusState _returnState = FocusState.Programmatic;

    // The playing video while one shows, and the still that it grows and shrinks as.
    private VideoPlayback? _video;
    private ImageSource? _poster;

    // The player of the bubble, which continues the video after the close. The close notes the position and the play
    // state when it starts.
    private Action<TimeSpan, bool>? _videoClosed;
    private (TimeSpan Position, bool Playing)? _closedAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="PhotoViewer"/> class, which is hidden.
    /// </summary>
    public PhotoViewer()
    {
        InitializeComponent();
        VideoHost.SizeChanged += (_, _) => FitVideo(_video);
    }

    /// <summary>Gets the name and tooltip of the Save as button.</summary>
    public static string SaveAsText => ChatCopy.SaveAs;

    /// <summary>Gets the name and tooltip of the Download button.</summary>
    public static string DownloadText => ChatCopy.Download;

    /// <summary>Gets the name and tooltip of the Open with button.</summary>
    public static string OpenWithText => ChatCopy.OpenWith;

    /// <summary>Gets the name and tooltip of the Close button.</summary>
    public static string CloseText => ChatCopy.Close;

    /// <summary>
    /// Gets a value indicating whether the viewer is open.
    /// </summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>
    /// Gets the toolbar, which sits in the title bar row of the window. The window lets clicks through to it there.
    /// </summary>
    public FrameworkElement Toolbar => ToolbarPanel;

    /// <summary>
    /// Occurs when the viewer opens or closes. The window then gives the title bar row to the toolbar or takes it back.
    /// </summary>
    public event EventHandler? IsOpenChanged;

    /// <summary>
    /// Keeps the toolbar 8 px in from the top-left corner of the title bar row. The toolbar also stays past any space
    /// that the system keeps on the left.
    /// </summary>
    /// <param name="leftInset">
    /// The width that the system keeps on the left of the row, in effective pixels. It is 0 unless the layout is right
    /// to left.
    /// </param>
    public void FitToolbar(double leftInset)
    {
        var margin = new Thickness(leftInset + ToolbarInset, 0, 0, 0);
        if (ToolbarPanel.Margin != margin)
        {
            ToolbarPanel.Margin = margin;
        }
    }

    /// <summary>
    /// Dims the toolbar icons while the window is inactive, as the title bar text dims. The icons return when the
    /// window is active again. A test run puts <c>Active</c> or <c>Inactive</c> in the item status of Close.
    /// </summary>
    /// <param name="active">True while the window is active.</param>
    public void ShowActive(bool active)
    {
        VisualStateManager.GoToState(this, active ? "Active" : "Inactive", false);
        TestHooks.ShowState(CloseButton, active ? "Active" : "Inactive");
    }

    /// <summary>
    /// Shows a photo, fitted at 1x, and moves focus to it.
    /// </summary>
    /// <remarks>
    /// The full image comes from the media store: from memory, or from a download after an eviction. It decodes at its
    /// own size, but no wider than twice the window. A photo that does not load closes the viewer again, and the log
    /// gets only the error type.
    /// </remarks>
    /// <param name="part">The media part of the photo.</param>
    /// <param name="messageId">The ID of the message, for the log and the actions.</param>
    /// <param name="name">The name that Narrator reads for the photo: its caption, or "Photo".</param>
    /// <param name="returnFocus">The photo in the chat, which gets focus again on close.</param>
    /// <returns>A task that ends when the photo shows or the load fails.</returns>
    public async Task ShowAsync(MediaPart part, string messageId, string name, Control returnFocus)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(returnFocus);

        Open(part, messageId, returnFocus, returnFocus.FocusState, isVideo: false);
        AutomationProperties.SetName(Zoomer, name);
        PhotoImage.Source = null;
        Zoomer.ChangeView(0, 0, 1, disableAnimation: true);

        // Focus moves in after the layout of the viewer, because the viewer cannot take it before. The move also waits
        // for the end of the click on the photo in the chat.
        XamlHost.Dispatcher.TryEnqueue(Windows.System.DispatcherQueuePriority.Low, () => Zoomer.Focus(FocusState.Programmatic));

        try
        {
            if ((Application.Current as App)?.Media is not { } media)
            {
                throw new InvalidOperationException("No media store.");
            }

            // The photo decodes no wider than twice the window in physical pixels, so it stays sharp to 2x zoom.
            // Neither side goes past MaxDecodeWidth.
            byte[] bytes = await media.GetAsync(part, thumbnail: false, CancellationToken.None);
            double twice = (XamlRoot?.Size.Width ?? 0) * (XamlRoot?.RasterizationScale ?? 1) * 2;
            int fit      = twice > 0 ? (int)Math.Min(Math.Ceiling(twice), MaxDecodeWidth) : MaxDecodeWidth;
            (ImageSource? image, int pixelWidth, int pixelHeight, int decodeWidth) =
                await Avatar.ToFittedImageAsync(bytes, (w, h) => CappedWidth(w, h, fit), DecodePixelType.Physical);
            if (image is null)
            {
                throw new InvalidDataException();
            }

            if (ReferenceEquals(part, _part) && !_closing)
            {
                _photoWidth       = pixelWidth;
                _photoHeight      = pixelHeight;
                _decodedWidth     = decodeWidth;
                PhotoImage.Source = image;
                TestHooks.NoteState("viewer-decode", decodeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _ = GrowAsync(PhotoImage, null);
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
    /// Shows a video and plays it with the full transport controls of Windows. It starts where the player of the bubble
    /// stopped.
    /// </summary>
    /// <remarks>
    /// The bytes come from the memory of the media store or from a download. They play from memory, as media, as in the
    /// player of the bubble. See <see cref="VideoPlayback"/>. A video that does not load closes the viewer again, and
    /// the log gets only the error type. Escape or Close stops the video and releases the player.
    /// </remarks>
    /// <param name="part">The media part of the video.</param>
    /// <param name="messageId">The ID of the message, for the log and the actions.</param>
    /// <param name="name">The name that Narrator reads for the video: its caption, or "Video".</param>
    /// <param name="returnFocus">The video frame in the chat, which gets focus again on close.</param>
    /// <param name="returnState">The focus state of the clicked button, which a close restores.</param>
    /// <param name="start">Where to start playing.</param>
    /// <param name="play">True to play from <paramref name="start"/>, false to stay paused there.</param>
    /// <param name="frame">
    /// The still frame of the bubble, which grows out of the bubble while the video loads. The player shows when it
    /// plays. With no frame, this is null, and the player itself grows when it is in.
    /// </param>
    /// <param name="closed">
    /// Gets the position and the play state of the viewer when it closes. The player of the bubble then continues from
    /// there. It is null when nothing continues.
    /// </param>
    /// <returns>A task that ends when the video plays or the load fails.</returns>
    public async Task ShowVideoAsync(MediaPart part, string messageId, string name, Control returnFocus, FocusState returnState, TimeSpan start, bool play, ImageSource? frame, Action<TimeSpan, bool>? closed)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(returnFocus);

        Open(part, messageId, returnFocus, returnState, isVideo: true);
        _videoClosed = closed;

        // The still grows at once over the player, which stays clear until it plays.
        bool poster = frame is not null && Ui.AnimationsEnabled;
        _poster     = poster ? frame : null;
        ShowPoster(_poster);
        Task grown = poster ? GrowAsync(VideoPoster, null, "GrewStill") : Task.CompletedTask;
        try
        {
            if ((Application.Current as App)?.Media is not { } media)
            {
                throw new InvalidOperationException("No media store.");
            }

            byte[] bytes = await media.GetAsync(part, thumbnail: false, CancellationToken.None);
            if (!ReferenceEquals(part, _part) || _closing)
            {
                return;
            }

            VideoPlayback video = new(XamlHost.Dispatcher, bytes, part.Mime, start, "PhotoViewer.Video", name);
            _video              = video;
            VideoHost.Children.Add(video.Element);
            video.Player.MediaOpened += (_, _) => XamlHost.Dispatcher.TryEnqueue(() => FitVideo(video));
            if (!poster)
            {
                _ = GrowAsync(VideoHost, part);
            }

            await grown;
            if (!ReferenceEquals(video, _video) || _closing)
            {
                return;
            }

            // The player replaces the still when its picture moves.
            video.Player.PlaybackSession.PlaybackStateChanged += (session, _) =>
            {
                if (session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
                {
                    XamlHost.Dispatcher.TryEnqueue(() =>
                    {
                        if (ReferenceEquals(video, _video) && !_closing && VideoPoster.Source is not null)
                        {
                            ShowPoster(null);
                            TestHooks.NoteState("viewer-motion", "Player");
                        }
                    });
                }
            };

            // A video that was paused in the bubble stays paused here, and the player shows where it stopped.
            if (play)
            {
                video.Player.Play();
            }
            else
            {
                ShowPoster(null);
            }

            XamlHost.Dispatcher.TryEnqueue(Windows.System.DispatcherQueuePriority.Low, () => _video?.Element.Focus(FocusState.Programmatic));
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
    /// <remarks>
    /// An animated close comes from Escape, Close or a click beside the photo. The photo then shrinks back into its
    /// bubble while the backdrop and the toolbar fade out. The viewer hides when that ends, and a video pauses at once.
    /// The viewer hides at once when the animation effects of Windows are off or the bubble is gone. The bubble is gone
    /// after a scroll or a chat switch. The viewer also hides at once when the caller does not ask for the animation.
    /// </remarks>
    /// <param name="animate">True to shrink back into the bubble first.</param>
    public void Close(bool animate = false)
    {
        if (!IsOpen || _closing)
        {
            return;
        }

        // The close notes the position and the play state of the video before the shrink pauses it. FinishClose gives
        // them back.
        _closedAt = _video is { } video ? (video.Position, video.IsPlaying) : null;
        if (animate && Shrink())
        {
            return;
        }

        FinishClose();
    }

    // Hides the viewer now and restores the animated layers.
    private void FinishClose()
    {
        _closeRun++;
        _closing         = false;
        IsHitTestVisible = true;
        ResetLayers();
        Visibility        = Visibility.Collapsed;
        PhotoImage.Source = null;
        _part             = null;
        StopVideo();
        _returnFocus?.Focus(_returnState);
        _returnFocus = null;
        IsOpenChanged?.Invoke(this, EventArgs.Empty);

        // The player of the bubble continues from where this player stopped.
        Action<TimeSpan, bool>? closed = _videoClosed;
        _videoClosed                   = null;
        if (closed is not null && _closedAt is { } at)
        {
            closed(at.Position, at.Playing);
        }

        _closedAt = null;
    }

    // Shows the viewer for a photo or a video. Any video that plays here or in a bubble stops, and the right layer
    // shows. For a video, Open in Photos becomes Open in default app.
    private void Open(MediaPart part, string messageId, Control returnFocus, FocusState returnState, bool isVideo)
    {
        StopVideo();
        MessageBubble.StopInlineVideo();
        _videoClosed = null;
        _part        = part;
        _messageId   = messageId;
        _returnFocus = returnFocus;
        _returnState = returnState == FocusState.Unfocused ? FocusState.Programmatic : returnState;

        string open = isVideo ? ChatCopy.OpenInDefaultApp : ChatCopy.OpenInPhotos;
        AutomationProperties.SetName(OpenInPhotosButton, open);
        ToolTipService.SetToolTip(OpenInPhotosButton, open);
        Zoomer.Visibility    = isVideo ? Visibility.Collapsed : Visibility.Visible;
        VideoHost.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;

        // An open during a shrink drops the shrink and restores the layers.
        if (_closing)
        {
            _closeRun++;
            _closing         = false;
            IsHitTestVisible = true;
            ResetLayers();
        }

        // The stage stays hidden until its photo or video is in, and then it grows. Meanwhile the backdrop and the
        // toolbar fade in.
        if (Ui.AnimationsEnabled)
        {
            ElementCompositionPreview.GetElementVisual(Stage).Opacity = 0;
            if (!IsOpen)
            {
                Fade(0, 1, GrowTime, Decelerate());
            }
        }

        if (!IsOpen)
        {
            Visibility = Visibility.Visible;
            IsOpenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // =========================================================================
    // GROW AND SHRINK
    // =========================================================================

    // The photo or video is in, so the stage shows. It grows from the place, size and crop of the bubble into its own.
    // See Morph. Without the bubble, or with animations off, it only shows. The task ends when the growth ends.
    private Task GrowAsync(FrameworkElement target, MediaPart? video, string grew = "Grew")
    {
        if (!Ui.AnimationsEnabled)
        {
            return Task.CompletedTask;
        }

        Visual visual  = ElementCompositionPreview.GetElementVisual(Stage);
        visual.Opacity = 1;
        Stage.UpdateLayout();
        if (BubbleRect() is not { } from || ShownRect(target, video, from) is not { } to)
        {
            TestHooks.NoteState("viewer-motion", "Shown");
            return Task.CompletedTask;
        }

        var ended                   = new TaskCompletionSource();
        CompositionScopedBatch done = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Morph(visual, from, to, growing: true, GrowTime, Decelerate());
        done.End();
        done.Completed += (_, _) => XamlHost.Dispatcher.TryEnqueue(() => ended.TrySetResult());
        TestHooks.NoteState("viewer-motion", grew);
        return ended.Task;
    }

    // Shows the still of the video over its player, which is clear meanwhile. With no still, the player shows.
    private void ShowPoster(ImageSource? frame)
    {
        VideoPoster.Source     = frame;
        VideoPoster.Visibility = frame is null ? Visibility.Collapsed : Visibility.Visible;
        VideoHost.Opacity      = frame is null ? 1 : 0;
    }

    // Shrinks the stage back into the bubble while the backdrop and the toolbar fade out, then hides the viewer.
    // Returns false and runs nothing when animations are off. It also returns false when the bubble or the shown photo
    // is not there.
    private bool Shrink()
    {
        FrameworkElement target = _video is null ? PhotoImage : VideoHost;
        if (!Ui.AnimationsEnabled || BubbleRect() is not { } to || ShownRect(target, _video is null ? null : _part, to) is not { } from)
        {
            return false;
        }

        _closing         = true;
        IsHitTestVisible = false;
        _video?.Player.Pause();

        // A video shrinks as its still over the paused player, as smoothly as it grew.
        if (_video is not null && _poster is { } still)
        {
            ShowPoster(still);
        }

        // The batch ends when every animation that started in it ends.
        int run                     = ++_closeRun;
        Visual visual               = ElementCompositionPreview.GetElementVisual(Stage);
        CompositionScopedBatch done = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Morph(visual, to, from, growing: false, ShrinkTime, Accelerate());
        Fade(1, 0, ShrinkTime, Accelerate());
        done.End();
        done.Completed += (_, _) => XamlHost.Dispatcher.TryEnqueue(() =>
        {
            if (run == _closeRun && _closing)
            {
                FinishClose();
            }
        });
        TestHooks.NoteState("viewer-motion", "Shrank");
        return true;
    }

    // Gives the photo or video frame of the bubble in the space of the stage. Returns null when the frame is gone or
    // not laid out.
    private Rect? BubbleRect()
    {
        if (_returnFocus is not { IsLoaded: true } bubble || bubble.ActualWidth <= 0 || bubble.ActualHeight <= 0 || !ReferenceEquals(bubble.XamlRoot, XamlRoot))
        {
            return null;
        }

        try
        {
            return bubble.TransformToVisual(Stage).TransformBounds(new Rect(0, 0, bubble.ActualWidth, bubble.ActualHeight));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    // Gives where the photo or video shows in the space of the stage. For a photo, that is the bounds of the image. For
    // a video, it is the host fitted to the shape of the bubble, because the player letterboxes it so. Returns null
    // before the target has a size.
    private Rect? ShownRect(FrameworkElement target, MediaPart? video, Rect bubble)
    {
        if (target.ActualWidth <= 0 || target.ActualHeight <= 0)
        {
            return null;
        }

        Rect box = target.TransformToVisual(Stage).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        if (video is null)
        {
            return box;
        }

        double aspect = video is { Width: > 0, Height: > 0 } ? (double)video.Width / video.Height : bubble.Width / bubble.Height;
        double width  = Math.Min(box.Width, box.Height * aspect);
        double height = width / aspect;
        return new Rect(box.X + ((box.Width - width) / 2), box.Y + ((box.Height - height) / 2), width, height);
    }

    // Moves the stage between the bubble and the shown photo with no distortion. A uniform scale about the middle of
    // the shown photo makes it just big enough to cover the bubble. The crop of the bubble does the same. A translation
    // moves it onto the middle of the bubble. An inset clip opens from the crop of the bubble out to the whole stage.
    // The clip is in the own space of the stage, before the scale. A grow runs from the bubble to the shown photo, and
    // a shrink runs back.
    private void Morph(Visual visual, Rect bubble, Rect shown, bool growing, TimeSpan time, CompositionEasingFunction ease)
    {
        Compositor compositor = visual.Compositor;
        var middle            = new Vector2((float)(shown.X + (shown.Width / 2)), (float)(shown.Y + (shown.Height / 2)));
        float scale           = (float)Math.Max(bubble.Width / shown.Width, bubble.Height / shown.Height);
        var shift             = new Vector3((float)(bubble.X + (bubble.Width / 2)) - middle.X, (float)(bubble.Y + (bubble.Height / 2)) - middle.Y, 0);

        // Before the scale, the crop of the bubble is the bubble size over the scale. It sits on the middle of the
        // shown photo.
        float cropWidth   = (float)bubble.Width / scale;
        float cropHeight  = (float)bubble.Height / scale;
        float stageWidth  = (float)Stage.ActualWidth;
        float stageHigh   = (float)Stage.ActualHeight;
        var insets        = new Vector4(
            Math.Max(0, middle.X - (cropWidth / 2)),
            Math.Max(0, middle.Y - (cropHeight / 2)),
            Math.Max(0, stageWidth - (middle.X + (cropWidth / 2))),
            Math.Max(0, stageHigh - (middle.Y + (cropHeight / 2))));

        ElementCompositionPreview.SetIsTranslationEnabled(Stage, true);
        visual.CenterPoint = new Vector3(middle, 0);
        InsetClip clip     = compositor.CreateInsetClip();
        visual.Clip        = clip;

        // Each property goes from its bubble value to its shown value, or back.
        Vector3KeyFrameAnimation size = compositor.CreateVector3KeyFrameAnimation();
        size.InsertKeyFrame(0, growing ? new Vector3(scale, scale, 1) : Vector3.One);
        size.InsertKeyFrame(1, growing ? Vector3.One : new Vector3(scale, scale, 1), ease);
        size.Duration = time;

        Vector3KeyFrameAnimation move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, growing ? shift : Vector3.Zero);
        move.InsertKeyFrame(1, growing ? Vector3.Zero : shift, ease);
        move.Duration = time;

        visual.StartAnimation(nameof(Visual.Scale), size);
        visual.StartAnimation("Translation", move);
        foreach ((string side, float inset) in (ReadOnlySpan<(string, float)>)[
            (nameof(InsetClip.LeftInset), insets.X), (nameof(InsetClip.TopInset), insets.Y),
            (nameof(InsetClip.RightInset), insets.Z), (nameof(InsetClip.BottomInset), insets.W)])
        {
            ScalarKeyFrameAnimation reveal = compositor.CreateScalarKeyFrameAnimation();
            reveal.InsertKeyFrame(0, growing ? inset : 0);
            reveal.InsertKeyFrame(1, growing ? 0 : inset, ease);
            reveal.Duration = time;
            clip.StartAnimation(side, reveal);
        }
    }

    private void Fade(float from, float to, TimeSpan time, CompositionEasingFunction ease)
    {
        foreach (UIElement layer in (ReadOnlySpan<UIElement>)[Backdrop, ToolbarPanel])
        {
            Visual visual                = ElementCompositionPreview.GetElementVisual(layer);
            ScalarKeyFrameAnimation fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, from);
            fade.InsertKeyFrame(1, to, ease);
            fade.Duration = time;
            visual.StartAnimation(nameof(Visual.Opacity), fade);
        }
    }

    // Puts the stage, the backdrop and the toolbar back at rest: shown, at full size and in place. With animations
    // off, the method changes nothing.
    private void ResetLayers()
    {
        if (!Ui.AnimationsEnabled)
        {
            return;
        }

        Visual stage = ElementCompositionPreview.GetElementVisual(Stage);
        stage.StopAnimation(nameof(Visual.Scale));
        stage.StopAnimation("Translation");
        stage.Scale   = Vector3.One;
        stage.Opacity = 1;
        stage.Clip    = null;
        stage.Properties.InsertVector3("Translation", Vector3.Zero);
        foreach (UIElement layer in (ReadOnlySpan<UIElement>)[Backdrop, ToolbarPanel])
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(layer);
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Opacity = 1;
        }
    }

    // The decelerate curve of Fluent is for things that arrive. The accelerate curve is for things that leave.
    private CubicBezierEasingFunction Decelerate() =>
        ElementCompositionPreview.GetElementVisual(Stage).Compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1));

    private CubicBezierEasingFunction Accelerate() =>
        ElementCompositionPreview.GetElementVisual(Stage).Compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0), new Vector2(1, 0.5f));

    // Stops the video of the viewer and releases its player and bytes.
    private void StopVideo()
    {
        _poster = null;
        ShowPoster(null);
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
            // A video pauses here while its default app plays it.
            _video?.Player.Pause();
            _ = MediaActions.OpenAsync(XamlRoot, part, _messageId);
        }
    }

    private void OnOpenWithClick(object sender, RoutedEventArgs e)
    {
        if (_part is { } part)
        {
            // A video pauses here while the picked app plays it.
            _video?.Player.Pause();
            _ = MediaActions.OpenWithAsync(part, _messageId);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close(animate: true);

    // =========================================================================
    // ZOOM
    // =========================================================================

    // Sizes the player to the picture of the video, fitted in its host. Thus its bar spans the picture as in the
    // bubble, not the empty bands beside it. The owner asked for this in Plan 11. Until the size of the video is known,
    // the player fills the host.
    private void FitVideo(VideoPlayback? video)
    {
        if (video is null || !ReferenceEquals(video, _video))
        {
            return;
        }

        double width  = video.Player.PlaybackSession.NaturalVideoWidth;
        double height = video.Player.PlaybackSession.NaturalVideoHeight;
        if (width <= 0 || height <= 0 || VideoHost.ActualWidth <= 0 || VideoHost.ActualHeight <= 0)
        {
            return;
        }

        double scale         = Math.Min(VideoHost.ActualWidth / width, VideoHost.ActualHeight / height);
        video.Element.Width  = Math.Floor(width * scale);
        video.Element.Height = Math.Floor(height * scale);
    }

    // At 1x the photo fits the viewport inside a 24 px margin. A smaller photo shows at its own size. A zoom scales it
    // from there. The image is only as big as the photo, so a click beside the photo is not on it.
    private void OnZoomerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PhotoImage.MaxWidth  = Math.Max(0, e.NewSize.Width - (PhotoMargin * 2));
        PhotoImage.MaxHeight = Math.Max(0, e.NewSize.Height - (PhotoMargin * 2));
    }

    // A double-click zooms to 2x at the pointer, or back to 1x after a zoom.
    private void OnPhotoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (Zoomer.ZoomFactor > 1.01f)
        {
            Zoomer.ChangeView(0, 0, 1);
            return;
        }

        Point at = e.GetPosition(PhotoImage);
        Zoomer.ChangeView((at.X * 2) - (Zoomer.ViewportWidth / 2), (at.Y * 2) - (Zoomer.ViewportHeight / 2), 2);
    }

    // A click beside the photo closes the viewer. A click on the photo does not, because its double-click zooms.
    private void OnZoomerTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PhotoImage))
        {
            Close(animate: true);
        }
    }

    private void OnZoomerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate)
        {
            return;
        }

        if (Zoomer.ZoomFactor > FullDecodeZoom)
        {
            _ = DecodeFullAsync();
        }

        TestHooks.ShowState(Zoomer, "Zoom" + Math.Round(Zoomer.ZoomFactor).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // Gives the decode width of a photo: its own width, no wider than the limit. The width is also narrow enough that
    // the height stays within MaxDecodeWidth. Else a 100 x 20000 photo would decode at full height. For an unknown
    // size, the method gives the limit.
    private static int CappedWidth(int width, int height, int limit)
    {
        if (width <= 0 || height <= 0)
        {
            return limit;
        }

        int heightCap = (int)Math.Min(int.MaxValue, (long)MaxDecodeWidth * width / height);
        return Math.Max(1, Math.Min(width, Math.Min(limit, heightCap)));
    }

    // Past 2x zoom, the photo decodes again once at its own width, so it stays sharp up close. Neither side goes past
    // MaxDecodeWidth.
    private async Task DecodeFullAsync()
    {
        int full = _photoWidth > 0 ? CappedWidth(_photoWidth, _photoHeight, MaxDecodeWidth) : 0;
        if (_part is not { } part || _closing || full <= _decodedWidth || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        _decodedWidth = full;
        try
        {
            byte[] bytes                = await media.GetAsync(part, thumbnail: false, CancellationToken.None);
            (ImageSource? image, _, _, _) = await Avatar.ToFittedImageAsync(bytes, (_, _) => full, DecodePixelType.Physical);
            if (image is not null && ReferenceEquals(part, _part) && !_closing)
            {
                PhotoImage.Source = image;
                TestHooks.NoteState("viewer-decode", full.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The sharper decode failed, so the first decode keeps showing.
            AppLog.MediaFailed(App.ShellLog, "view", _messageId, ex.GetType().Name);
        }
    }
}
