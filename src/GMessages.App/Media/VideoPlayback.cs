using GMessages.App.Views;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using WinRT;

namespace GMessages.App.Media;

/// <summary>
/// Plays 1 video from memory: a <see cref="MediaPlayer"/> over the decrypted bytes, shown in a
/// <see cref="MediaPlayerElement"/> with the transport controls of Windows.
/// </summary>
/// <remarks>
/// <para>
/// The class makes its player in the way that <see cref="VoicePlayer"/> does. The bytes play from memory, never from
/// disk. They play as media, because a communications stream goes to the default communications device. That device is
/// often not the speakers. A test run plays muted.
/// </para>
/// <para>
/// The command manager stays on. With it off, the play button of the Windows controls never played a paused video.
/// The bubble and the viewer show the same compact bar: play or pause, seek and volume. Zoom, cast and full window are
/// hidden. The expand button of the bubble and the viewer take the place of full window.
/// </para>
/// <para>
/// The owner puts <see cref="Element"/> in a panel. Before <see cref="Dispose"/>, the owner must take it out of the
/// panel. The dispose stops the sound and releases the player and the stream of the bytes. A test run puts the player
/// state in the item status of the element, such as <c>Playing</c> or <c>Paused</c>. The test run also notes the state
/// and the audio category in <c>video-player.txt</c>, and a failure in <c>video-failed.txt</c>.
/// </para>
/// </remarks>
internal sealed class VideoPlayback : IDisposable
{
    private readonly DispatcherQueue _queue;
    private readonly IRandomAccessStream _stream;
    private readonly MediaSource _source;

    // Hides cast and full window when the controls make them. The field holds the handler, so that Dispose can remove
    // it when the controls never made them.
    private readonly EventHandler<object> _hideExtras;

    // Seeks to the start position when the video opens. The field holds the handler, so that Dispose can remove it.
    private readonly TypedEventHandler<MediaPlayer, object>? _seekToStart;

    // While the video plays, the controls hide 1 s after play starts or the pointer rests or leaves. The owner asked
    // for this. The fade takes the control animation time of WinUI.
    private static readonly TimeSpan HideAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FadeTime  = TimeSpan.FromMilliseconds(250);
    private readonly DispatcherQueueTimer _hideTimer;

    // The panel of the bar, after its template makes it, and whether the bar must show.
    private FrameworkElement? _panel;
    private bool _panelShown = true;

    // Keyboard or gamepad focus in the controls keeps them shown. GettingFocus sets the first flag, and GotFocus reads
    // it.
    private bool _focusByKeys;
    private bool _keysInside;

    // The last pointer position over the video, in window coordinates.
    private Point _pointerAt = new(double.NaN, double.NaN);
    private bool _disposed;

    // The fields hold the box that the drawn frames fit and the surface of each copy. The surface has the frame size.
    // Then come the bitmap on show and a flag that is 1 while a frame draws.
    private SizeInt32 _frameBox;
    private VideoFrame? _frame;
    private SoftwareBitmap? _shown;
    private int _drawing;

    /// <summary>
    /// Makes the player and its element, paused at the start position.
    /// </summary>
    /// <param name="queue">The queue of the UI thread, where state changes show.</param>
    /// <param name="bytes">The bytes of the video file.</param>
    /// <param name="mime">The MIME type of the video.</param>
    /// <param name="start">Where to start.</param>
    /// <param name="automationId">The AutomationId of the element.</param>
    /// <param name="name">The name that Narrator reads for the element.</param>
    /// <param name="drawFrames">
    /// True to give the frames to the owner through <see cref="Frames"/>, and the element then does not draw them. The
    /// bubble uses this, because the own picture of the player ignores rounded corners. See <see cref="SizeFrames"/>.
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

        // The player seeks to the start when the video opens. The start time of the item alone left the player at the
        // beginning.
        if (start > TimeSpan.Zero)
        {
            _seekToStart        = (sender, _) => sender.PlaybackSession.Position = start;
            Player.MediaOpened += _seekToStart;
        }

