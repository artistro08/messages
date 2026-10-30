using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace GMessages.App.Media;

/// <summary>
/// A request to play, pause or seek a voice message (a bubble's, or the recording's preview chip).
/// </summary>
/// <param name="Key">Names the message, so the player knows whether it is the one playing.</param>
/// <param name="Audio">The audio file's bytes.</param>
/// <param name="Length">How long it plays (from its waveform).</param>
/// <param name="SeekTo">Where to seek, from 0 to 1; null to play or pause.</param>
/// <param name="Show">Shows the message's state: whether it plays, and how much has played (0 to 1).</param>
public sealed record VoiceRequest(string Key, byte[] Audio, TimeSpan Length, double? SeekTo, Action<bool, double> Show);

/// <summary>
/// Plays voice messages for one chat pane: one <see cref="MediaPlayer"/>, so starting one message stops the other.
/// </summary>
/// <remarks>
/// <para>
/// The audio plays from memory (never from disk). While a message plays, its <see cref="VoiceRequest.Show"/> hears how
/// much has played ten times a second; when it pauses, ends or another message starts, it hears that too. The player
/// stays off the system media controls, so no message shows there. It plays as media, so it reaches the default
/// speakers (a communications stream goes to the default communications device instead). A test run plays muted, and
/// notes the player's real state for the tests (<see cref="TestHooks.NoteState"/>).
/// </para>
/// <para>
/// The pane disposes it on a chat switch and when it unloads, which stops the playing message and lets go of the
/// player and its audio.
/// </para>
/// </remarks>
internal sealed class VoicePlayer : IDisposable
{
    // How Often A Playing Message Hears How Far It Got
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _timer;
    private MediaPlayer? _player;
    private IRandomAccessStream? _stream;

    // Whether The Loaded Message Plays (Asked For: The Player's Own State Lags)
    private bool _playing;

    // The Message Loaded In The Player, Its Length And Who Shows Its State
    private string _key = string.Empty;
    private TimeSpan _length;
    private Action<bool, double>? _show;

    /// <summary>
    /// Creates the player; nothing loads until the first request.
    /// </summary>
    /// <param name="queue">The UI thread's queue, where every state change is shown.</param>
    public VoicePlayer(DispatcherQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        _queue          = queue;
        _timer          = queue.CreateTimer();
        _timer.Interval = Tick;
        _timer.Tick    += (_, _) => ShowState();
    }

    /// <summary>Gets the key of the message loaded in the player, or empty.</summary>
    public string Key => _key;

    /// <summary>
    /// Plays or pauses a message, or seeks in it: another message stops first.
    /// </summary>
    /// <remarks>A seek in a message not loaded starts it there; a seek in the loaded one keeps it playing or paused.</remarks>
    /// <param name="request">The request.</param>
    public void Handle(VoiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Another Message: Stop The Last, Load This One At The Seek
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
    /// Stops the playing message (it shows stopped) and unloads it.
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
            _player.Dispose();
            _player = null;
        }

        _stream?.Dispose();
        _stream = null;
        show?.Invoke(false, 0);
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();

    // A New Player Over The Audio In Memory, Starting At The Seek (Off The System Media Controls; Muted In A Test Run).
    // Media, Not Communications: Windows Sends A Communications Stream To The Default Communications Device (Often A
    // Headset Or A USB Microphone's Jack), So It Was Silent On The Speakers
    private void Load(VoiceRequest request)
    {
        _key    = request.Key;
        _length = request.Length;
        _show   = request.Show;
        _stream = new MemoryStream(request.Audio, writable: false).AsRandomAccessStream();

        var source = MediaSource.CreateFromStream(_stream, "audio/mp4");
        _player    = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            IsMuted       = TestHooks.BlockLaunches,
            Source        = new MediaPlaybackItem(source, _length * (request.SeekTo ?? 0)),
        };
        _player.CommandManager.IsEnabled = false;
        _player.MediaEnded              += OnMediaEnded;
        _player.MediaFailed             += OnMediaFailed;
    }

    // Raised Off The UI Thread: The Audio Couldn't Play
    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        TestHooks.NoteState("voice-failed", FormattableString.Invariant($"{args.Error} 0x{args.ExtendedErrorCode?.HResult ?? 0:X8}"));

    // Raised Off The UI Thread: Back To The Start, Shown Stopped
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

    // Plays Or Pauses The Loaded Message, Following It Ten Times A Second While It Plays
    private void Play(bool play)
    {
        _playing = play;
        if (play)
        {
            _player!.Play();
            _timer.Start();
        }
        else
        {
            _player!.Pause();
            _timer.Stop();
        }

        ShowState();
    }

    // Whether It Plays, And How Much Has Played
    private void ShowState()
    {
        if (_player is not { } player || _show is not { } show)
        {
            return;
        }

        // Test Run: The Player's Real State, Position And Length (Seconds)
        MediaPlaybackSession session = player.PlaybackSession;
        TestHooks.NoteState("voice-player", FormattableString.Invariant($"{session.PlaybackState} {session.Position.TotalSeconds:0.00} {session.NaturalDuration.TotalSeconds:0.00} {player.AudioCategory}"));

        double total = Duration().TotalSeconds;
        show(_playing, total > 0 ? player.PlaybackSession.Position.TotalSeconds / total : 0);
    }

    // The Player's Own Length Once Opened, Else The Waveform's
    private TimeSpan Duration() =>
        _player?.PlaybackSession.NaturalDuration is { Ticks: > 0 } natural ? natural : _length;
}
