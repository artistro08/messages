using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.System;

namespace GMessages.App.Media;

/// <summary>
/// Holds a request to play, pause or seek a voice message. The message is in a bubble or in the recording preview.
/// </summary>
/// <param name="Key">The name of the message, so that the player knows whether this message plays now.</param>
/// <param name="Audio">The bytes of the audio file.</param>
/// <param name="Length">The length of the message, from its waveform.</param>
/// <param name="SeekTo">Where to seek, from 0 to 1, or null to play or pause.</param>
/// <param name="Show">Shows whether the message plays, and how much of it has played, from 0 to 1.</param>
public sealed record VoiceRequest(string Key, byte[] Audio, TimeSpan Length, double? SeekTo, Action<bool, double> Show);

/// <summary>
/// Plays the voice messages of a chat pane with 1 <see cref="MediaPlayer"/>, so a message that starts stops the last
/// message.
/// </summary>
/// <remarks>
/// <para>
/// The audio plays from memory, never from disk. While a message plays, its <see cref="VoiceRequest.Show"/> gets the
/// played share 10 times a second. It also gets a pause, the end and the start of another message. The player stays
/// off the system media controls, so no message shows there. It plays as media, so the sound reaches the default
/// speakers. A communications stream goes to the default communications device instead. A test run plays muted and
/// notes the real state of the player for the tests. See <see cref="TestHooks.NoteState"/>.
/// </para>
/// <para>
/// The pane disposes the player on a chat switch and when the pane unloads. That stops the message that plays and
/// releases the player and its audio.
/// </para>
/// </remarks>
internal sealed class VoicePlayer : IDisposable
{
    // How often a message that plays gets its played share.
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _timer;
    private MediaPlayer? _player;
    private MediaSource? _source;
    private IRandomAccessStream? _stream;

    // The play state that the request asked for. The state of the player itself lags.
    private bool _playing;

    private string _key = string.Empty;
    private TimeSpan _length;
    private Action<bool, double>? _show;

    /// <summary>
    /// Creates the player. Nothing loads before the first request.
    /// </summary>
    /// <param name="queue">The queue of the UI thread, where every state change shows.</param>
    public VoicePlayer(DispatcherQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        _queue          = queue;
        _timer          = queue.CreateTimer();
        _timer.Interval = Tick;
        _timer.Tick    += (_, _) => ShowState();
    }

    /// <summary>Gets the key of the message in the player, or an empty string.</summary>
    public string Key => _key;

    /// <summary>
    /// Plays or pauses a message, or seeks in it. Another message that plays stops first.
    /// </summary>
    /// <remarks>
    /// A seek in a message that is not loaded starts it there. A seek in the loaded message keeps its play or pause
    /// state.
    /// </remarks>
    /// <param name="request">The request.</param>
    public void Handle(VoiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Key != _key || _player is null)
        {
            Stop();
            Load(request);
            Play(true);
            return;
        }

        _show = request.Show;
        MediaPlaybackSession session = _player.PlaybackSession;
        if (request.SeekTo is { } share)
        {
            session.Position = Duration() * share;
        }
        else
        {
            Play(!_playing);
            return;
        }

        ShowState();
    }

    /// <summary>
    /// Stops the message that plays, shows it stopped and unloads it.
    /// </summary>
    public void Stop()
    {
        _timer.Stop();
        Action<bool, double>? show = _show;
        _show    = null;
        _key     = string.Empty;
        _playing = false;

        if (_player is not null)
        {
            _player.MediaEnded  -= OnMediaEnded;
            _player.MediaFailed -= OnMediaFailed;
            _player.Source = null;
            _player.Dispose();
            _player = null;
        }

        _source?.Dispose();
        _source = null;
        _stream?.Dispose();
        _stream = null;
        show?.Invoke(false, 0);
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();

    // The command manager is off, so the player stays off the system media controls. The audio category is Media, not
    // Communications. Windows sends a communications stream to the default communications device. That device is
    // often a headset or the jack of a USB microphone. Thus the message was silent on the speakers.
    private void Load(VoiceRequest request)
    {
        _key    = request.Key;
        _length = request.Length;
        _show   = request.Show;
        _stream = new MemoryStream(request.Audio, writable: false).AsRandomAccessStream();

        _source = MediaSource.CreateFromStream(_stream, "audio/mp4");
        _player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            IsMuted       = TestHooks.BlockLaunches,
            Source        = new MediaPlaybackItem(_source, _length * (request.SeekTo ?? 0)),
        };
        _player.CommandManager.IsEnabled = false;
        _player.MediaEnded              += OnMediaEnded;
        _player.MediaFailed             += OnMediaFailed;
    }

    // Windows raises this event off the UI thread when the audio cannot play.
    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        TestHooks.NoteState("voice-failed", FormattableString.Invariant($"{args.Error} 0x{args.ExtendedErrorCode?.HResult ?? 0:X8}"));

    // Windows raises this event off the UI thread. The message returns to the start and shows stopped.
    private void OnMediaEnded(MediaPlayer sender, object args) => _queue.TryEnqueue(() =>
    {
        if (!ReferenceEquals(sender, _player))
        {
            return;
        }

        _timer.Stop();
        _playing = false;
        sender.Pause();
        sender.PlaybackSession.Position = TimeSpan.Zero;
        _show?.Invoke(false, 0);
    });

    private void Play(bool play)
    {
        if (_player is not { } player)
        {
            return;
        }

        _playing = play;
        if (play)
        {
            player.Play();
            _timer.Start();
        }
        else
        {
            player.Pause();
            _timer.Stop();
        }

        ShowState();
    }

    private void ShowState()
    {
        if (_player is not { } player || _show is not { } show)
        {
            return;
        }

#if EnableTestHooks
        // A test run notes the real state, position and length of the player in seconds. Other runs never build this
        // string 10 times a second.
        MediaPlaybackSession session = player.PlaybackSession;
        TestHooks.NoteState("voice-player", FormattableString.Invariant($"{session.PlaybackState} {session.Position.TotalSeconds:0.00} {session.NaturalDuration.TotalSeconds:0.00} {player.AudioCategory}"));
#endif

        double total = Duration().TotalSeconds;
        show(_playing, total > 0 ? player.PlaybackSession.Position.TotalSeconds / total : 0);
    }

    // The length of the player after it opens, else the length of the waveform.
    private TimeSpan Duration() =>
        _player?.PlaybackSession.NaturalDuration is { Ticks: > 0 } natural ? natural : _length;
}
