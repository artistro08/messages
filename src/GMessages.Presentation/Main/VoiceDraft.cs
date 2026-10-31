using System.ComponentModel;
using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Main;

/// <summary>
/// Where a voice message being made stands: nothing yet, recording, or recorded and waiting to be sent.
/// </summary>
public enum VoiceDraftState
{
    /// <summary>No voice message: the mic button shows.</summary>
    Idle,

    /// <summary>Recording: the compose area shows the timer, the live waveform, Cancel recording, Pause and Done.</summary>
    Recording,

    /// <summary>Paused: the recording bar stays, with its timer and waveform still, and Pause becomes Resume.</summary>
    Paused,

    /// <summary>Recorded: the preview chip shows over the message box, to play, send or remove.</summary>
    Preview,
}

/// <summary>
/// The chat's voice message being made: Idle, then Recording (paused and resumed any number of times), then Preview,
/// then Idle again once sent or removed.
/// </summary>
/// <remarks>
/// <para>
/// The recording goes straight to a temp file (in the media store's private temp folder). <see cref="Cancel"/> (from
/// any state) and <see cref="Take"/> (sending) delete it, so no recording outlives its use; the chat cancels on close.
/// A step from the wrong state does nothing and says so, so a late callback (a recorder that finished after Cancel)
/// can't bring a recording back.
/// </para>
/// <para>
/// When the microphone can't be used (access off, or no device), the chat pane cancels and shows its microphone
/// dialog. The audio and file path are never logged.
/// </para>
/// </remarks>
public sealed class VoiceDraft : INotifyPropertyChanged
{
    /// <summary>The file name a voice message is sent with, as the official app names it.</summary>
    public const string FileName = "Voice message.m4a";

    /// <summary>The mime type a voice message is sent with (LibGM's AUDIO_MP4).</summary>
    public const string Mime = "audio/mp4";

    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets where the voice message stands.</summary>
    public VoiceDraftState State { get; private set; }

    /// <summary>Gets whether nothing is recorded or recording (the mic button shows).</summary>
    public bool IsIdle => State == VoiceDraftState.Idle;

    /// <summary>Gets whether recording is under way.</summary>
    public bool IsRecording => State == VoiceDraftState.Recording;

    /// <summary>Gets whether recording is paused.</summary>
    public bool IsPaused => State == VoiceDraftState.Paused;

    /// <summary>Gets whether a recording is under way or paused (the recording bar shows).</summary>
    public bool IsCapturing => IsRecording || IsPaused;

    /// <summary>Gets whether a recording waits to be sent (the preview chip shows).</summary>
    public bool IsPreview => State == VoiceDraftState.Preview;

    /// <summary>Gets the temp file being recorded to, or empty when Idle.</summary>
    public string FilePath { get; private set; } = string.Empty;

    /// <summary>Gets the recorded audio while in Preview, else empty.</summary>
    public byte[] Audio { get; private set; } = [];

    /// <summary>Gets the recording's waveform and length while in Preview, else null.</summary>
    public VoiceWave? Wave { get; private set; }

    /// <summary>Gets the recording's length as the chip shows it, or empty.</summary>
    public string LengthText => Wave is null ? string.Empty : VoiceWave.LengthText(Wave.Length);

    /// <summary>
    /// Deletes a recording's temp file, if it is there; a file still held open stays (the caller deletes it again later).
    /// </summary>
    /// <param name="path">The file's path, or empty for none.</param>
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
    /// Starts recording to a temp file: Idle to Recording.
    /// </summary>
    /// <param name="path">The temp file the recording goes to.</param>
    /// <returns>False (nothing changes) unless Idle.</returns>
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
    /// Pauses recording: Recording to Paused. The file keeps what was recorded, and resuming adds to it.
    /// </summary>
    /// <returns>False (nothing changes) unless Recording.</returns>
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
    /// <returns>False (nothing changes) unless Paused.</returns>
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
    /// Ends recording with what was recorded: Recording or Paused to Preview.
    /// </summary>
    /// <param name="audio">The recorded file's bytes.</param>
    /// <param name="wave">Its waveform and length.</param>
    /// <returns>False (nothing changes) unless Recording or Paused.</returns>
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
    /// Drops the recording, from any state: back to Idle with its temp file deleted.
    /// </summary>
    public void Cancel()
    {
        if (IsIdle && FilePath.Length == 0)
        {
            return;
        }

        Reset();
        Move(VoiceDraftState.Idle);
    }

    /// <summary>
    /// Hands the recording over for sending: Preview to Idle, with its temp file deleted.
    /// </summary>
    /// <returns>The recorded audio, or null (nothing changes) unless in Preview.</returns>
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

    // Deletes The Temp File And Lets Go Of The Audio
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
