using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using GMessages.App.Media;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace GMessages.App.Views.Controls;

/// <summary>
/// The in-app photo viewer: a clicked photo over the whole window, title bar row included, on a dark scrim (ruling R7-3:
/// an in-window overlay, not a separate window).
/// </summary>
/// <remarks>
/// <para>
/// The toolbar sits in the title bar row's top-left corner, Close first, then Open in Photos, Download and Save as
/// (owner, Plan 8). Close is a back arrow on a round, softly filled plate, unlike Windows' caption close, and the icons
/// dim while the window is inactive (<see cref="ShowActive"/>). The window lets clicks through to the toolbar there,
/// keeps the rest of the row dragging the window, and turns the caption buttons dark to read on the scrim, all while
/// <see cref="IsOpenChanged"/> says the viewer is open (see <c>MainWindow</c>).
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
/// Opening grows the photo or video out of its bubble (from the bubble's place and size to its own, 300 ms, Fluent's
/// decelerate curve) while the backdrop and toolbar fade in; closing by Escape, Close or a click beside the photo
/// shrinks it back (200 ms, accelerate) while they fade out (owner, Plan 8). A uniform Composition scale and a
/// translation on the stage that holds both, with an inset clip that opens from the bubble's crop to the whole photo,
/// so a cropped photo reveals itself undistorted and no layout moves. With Windows' animation effects off it shows and hides at once. A
/// video grows and shrinks as the bubble's still frame (a playing video's picture doesn't scale smoothly); its player
/// takes over once it plays. A test run notes <c>Grew</c>, <c>GrewStill</c>, <c>Player</c>, <c>Shown</c> (no bubble to
/// grow from) or <c>Shrank</c> in <c>viewer-motion.txt</c>.
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

    // The Toolbar's Gap From The Window's Left Edge
    private const double ToolbarInset = 8;

    // Opening Grows The Photo Out Of Its Bubble (Fluent's Decelerate Curve), Closing Shrinks It Back Faster (Accelerate)
    private static readonly TimeSpan GrowTime   = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ShrinkTime = TimeSpan.FromMilliseconds(200);
    private static readonly UISettings Ui       = new();

    // Bumped On Each Open And Close, So A Shrink That Finishes After The Viewer Opened Again Changes Nothing
    private int _closeRun;
    private bool _closing;

    // The Shown Photo, Its Message, And Where Focus Goes On Close
    private MediaPart? _part;
    private string _messageId = string.Empty;
    private Control? _returnFocus;

    // How The Photo In The Chat Had Focus When The Viewer Opened (Pointer For A Click), So Closing Gives It Back The Same
    // Way: A Clicked Photo Gets No Focus Ring Back, Even When Escape Closes The Viewer
    private FocusState _returnState = FocusState.Programmatic;

    // The Playing Video, While One Shows, And The Still It Grows And Shrinks As
    private VideoPlayback? _video;
    private ImageSource? _poster;

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
    /// Keeps the toolbar in the title bar row's top-left corner, 8 px in, past anything the system keeps on the left.
    /// </summary>
    /// <param name="leftInset">The width the system keeps on the row's left, in effective pixels (0 unless right to left).</param>
    public void FitToolbar(double leftInset)
    {
        var margin = new Thickness(leftInset + ToolbarInset, 0, 0, 0);
        if (ToolbarPanel.Margin != margin)
        {
            ToolbarPanel.Margin = margin;
        }
    }

    /// <summary>
    /// Dims the toolbar's icons while the window is inactive, as the title bar's text dims, and brings them back when
    /// it is active again. A test run puts <c>Active</c> or <c>Inactive</c> in Close's item status.
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
            if (ReferenceEquals(part, _part) && !_closing)
            {
                PhotoImage.Source = image;
                _ = Grow(PhotoImage, null);
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
    /// <param name="frame">The bubble's still frame, which grows out of the bubble while the video loads (the player
    /// shows once it plays); null when none showed, and the player itself then grows once it is in.</param>
    /// <returns>A task that ends once the video plays, or failed.</returns>
    public async Task ShowVideoAsync(MediaPart part, string messageId, string name, Control returnFocus, FocusState returnState, TimeSpan start, ImageSource? frame)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(returnFocus);

        Open(part, messageId, returnFocus, returnState, isVideo: true);

        // The Still Grows At Once, Over The Player, Which Stays Clear Until It Plays
        bool poster = frame is not null && Ui.AnimationsEnabled;
        _poster     = poster ? frame : null;
        ShowPoster(_poster);
        Task grown = poster ? Grow(VideoPoster, null, "GrewStill") : Task.CompletedTask;
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

            VideoPlayback video = new(DispatcherQueue, bytes, part.Mime, start, compact: false, "PhotoViewer.Video", name);
            _video              = video;
            VideoHost.Children.Add(video.Element);
            if (!poster)
            {
                _ = Grow(VideoHost, part);
            }

            await grown;
            if (!ReferenceEquals(video, _video) || _closing)
            {
                return;
            }

            // The Player Takes Over From The Still Once Its Picture Moves
            video.Player.PlaybackSession.PlaybackStateChanged += (session, _) =>
            {
                if (session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (ReferenceEquals(video, _video) && !_closing && VideoPoster.Source is not null)
                        {
                            ShowPoster(null);
                            TestHooks.NoteState("viewer-motion", "Player");
                        }
                    });
                }
            };
            video.Player.Play();
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
    /// <remarks>
    /// Animated (Escape, Close, a click beside the photo), the photo shrinks back into its bubble while the backdrop and
    /// toolbar fade out, and the viewer hides once that ends; a video pauses at once. It hides at once when Windows'
    /// animation effects are off, when the bubble is gone (scrolled away or another chat), or when asked to.
    /// </remarks>
    /// <param name="animate">True to shrink back into the bubble first.</param>
    public void Close(bool animate = false)
    {
        if (!IsOpen || _closing)
        {
            return;
        }

        if (animate && Shrink())
        {
            return;
        }

        FinishClose();
    }

    // Hides The Viewer Now, Putting The Animated Layers Back As They Were
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

        // Opened Again While Shrinking: The Shrink Is Dropped And The Layers Put Back
        if (_closing)
        {
            _closeRun++;
            _closing         = false;
            IsHitTestVisible = true;
            ResetLayers();
        }

        // The Stage Waits Unseen Until Its Photo Or Video Is In (Then It Grows), While The Backdrop And Toolbar Fade In
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

    // The Photo Or Video Is In: The Stage Shows, Growing From The Bubble's Place, Size And Crop Into Its Own (See Morph).
    // Without The Bubble, Or With Animations Off, It Just Shows. The Task Ends When The Growth Has
    private Task Grow(FrameworkElement target, MediaPart? video, string grew = "Grew")
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
        done.Completed += (_, _) => DispatcherQueue.TryEnqueue(() => ended.TrySetResult());
        TestHooks.NoteState("viewer-motion", grew);
        return ended.Task;
    }

    // The Video's Still Over Its Player (Which Is Clear Meanwhile), Or Gone With The Player Shown
    private void ShowPoster(ImageSource? frame)
    {
        VideoPoster.Source     = frame;
        VideoPoster.Visibility = frame is null ? Visibility.Collapsed : Visibility.Visible;
        VideoHost.Opacity      = frame is null ? 1 : 0;
    }

    // Shrinks The Stage Back Into The Bubble While The Backdrop And Toolbar Fade Out, Then Hides The Viewer. False (Nothing
    // Run) When Animations Are Off Or The Bubble Or The Shown Photo Isn't There To Shrink Between
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

        // A Video Shrinks As Its Still, Over The Paused Player, As Smoothly As It Grew
        if (_video is not null && _poster is { } still)
        {
            ShowPoster(still);
        }

        // The Batch Ends Once Every Animation Started Inside It Has
        int run                     = ++_closeRun;
        Visual visual               = ElementCompositionPreview.GetElementVisual(Stage);
        CompositionScopedBatch done = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Morph(visual, to, from, growing: false, ShrinkTime, Accelerate());
        Fade(1, 0, ShrinkTime, Accelerate());
        done.End();
        done.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (run == _closeRun && _closing)
            {
                FinishClose();
            }
        });
        TestHooks.NoteState("viewer-motion", "Shrank");
        return true;
    }

    // The Bubble's Photo Or Video Frame In The Stage's Space; Null When It's Gone Or Not Laid Out
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

    // Where The Photo Or Video Shows In The Stage's Space: The Image's Own Bounds, Or For A Video Its Host Fitted To The
    // Bubble's Shape (The Player Letterboxes It So). Null Before It Has A Size
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

    // Moves The Stage Between The Bubble And The Shown Photo Without Distorting It: One Uniform Scale About The Shown
    // Photo's Middle, Just Big Enough To Cover The Bubble (As The Bubble's Crop Does), A Translation Onto The Bubble's
    // Middle, And An Inset Clip From The Bubble's Crop (In The Stage's Own Space, Before The Scale) Out To The Whole
    // Stage. Growing Runs Bubble To Shown; Shrinking Runs It Back
    private void Morph(Visual visual, Rect bubble, Rect shown, bool growing, TimeSpan time, CompositionEasingFunction ease)
    {
        Compositor compositor = visual.Compositor;
        var middle            = new Vector2((float)(shown.X + (shown.Width / 2)), (float)(shown.Y + (shown.Height / 2)));
        float scale           = (float)Math.Max(bubble.Width / shown.Width, bubble.Height / shown.Height);
        var shift             = new Vector3((float)(bubble.X + (bubble.Width / 2)) - middle.X, (float)(bubble.Y + (bubble.Height / 2)) - middle.Y, 0);

        // The Bubble's Crop Before The Scale: The Bubble's Size Over The Scale, On The Shown Photo's Middle
        float crop_width  = (float)bubble.Width / scale;
        float crop_height = (float)bubble.Height / scale;
        float stage_width = (float)Stage.ActualWidth;
        float stage_high  = (float)Stage.ActualHeight;
        var insets        = new Vector4(
            Math.Max(0, middle.X - (crop_width / 2)),
            Math.Max(0, middle.Y - (crop_height / 2)),
            Math.Max(0, stage_width - (middle.X + (crop_width / 2))),
            Math.Max(0, stage_high - (middle.Y + (crop_height / 2))));

        ElementCompositionPreview.SetIsTranslationEnabled(Stage, true);
        visual.CenterPoint = new Vector3(middle, 0);
        InsetClip clip     = compositor.CreateInsetClip();
        visual.Clip        = clip;

        // Each Property From Its Bubble Value To Its Shown Value, Or Back
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

    // Fades The Backdrop And The Toolbar Together
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

    // Puts The Stage, Backdrop And Toolbar Back At Rest (Shown, Full Size, In Place); Untouched With Animations Off
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

    // Fluent's Decelerate (Things Arriving) And Accelerate (Things Leaving) Curves
    private CubicBezierEasingFunction Decelerate() =>
        ElementCompositionPreview.GetElementVisual(Stage).Compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1));

    private CubicBezierEasingFunction Accelerate() =>
        ElementCompositionPreview.GetElementVisual(Stage).Compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0), new Vector2(1, 0.5f));

    // Stops The Viewer's Video And Lets Go Of Its Player And Bytes
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
            // A Video Pauses Here While Its Default App Plays It
            _video?.Player.Pause();
            _ = MediaActions.OpenAsync(XamlRoot, part, _messageId);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close(animate: true);

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
            Close(animate: true);
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
