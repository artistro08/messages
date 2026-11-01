using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace GMessages.App.Media;

/// <summary>
/// One video playing from memory: a <see cref="MediaPlayer"/> over the decrypted bytes, shown in a
/// <see cref="MediaPlayerElement"/> with Windows' transport controls.
/// </summary>
/// <remarks>
/// <para>
/// Made the way <see cref="VoicePlayer"/> makes its player: the bytes play from memory (never from disk), as media (a
/// communications stream goes to the default communications device, often not the speakers), off the system media
/// controls, and muted in a test run. A bubble plays one in place with compact controls (play or pause, seek, volume);
/// the viewer plays one with the full controls. Windows' own full window and zoom buttons stay hidden: the bubble's
/// expand button and the viewer stand in for them.
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
    /// <param name="compact">True for a bubble's compact controls, false for the viewer's full ones.</param>
    /// <param name="automationId">The element's AutomationId.</param>
    /// <param name="name">What Narrator calls the element.</param>
    public VideoPlayback(DispatcherQueue queue, byte[] bytes, string mime, TimeSpan start, bool compact, string automationId, string name)
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
        Player.CommandManager.IsEnabled              = false;
        Player.MediaFailed                          += OnMediaFailed;
        Player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;

        // Element: Windows' Controls, Without Its Own Full Window And Zoom
        Element = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            Stretch                     = Stretch.Uniform,
            TransportControls           = new MediaTransportControls
            {
                IsCompact           = compact,
                IsZoomButtonVisible = false,
                IsZoomEnabled       = false,
            },
        };
        AutomationProperties.SetAutomationId(Element, automationId);
        AutomationProperties.SetName(Element, name);
        Element.SetMediaPlayer(Player);
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
