using System.ComponentModel;
using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Main;

/// <summary>
/// Tells the state of a new voice message: nothing yet, a recording, or a recording that waits for Send.
/// </summary>
public enum VoiceDraftState
{
    /// <summary>No voice message exists, and the mic button shows.</summary>
    Idle,

    /// <summary>
    /// The recording runs. The compose area shows the timer, the live waveform, Cancel recording, Pause and Done.
    /// </summary>
    Recording,

    /// <summary>
    /// The recording is paused. The recording bar stays with a still timer and waveform, and Pause becomes Resume.
    /// </summary>
    Paused,

    /// <summary>
    /// The recording is done. The preview chip shows over the message box, to play, send or remove it.
    /// </summary>
    Preview,
}

/// <summary>
/// Holds the new voice message of the chat. It goes from Idle to Recording, then to Preview, then to Idle after Send or
/// a removal.
/// </summary>
/// <remarks>
/// <para>
/// A recording can pause and resume any number of times. It goes directly to a temp file in the private temp folder of
/// the media store. <see cref="Cancel"/> from any state and <see cref="Take"/> for the send delete the file. Thus no
/// recording lives longer than its use, and the chat cancels on close. A step from the wrong state does nothing and
/// returns false. Thus a late callback cannot bring a recording back, such as a recorder that finished after Cancel.
/// </para>
/// <para>
/// When the chat cannot use the microphone, the chat pane cancels and shows its microphone dialog. The access can be
/// off, or no device exists. The log never gets the audio or the file path.
/// </para>
/// </remarks>
public sealed class VoiceDraft : INotifyPropertyChanged
{
    /// <summary>A voice message goes with this file name, as the official app names it.</summary>
    public const string FileName = "Voice message.m4a";

    /// <summary>A voice message goes with this mime type, the AUDIO_MP4 of LibGM.</summary>
    public const string Mime = "audio/mp4";

    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the state of the voice message.</summary>
    public VoiceDraftState State { get; private set; }

    /// <summary>Gets whether nothing is recorded or in a recording. The mic button then shows.</summary>
    public bool IsIdle => State == VoiceDraftState.Idle;

    /// <summary>Gets whether the recording runs.</summary>
    public bool IsRecording => State == VoiceDraftState.Recording;

    /// <summary>Gets whether the recording is paused.</summary>
    public bool IsPaused => State == VoiceDraftState.Paused;

    /// <summary>Gets whether the recording runs or is paused. The recording bar then shows.</summary>
    public bool IsCapturing => IsRecording || IsPaused;

    /// <summary>Gets whether a recording waits for Send. The preview chip then shows.</summary>
    public bool IsPreview => State == VoiceDraftState.Preview;

    /// <summary>Gets the temp file of the recording, or empty when Idle.</summary>
    public string FilePath { get; private set; } = string.Empty;

    /// <summary>Gets the recorded audio while in Preview, else empty.</summary>
    public byte[] Audio { get; private set; } = [];

    /// <summary>Gets the waveform and the length of the recording while in Preview, else null.</summary>
    public VoiceWave? Wave { get; private set; }

    /// <summary>Gets the length of the recording as the chip shows it, or empty.</summary>
    public string LengthText => Wave is null ? string.Empty : VoiceWave.LengthText(Wave.Length);

    /// <summary>
    /// Deletes the temp file of a recording when it exists. A file that is still open stays, and the caller deletes it
    /// again later.
    /// </summary>
    /// <param name="path">The path of the file, or empty for none.</param>
    public static void DeleteFile(string path)
    {
        if (path.Length == 0)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Starts the recording to a temp file: Idle to Recording.
    /// </summary>
    /// <param name="path">The temp file for the recording.</param>
    /// <returns>False, with no change, unless Idle.</returns>
    public bool Start(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!IsIdle)
        {
            return false;
        }

        FilePath = path;
        Move(VoiceDraftState.Recording);
        return true;
    }

    /// <summary>
    /// Pauses the recording: Recording to Paused. The file keeps the recorded audio, and <see cref="Resume"/> adds to
    /// it.
    /// </summary>
    /// <returns>False, with no change, unless Recording.</returns>
    public bool Pause()
    {
        if (!IsRecording)
        {
            return false;
        }

        Move(VoiceDraftState.Paused);
        return true;
    }

    /// <summary>
    /// Resumes a paused recording: Paused to Recording.
    /// </summary>
    /// <returns>False, with no change, unless Paused.</returns>
    public bool Resume()
    {
        if (!IsPaused)
        {
            return false;
        }

        Move(VoiceDraftState.Recording);
        return true;
    }

    /// <summary>
    /// Ends the recording with the recorded audio: Recording or Paused to Preview.
    /// </summary>
    /// <param name="audio">The bytes of the recorded file.</param>
    /// <param name="wave">The waveform and the length of the recording.</param>
    /// <returns>False, with no change, unless Recording or Paused.</returns>
    public bool Stop(byte[] audio, VoiceWave wave)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(wave);

        if (!IsCapturing)
        {
            return false;
        }

        Audio = audio;
        Wave  = wave;
        Move(VoiceDraftState.Preview);
        return true;
    }

    /// <summary>
    /// Drops the recording from any state. The state goes to Idle, and the call deletes the temp file.
    /// </summary>
    public void Cancel()
    {
        if (IsIdle)
        {
            return;
        }

        Reset();
        Move(VoiceDraftState.Idle);
    }

    /// <summary>
    /// Gives the recording to the send: Preview to Idle. The call deletes the temp file.
    /// </summary>
    /// <returns>The recorded audio, or null with no change unless in Preview.</returns>
    public byte[]? Take()
    {
        if (!IsPreview)
        {
            return null;
        }

        byte[] audio = Audio;
        Reset();
        Move(VoiceDraftState.Idle);
        return audio;
    }

    private void Reset()
    {
        DeleteFile(FilePath);
        FilePath = string.Empty;
        Audio    = [];
        Wave     = null;
    }

    private void Move(VoiceDraftState state)
    {
        State = state;
        PropertyChanged?.Invoke(this, AllChanged);
    }
}
