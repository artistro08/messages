using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
        Player  = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            IsMuted       = TestHooks.BlockLaunches,
            Source        = new MediaPlaybackItem(MediaSource.CreateFromStream(_stream, mime), start),
        };
        Player.MediaFailed                          += OnMediaFailed;

        // The Start Position Once Opened (The Item's Own Start Time Alone Left The Player At The Beginning)
        if (start > TimeSpan.Zero)
        {
            Player.MediaOpened += (sender, _) => sender.PlaybackSession.Position = start;
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

        // Cast And Full Window: WinUI 3's Controls Have No Property For Either, And Set The Template's Buttons Visible
        // Themselves (Collapsing Them Fought That And Hung The Window), So Once Made They Get No Width, No Input, No Tab
        // Stop And No Place In Narrator's Tree. The Compact Bar Then Ends At Volume, With No Empty Slot (Owner, Plan 10)
        controls.LayoutUpdated += HideExtras;

        void HideExtras(object? sender, object e)
        {
            if (FindNamed(controls, "CastButton") is not { } cast)
            {
                return;
            }

            // Full Window Isn't Always Made In The Compact Bar
            controls.LayoutUpdated -= HideExtras;
            FrameworkElement? full = FindNamed(controls, "FullWindowButton");
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

        _disposed                                    = true;
        Player.MediaFailed                          -= OnMediaFailed;
        Player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
        Player.Pause();
        Player.Dispose();
        _stream.Dispose();
    }

    // A Template Part By Its Name Only (No Type Check, So It Is AOT Safe)
    private static FrameworkElement? FindNamed(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var found_name } element && found_name == name)
            {
                return element;
            }

            if (FindNamed(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
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
        });
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
