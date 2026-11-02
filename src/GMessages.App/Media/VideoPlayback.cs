using GMessages.App.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using WinRT;

namespace GMessages.App.Media;

/// <summary>
/// One video playing from memory: a <see cref="MediaPlayer"/> over the decrypted bytes, shown in a
/// <see cref="MediaPlayerElement"/> with Windows' transport controls.
/// </summary>
/// <remarks>
/// <para>
/// Made the way <see cref="VoicePlayer"/> makes its player: the bytes play from memory (never from disk), as media (a
/// communications stream goes to the default communications device, often not the speakers), and muted in a test run.
/// Its command manager stays on: with it off, the play button of Windows' controls never played a paused video. The
/// bubble and the viewer show the same compact bar: play or pause, seek, volume. Zoom, cast and full window are hidden
/// (the bubble's expand and the viewer stand in for full window).
/// </para>
/// <para>
/// The owner puts <see cref="Element"/> in a panel, and on <see cref="Dispose"/> takes it out first, then disposes:
/// that stops the sound and lets go of the player and the bytes' stream. A test run puts the player's state in the
/// element's item status (<c>Playing</c>, <c>Paused</c>, ...) and notes it, with the audio category, in
/// <c>video-player.txt</c>; a failure goes in <c>video-failed.txt</c>.
/// </para>
/// </remarks>
internal sealed class VideoPlayback : IDisposable
{
    private readonly DispatcherQueue _queue;
    private readonly IRandomAccessStream _stream;
    private readonly MediaSource _source;

    // Hides Cast And Full Window Once The Controls Make Them; Held, So Dispose Can Take It Off If They Never Did
    private readonly EventHandler<object> _hideExtras;

    // Seeks To The Start Position Once Opened; Held, So Dispose Can Take It Off
    private readonly TypedEventHandler<MediaPlayer, object>? _seekToStart;

    // Hides The Controls While Playing, 1 s After Play Starts Or The Pointer Rests Or Leaves (Owner), Fading Over The
    // Time WinUI's Own Controls Take
    private static readonly TimeSpan HideAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FadeTime  = TimeSpan.FromMilliseconds(250);
    private readonly DispatcherQueueTimer _hideTimer;

    // The Bar's Panel Once Its Template Has Made It, And Whether The Bar Is Meant To Show
    private FrameworkElement? _panel;
    private bool _panelShown = true;

    // Keyboard (Or Gamepad) Focus Is In The Controls, Which Then Stay Shown; Set As Focus Arrives, Read Once It Has
    private bool _focusByKeys;
    private bool _keysInside;

    // Where The Pointer Last Was Over The Video, In Window Coordinates
    private Point _pointerAt = new(double.NaN, double.NaN);
    private bool _disposed;

    // Frames Drawn By The Owner: The Box They Fit, The Surface Each Is Copied To (Made At The Frame's Size), The Bitmap On
    // Show, And Whether One Is On Its Way (A Frame Arriving Meanwhile Is Skipped)
    private SizeInt32 _frameBox;
    private VideoFrame? _frame;
    private SoftwareBitmap? _shown;
    private int _drawing;

    /// <summary>
    /// Makes the player and its element, paused at the start position.
    /// </summary>
    /// <param name="queue">The UI thread's queue, where state changes are shown.</param>
    /// <param name="bytes">The video file's bytes.</param>
    /// <param name="mime">The video's mime type.</param>
    /// <param name="start">Where to start.</param>
    /// <param name="automationId">The element's AutomationId.</param>
    /// <param name="name">What Narrator calls the element.</param>
    /// <param name="drawFrames">
    /// True to hand the frames to the owner through <see cref="Frames"/> (the bubble, whose rounded corners the
    /// player's own picture ignores) instead of the element drawing them; see <see cref="SizeFrames"/>.
    /// </param>
    public VideoPlayback(DispatcherQueue queue, byte[] bytes, string mime, TimeSpan start, string automationId, string name, bool drawFrames = false)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(bytes);

