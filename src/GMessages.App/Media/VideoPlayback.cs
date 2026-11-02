using GMessages.App.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.Foundation;
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

    // Hides The Controls While Playing, 1 s After Play Starts Or The Pointer Rests Or Leaves (Owner)
    private static readonly TimeSpan HideAfter = TimeSpan.FromSeconds(1);
    private readonly DispatcherQueueTimer _hideTimer;

    // Keyboard (Or Gamepad) Focus Is In The Controls, Which Then Stay Shown; Set As Focus Arrives, Read Once It Has
    private bool _focusByKeys;
    private bool _keysInside;

    // Where The Pointer Last Was Over The Video, In Window Coordinates
    private Point _pointerAt = new(double.NaN, double.NaN);
    private bool _disposed;

    /// <summary>
    /// Makes the player and its element, paused at the start position.
    /// </summary>
    /// <param name="queue">The UI thread's queue, where state changes are shown.</param>
    /// <param name="bytes">The video file's bytes.</param>
    /// <param name="mime">The video's mime type.</param>
    /// <param name="start">Where to start.</param>
    /// <param name="automationId">The element's AutomationId.</param>
    /// <param name="name">What Narrator calls the element.</param>
    public VideoPlayback(DispatcherQueue queue, byte[] bytes, string mime, TimeSpan start, string automationId, string name)
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

    /// <summary>Gets the element that shows the video and its controls.</summary>
    public MediaPlayerElement Element { get; }

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

    // Fades The Bar In Or Out With Its Template's Own States (Windows' Slide And Fade; Faded Out It Takes No Clicks). Hide
    // Itself Is Refused While Focus Sits In The Bar, Which A Click On Play Leaves There. Going To The State It Is Already
    // In Does Nothing, So Each Pointer Move Doesn't Restart The Fade In
    private void ShowControls(bool show) =>
        VisualStateManager.GoToState(Element.TransportControls, show ? "ControlPanelFadeIn" : "ControlPanelFadeOut", useTransitions: true);

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
