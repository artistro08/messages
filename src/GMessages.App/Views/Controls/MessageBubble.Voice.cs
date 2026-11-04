using GMessages.App.Media;
using GMessages.Presentation.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// The bubble's voice message: fetching its audio, its play button and waveform.
/// </summary>
/// <remarks>
/// Part of <see cref="MessageBubble"/>; the other members are in MessageBubble.xaml.cs.
/// </remarks>
public sealed partial class MessageBubble
{

    // =========================================================================
    // VOICE
    // =========================================================================

    // Another Voice Message: Shown Stopped, Then (When Asked To Fetch) Downloaded And Its Waveform And Length Read. The
    // Echo Of One Sent From Here Keeps Its Waveform And Play State, So A Playing Message Plays On
    private void ShowVoice(bool fetch)
    {
        MediaPart? part = Bubble is { HasVoice: true } bubble ? bubble.Media : null;
        if (SameMedia(part, _voicePart) && ReferenceEquals(Bubble, _voiceBubble))
        {
            FetchVoice(fetch);
            return;
        }

        if (part is not null && Bubble is not null && ReferenceEquals(Bubble, _voiceBubble) && KeepSentMedia(_voicePart, part))
        {
            _voicePart = part;
            FetchVoice(fetch);
            return;
        }

        _voicePart   = part;
        _voiceBubble = Bubble;
        _voiceKey    = Bubble is { } shown ? (shown.TmpId.Length > 0 ? shown.TmpId : shown.Id) : string.Empty;
        _voiceLoad++;
        _voiceWave   = null;

        // No Voice Message: Its Part Unloads (x:Load)
        if (part is null)
        {
            return;
        }

        FindName(nameof(Voice));
        VoiceLength.Text = string.Empty;
        VoiceBars.Show(null);
        VoiceBars.Length = TimeSpan.Zero;
        ShowVoicePlaying(false, 0);
        FetchVoice(fetch);
    }

    // The Shown Voice Message's Audio, Read For Its Waveform And Length Once Per Message Shown; The Audio Is Let Go After
    private async void FetchVoice(bool fetch)
    {
        if (!fetch || _voicePart is not { IsPending: false } part || _voiceWave is not null || _voiceFetched == _voiceLoad
            || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        int load      = _voiceLoad;
        _voiceFetched = load;
        try
        {
            VoiceWave wave = await WaveformReader.ReadAsync(await media.GetAsync(part, thumbnail: false, CancellationToken.None));
            if (load != _voiceLoad)
            {
                return;
            }

            _voiceWave       = wave;
            VoiceLength.Text = VoiceWave.LengthText(wave.Length);
            VoiceBars.Show(wave.Peaks);
            VoiceBars.Length = wave.Length;
            TestHooks.ShowState(VoiceLength, "Ready");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Another Message Since: Its Part May Be Gone
            if (load != _voiceLoad)
            {
                return;
            }

            AppLog.MediaFailed(App.ShellLog, "voice", Bubble?.Id ?? string.Empty, ex.GetType().Name);
            TestHooks.ShowState(VoiceLength, "Failed");
        }
    }

    // Play Or Pause: The Chat's Player Shows The State Back (See VoiceRequest)
    private void OnVoicePlayClick(object sender, RoutedEventArgs e) => RequestVoice(null);

    // A Click On The Waveform Seeks There
    private void OnVoiceSeekRequested(object? sender, double share) => RequestVoice(share);

    // The Audio Comes From The Media Store Again (Kept In Its Memory Cache Since The Waveform Was Read)
    private async void RequestVoice(double? seekTo)
    {
        if (_voicePart is not { IsPending: false } part || _voiceWave is not { } wave || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        // Only While This View Still Shows The Message (A Recycled Row Moves On; Its Echo Doesn't)
        int load = _voiceLoad;
        byte[] audio;
        try
        {
            audio = await media.GetAsync(part, thumbnail: false, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "voice", Bubble?.Id ?? string.Empty, ex.GetType().Name);
            return;
        }

        if (load != _voiceLoad)
        {
            return;
        }

        VoiceRequested?.Invoke(this, new VoiceRequest(_voiceKey, audio, wave.Length, seekTo, (playing, share) =>
        {
            if (load == _voiceLoad)
            {
                ShowVoicePlaying(playing, share);
            }
        }));
    }

    // The Play Button Becomes Pause While It Plays, And The Waveform Fills Up To How Much Has Played
    private void ShowVoicePlaying(bool playing, double share)
    {
        if (Voice is null)
        {
            return;
        }

        string name      = playing ? ChatCopy.Pause : ChatCopy.Play;
        VoiceGlyph.Glyph = playing ? "" : "";
        AutomationProperties.SetName(VoicePlay, name);
        ToolTipService.SetToolTip(VoicePlay, name);
        VoiceBars.ShowProgress(share, playing);
        TestHooks.ShowState(VoicePlay, playing ? "Playing" : "Paused");
    }
}