        _queue  = queue;
        _stream = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
        _source = MediaSource.CreateFromStream(_stream, mime);
        Player  = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            IsMuted       = TestHooks.BlockLaunches,
            Source        = new MediaPlaybackItem(_source, start),
        };
        Player.MediaFailed                          += OnMediaFailed;

        // The Start Position Once Opened (The Item's Own Start Time Alone Left The Player At The Beginning)
        if (start > TimeSpan.Zero)
        {
            _seekToStart        = (sender, _) => sender.PlaybackSession.Position = start;
            Player.MediaOpened += _seekToStart;
        }

        Player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;

        // Element: Windows' Controls, Without Its Own Full Window, Zoom And Cast. The Element's Own Controls Are Set Up
        // In Place: A New MediaTransportControls Put In Their Stead Never Heard The Player's State, So Its Play Button
        // Stayed On "Play" While The Video Played And Only Ever Paused It
        Element = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            Stretch                     = Stretch.Uniform,
        };
        AutomationProperties.SetAutomationId(Element, automationId);
        AutomationProperties.SetName(Element, name);
        Element.SetMediaPlayer(Player);

        // Frames Drawn By The Owner: The Player Serves Each Frame Instead Of Showing It (Frame Server Mode), And The Owner
        // Shows It As A Picture Of Its Own, Which Rounds, Clips And Layers Like Any Other. After The Element Takes The
        // Player, Which Would Set It Up To Draw Again
        if (drawFrames)
        {
            Frames                           = new SoftwareBitmapSource();
            Element.Background               = null;
            Player.VideoFrameAvailable      += OnFrameAvailable;
            Player.IsVideoFrameServerEnabled = true;
        }

        // One Bar For The Bubble And The Viewer (Owner, Plan 10): Play Or Pause On The Left, The Seek Bar, Volume On The Right
        MediaTransportControls controls     = Element.TransportControls;
        controls.IsCompact                  = true;
        controls.IsZoomButtonVisible        = false;

        // Across The Whole Video, Inside The Bar's Own 12 px Inset: The Style's 296 px Minimum Ran Past A Narrow Bubble's
        // Right Edge, And Its 720 px Maximum Left A Wide Video's Bar Short (Owner, Plan 11)
        controls.MinWidth = 0;
        controls.MaxWidth = double.PositiveInfinity;

        // Cast And Full Window: WinUI 3's Controls Have No Property For Either, And Set The Template's Buttons Visible
        // Themselves (Collapsing Them Fought That And Hung The Window), So Once Made They Get No Width, No Input, No Tab
        // Stop And No Place In Narrator's Tree. The Compact Bar Then Ends At Volume, With No Empty Slot (Owner, Plan 10)
        _hideExtras             = HideExtras;
        controls.LayoutUpdated += _hideExtras;

        // Controls Hide 1 s After Play Starts Or The Pointer Rests Or Leaves; Moving The Pointer Over The Video Or Keyboard
        // Focus Brings Them Back, And They Stay While Paused. Driven Here Alone: Windows' Own Show And Hide Is Off
        controls.ShowAndHideAutomatically = false;
        _hideTimer             = queue.CreateTimer();
        _hideTimer.Interval    = HideAfter;
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick       += OnHideTick;
        Element.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(OnPointerActivity), handledEventsToo: true);
        Element.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerActivity), handledEventsToo: true);
        Element.PointerExited += OnPointerExited;
        Element.GettingFocus  += OnControlsGettingFocus;
        Element.GotFocus      += OnControlsGotFocus;
        Element.LostFocus     += OnControlsLostFocus;
    }

    /// <summary>
    /// Raised on the UI thread when the video can't play.
    /// </summary>
    public event EventHandler? Failed;

    /// <summary>Gets the player.</summary>
    public MediaPlayer Player { get; }

    /// <summary>Gets the element that shows the video and its controls (the controls alone when the owner draws the frames).</summary>
    public MediaPlayerElement Element { get; }

    /// <summary>
    /// Gets the picture the frames are drawn to when the owner draws them (see the constructor), else null. Show it with
    /// an image brush, stretched uniformly: each frame is the video's own shape, fitted to <see cref="SizeFrames"/>.
    /// </summary>
    public SoftwareBitmapSource? Frames { get; }

    /// <summary>
    /// Sets the box the frames are drawn to fit, in pixels (the shown size times the display scale), when the owner draws
    /// them. Until set, no frame is drawn.
    /// </summary>
    /// <param name="width">The box's width in pixels.</param>
    /// <param name="height">The box's height in pixels.</param>
    public void SizeFrames(int width, int height) => _frameBox = new SizeInt32(width, height);

    /// <summary>Gets how far the video has played.</summary>
    public TimeSpan Position => _disposed ? TimeSpan.Zero : Player.PlaybackSession.Position;

    /// <summary>Gets a value indicating whether the video is playing (or opening or buffering to play).</summary>
    public bool IsPlaying => !_disposed && Player.PlaybackSession.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Opening or MediaPlaybackState.Buffering;

    /// <summary>
    /// Stops the video and lets go of the player and the bytes. Take <see cref="Element"/> out of its panel first.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hideTimer.Stop();
        _hideTimer.Tick       -= OnHideTick;
        Element.PointerExited -= OnPointerExited;
        Element.GettingFocus  -= OnControlsGettingFocus;
        Element.GotFocus      -= OnControlsGotFocus;
        Element.LostFocus     -= OnControlsLostFocus;

        Element.TransportControls.LayoutUpdated     -= _hideExtras;
        Player.MediaFailed                          -= OnMediaFailed;
        Player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
        if (Frames is not null)
        {
            Player.VideoFrameAvailable -= OnFrameAvailable;
            _frame?.Dispose();
            _shown?.Dispose();
        }
        if (_seekToStart is not null)
        {
            Player.MediaOpened -= _seekToStart;
        }

        Player.Pause();

        // The Element Lets Go Of The Player, Then The Player, Its Item (Not Closable: Dropped As Its Source), Source And
        // Stream Go
        Element.SetMediaPlayer(null);
        Player.Source = null;
        Player.Dispose();
        _source.Dispose();
        _stream.Dispose();
    }

    // Cast And Full Window, Once The Controls Make Them: No Width, No Input, No Tab Stop, No Place In Narrator's Tree
    private void HideExtras(object? sender, object e)
    {
        MediaTransportControls controls = Element.TransportControls;
        if (UiHelpers.FindPart<FrameworkElement>(controls, "CastButton") is not { } cast)
        {
            return;
        }

        // Full Window Isn't Always Made In The Compact Bar
        controls.LayoutUpdated -= _hideExtras;
        FrameworkElement? full  = UiHelpers.FindPart<FrameworkElement>(controls, "FullWindowButton");

        // Frames Drawn By The Owner: The Element's Own Picture Goes (Serving Frames, It Still Painted An Opaque Backdrop
        // Over The Owner's Picture), Leaving The Controls
        if (Frames is not null && UiHelpers.FindPart<FrameworkElement>(Element, "MediaPlayerPresenter") is { } presenter)
        {
            presenter.Visibility = Visibility.Collapsed;
        }

        // The Bar Fades In And Out In Place (Owner): Its Own States Slide It Too. Faded Out It Takes No Clicks
        if (UiHelpers.FindPart<FrameworkElement>(controls, "ControlPanel_ControlPanelVisibilityStates_Border") is { } panel)
        {
            _panel                   = panel;
            panel.OpacityTransition  = new ScalarTransition { Duration = FadeTime };
            VisualStateManager.GoToState(controls, "ControlPanelFadeIn", useTransitions: false);
            ShowControls(_panelShown);
        }

        // The Seek Bar Shrinks To What A Narrow Bubble Leaves It: Its Style's 80 px Minimum Overran Its Slot, And Windows
        // Cut Its Thumb Off At The Slot's Edge (Owner Report)
        if (UiHelpers.FindPart<FrameworkElement>(controls, "ProgressSlider") is { } seek)
        {
            seek.MinWidth = 0;
        }

        foreach (FrameworkElement? button in (ReadOnlySpan<FrameworkElement?>)[cast, full])
        {
            if (button is null)
            {
                continue;
            }

            button.MaxWidth         = 0;
            button.Margin           = new Thickness(0);
            button.IsHitTestVisible = false;
            button.As<Control>().IsTabStop = false;
            AutomationProperties.SetAccessibilityView(button, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        }
    }

    // Raised Off The UI Thread: The State For The Tests
    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        MediaPlaybackState state = sender.PlaybackState;
        _queue.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            TestHooks.ShowState(Element, state.ToString());
            TestHooks.NoteState("video-player", FormattableString.Invariant($"{state} {Player.AudioCategory}"));

            // Playing: The Controls Hide In 1 s. Paused Or Ended: Shown, Nothing Pending
            if (state == MediaPlaybackState.Playing)
            {
                RestartHide();
                return;
            }

            _hideTimer.Stop();
            ShowControls(true);
        });
    }

    // =========================================================================
    // CONTROLS HIDING
    // =========================================================================

    // The Pointer Over The Video, Arriving Or Moving: The Controls Show, And Hide Again 1 s After It Rests. Only A Real
    // Move Counts: The Bar Fading Out Changes What Is Under A Still Pointer, And Windows Reports That As A Move, Which
    // Brought The Bar Straight Back
    private void OnPointerActivity(object sender, PointerRoutedEventArgs e)
    {
        Point at = e.GetCurrentPoint(null).Position;
        if (at == _pointerAt)
        {
            return;
        }

        _pointerAt = at;
        ShowControls(true);
        RestartHide();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => RestartHide();

    // Focus Arriving In The Controls: Remembered Whether It Came From The Keyboard Or A Gamepad (A Click's Doesn't Count)
    private void OnControlsGettingFocus(UIElement sender, GettingFocusEventArgs args) =>
        _focusByKeys = args.InputDevice is FocusInputDeviceKind.Keyboard or FocusInputDeviceKind.GameController;

    // Keyboard Focus In The Controls Keeps Them Shown Until It Leaves
    private void OnControlsGotFocus(object sender, RoutedEventArgs e)
    {
        if (!_focusByKeys)
        {
            return;
        }

        _keysInside = true;
        _hideTimer.Stop();
        ShowControls(true);
    }

    // Raised Before The Next Control's GotFocus, So Moving Between The Buttons Keeps Them Shown
    private void OnControlsLostFocus(object sender, RoutedEventArgs e)
    {
        _keysInside = false;
        RestartHide();
    }

    private void RestartHide()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    // Hides Only While Playing, With No Keyboard Focus In The Controls
    private void OnHideTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _keysInside || Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            return;
        }

        ShowControls(false);
    }

    // Fades The Bar In Or Out In Place (Its Opacity Transition Animates It); Faded Out It Takes No Clicks. Until The Bar's
    // Panel Is Made (See HideExtras), Only The Wish Is Kept. Setting What Is Already Set Does Nothing, So Each Pointer
    // Move Doesn't Restart The Fade In
    private void ShowControls(bool show)
    {
        _panelShown = show;
        if (_panel is { } panel)
        {
            panel.Opacity          = show ? 1 : 0;
            panel.IsHitTestVisible = show;
        }
    }

    // =========================================================================
    // FRAMES DRAWN BY THE OWNER
    // =========================================================================

    // Raised Off The UI Thread For Each Frame: Drawn On The UI Thread, One At A Time (A Frame Arriving While One Is Still
    // Being Drawn Is Skipped; The Next Catches Up)
    private void OnFrameAvailable(MediaPlayer sender, object args)
    {
        if (_disposed || Interlocked.CompareExchange(ref _drawing, 1, 0) != 0)
        {
            return;
        }

        if (!_queue.TryEnqueue(() => _ = DrawFrameAsync()))
        {
            _drawing = 0;
        }
    }

    // Copies The Current Frame To A Surface The Video's Own Shape, Fitted To The Box, Then Into The Picture On Show. A
    // Copy That Fails Is Noted And Skipped (The Next Frame Draws)
    private async Task DrawFrameAsync()
    {
        try
        {
            if (_disposed || Frames is null || FrameSize() is not { Width: > 0, Height: > 0 } size)
            {
                return;
            }

            if (_frame is null || _frame.Direct3DSurface.Description.Width != size.Width || _frame.Direct3DSurface.Description.Height != size.Height)
            {
                _frame?.Dispose();
                _frame = VideoFrame.CreateAsDirect3D11SurfaceBacked(DirectXPixelFormat.B8G8R8A8UIntNormalized, size.Width, size.Height);
            }

            Player.CopyFrameToVideoSurface(_frame.Direct3DSurface);
            // No Alpha: A Video Frame Copied To The Surface Leaves It At Zero, Which Premultiplied Shows As Nothing
            SoftwareBitmap bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(_frame.Direct3DSurface, BitmapAlphaMode.Ignore);
            if (_disposed)
            {
                bitmap.Dispose();
                return;
            }

            await Frames.SetBitmapAsync(bitmap);
            _shown?.Dispose();
            _shown = bitmap;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "frame", AutomationProperties.GetAutomationId(Element), FormattableString.Invariant($"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}"));
        }
        finally
        {
            _drawing = 0;
        }
    }

    // The Frame's Size In Pixels: The Video's Own Shape (Once Opened; The Box's Until Then) Fitted Inside The Box
    private SizeInt32 FrameSize()
    {
        uint naturalWidth  = Player.PlaybackSession.NaturalVideoWidth;
        uint naturalHeight = Player.PlaybackSession.NaturalVideoHeight;
        if (_frameBox.Width <= 0 || _frameBox.Height <= 0 || naturalWidth == 0 || naturalHeight == 0)
        {
            return _frameBox;
        }

        double scale = Math.Min((double)_frameBox.Width / naturalWidth, (double)_frameBox.Height / naturalHeight);
        return new SizeInt32(Math.Max(1, (int)Math.Round(naturalWidth * scale)), Math.Max(1, (int)Math.Round(naturalHeight * scale)));
    }

    // Raised Off The UI Thread: The Video Couldn't Play (The Error Kind Only Is Noted)
    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        TestHooks.NoteState("video-failed", FormattableString.Invariant($"{args.Error} 0x{args.ExtendedErrorCode?.HResult ?? 0:X8}"));
        _queue.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                Failed?.Invoke(this, EventArgs.Empty);
            }
        });
    }
}