        Player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;

        // The code configures the own controls of the element in place. A new MediaTransportControls in their place
        // never got the player state. Its play button stayed on "Play" while the video played and only paused it.
        Element = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            Stretch                     = Stretch.Uniform,
        };
        AutomationProperties.SetAutomationId(Element, automationId);
        AutomationProperties.SetName(Element, name);
        Element.SetMediaPlayer(Player);

        // In frame server mode, the player serves each frame and does not show it. The owner shows the frame as its own
        // picture, which rounds, clips and layers as any other picture does. This must come after the element takes the
        // player, because the element sets the player up to draw again.
        if (drawFrames)
        {
            Frames                           = new SoftwareBitmapSource();
            Element.Background               = null;
            Player.VideoFrameAvailable      += OnFrameAvailable;
            Player.IsVideoFrameServerEnabled = true;
        }

        // The bubble and the viewer share 1 bar, as the owner asked in Plan 10. Play or pause is on the left, then the
        // seek bar, and volume is on the right.
        MediaTransportControls controls     = Element.TransportControls;
        controls.IsCompact                  = true;
        controls.IsZoomButtonVisible        = false;

        // The bar spans the whole video inside its own 12 px inset, as the owner asked in Plan 11. The 296 px minimum
        // of the style ran past the right edge of a narrow bubble. Its 720 px maximum left a short bar on a wide video.
        controls.MinWidth = 0;
        controls.MaxWidth = double.PositiveInfinity;

        // The WinUI 3 controls had no property for cast or full window, and they set the template buttons visible
        // themselves. A collapse of the buttons fought that and hung the window. Thus each button gets no width, no
        // input, no tab stop and no place in the tree of Narrator. The compact bar then ends at volume, with no empty
        // slot, as the owner asked in Plan 10.
        _hideExtras             = HideExtras;
        controls.LayoutUpdated += _hideExtras;

        // The controls hide 1 s after play starts or the pointer rests or leaves. A pointer move over the video or
        // keyboard focus shows them again, and they stay while the video is paused. Only this class shows and hides
        // them, because the automatic show and hide of Windows is off.
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
    /// Occurs on the UI thread when the video cannot play.
    /// </summary>
    public event EventHandler? Failed;

    /// <summary>Gets the player.</summary>
    public MediaPlayer Player { get; }

    /// <summary>
    /// Gets the element that shows the video and its controls. When the owner draws the frames, the element shows only
    /// the controls.
    /// </summary>
    public MediaPlayerElement Element { get; }

    /// <summary>
    /// Gets the picture that the frames draw to when the owner draws them, else null. Show it with an image brush that
    /// stretches uniformly. Each frame has the shape of the video, fitted to <see cref="SizeFrames"/>.
    /// </summary>
    public SoftwareBitmapSource? Frames { get; }

    /// <summary>
    /// Sets the box in pixels that the frames fit when the owner draws them. Until then, no frame draws.
    /// </summary>
    /// <param name="width">The width of the box in pixels, which is the shown width times the display scale.</param>
    /// <param name="height">The height of the box in pixels, which is the shown height times the display scale.</param>
    public void SizeFrames(int width, int height) => _frameBox = new SizeInt32(width, height);

    /// <summary>Gets how far the video has played.</summary>
    public TimeSpan Position => _disposed ? TimeSpan.Zero : Player.PlaybackSession.Position;

    /// <summary>Gets a value indicating whether the video plays, or opens or buffers to play.</summary>
    public bool IsPlaying => !_disposed && Player.PlaybackSession.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Opening or MediaPlaybackState.Buffering;

    /// <summary>
    /// Stops the video and releases the player and the bytes. Take <see cref="Element"/> out of its panel first.
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

        // The element releases the player first. Then the player, its source and the stream go. The item cannot close,
        // so the player drops it as its source.
        Element.SetMediaPlayer(null);
        Player.Source = null;
        Player.Dispose();
        _source.Dispose();
        _stream.Dispose();
    }

    // The controls make cast and full window late. Each then gets no width, no input, no tab stop and no place in the
    // tree of Narrator.
    private void HideExtras(object? sender, object e)
    {
        MediaTransportControls controls = Element.TransportControls;
        if (UiHelpers.FindPart<FrameworkElement>(controls, "CastButton") is not { } cast)
        {
            return;
        }

        // The compact bar does not always make full window.
        controls.LayoutUpdated -= _hideExtras;
        FrameworkElement? full  = UiHelpers.FindPart<FrameworkElement>(controls, "FullWindowButton");

        // When the owner draws the frames, the own picture of the element goes, so only the controls stay. While it
        // served frames, that picture still painted an opaque backdrop over the picture of the owner.
        if (Frames is not null && UiHelpers.FindPart<FrameworkElement>(Element, "MediaPlayerPresenter") is { } presenter)
        {
            presenter.Visibility = Visibility.Collapsed;
        }

        // The bar fades in and out in place, as the owner asked. Its own states slide it too. A faded bar takes no
        // clicks.
        if (UiHelpers.FindPart<FrameworkElement>(controls, "ControlPanel_ControlPanelVisibilityStates_Border") is { } panel)
        {
            _panel                   = panel;
            panel.OpacityTransition  = new ScalarTransition { Duration = FadeTime };
            VisualStateManager.GoToState(controls, "ControlPanelFadeIn", useTransitions: false);
            ShowControls(_panelShown);
        }

        // The seek bar shrinks to the room that a narrow bubble leaves it. The 80 px minimum of its style overran its
        // slot. Windows then cut its thumb at the edge of the slot, as the owner reported.
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
            AutomationProperties.SetAccessibilityView(button, Windows.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        }
    }

    // Windows raises this event off the UI thread. The state goes to the tests.
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

            // While the video plays, the controls hide in 1 s. A pause or the end shows them with no hide pending.
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

    // A pointer that arrives or moves over the video shows the controls. They hide again 1 s after the pointer rests.
    // Only a real move counts. The fade of the bar changes what is under a still pointer. Windows reports that as a
    // move, which showed the bar again at once.
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

    // Notes whether focus comes from the keyboard or a gamepad. Focus from a click does not count.
    private void OnControlsGettingFocus(UIElement sender, GettingFocusEventArgs args) =>
        _focusByKeys = args.InputDevice is FocusInputDeviceKind.Keyboard or FocusInputDeviceKind.GameController;

    // Keyboard focus in the controls keeps them shown until the focus leaves.
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

    // Windows raises LostFocus before the GotFocus of the next control. Thus a move between the buttons keeps them
    // shown.
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

    // The controls hide only while the video plays and no keyboard focus is in them.
    private void OnHideTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _keysInside || Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            return;
        }

        ShowControls(false);
    }

    // The opacity transition of the bar fades it in or out in place, and a faded bar takes no clicks. Until HideExtras
    // finds the panel of the bar, the method keeps only the wish. A set to the same value does nothing. Thus a pointer
    // move does not start the fade-in again.
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

    // Windows raises this event off the UI thread for each frame. The UI thread draws 1 frame at a time. The code skips
    // a frame that arrives while another frame draws, and the next frame shows the current picture.
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

    // Copies the current frame to a surface with the shape of the video, fitted to the box. Then the frame goes into
    // the picture on show. A copy that fails goes to the log, and the next frame draws.
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
            // The copy ignores alpha. A video frame copied to the surface leaves alpha at 0, and premultiplied alpha
            // then shows nothing.
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

    // The frame size in pixels: the shape of the video fitted inside the box. Before the video opens, the size of the
    // box.
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

    // Windows raises this event off the UI thread when the video cannot play. The note gets only the kind of error.
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
