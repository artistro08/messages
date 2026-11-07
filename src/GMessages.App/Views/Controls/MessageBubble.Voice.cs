using GMessages.App.Media;
using GMessages.Presentation.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows the voice message of the bubble with its play button and waveform, and fetches its audio.
/// </summary>
/// <remarks>
/// This file is a part of <see cref="MessageBubble"/>. The other members are in MessageBubble.xaml.cs.
/// </remarks>
public sealed partial class MessageBubble
{
    // A new voice message shows stopped. When the caller asks for the fetch, the audio downloads for its waveform and
    // length. The echo of a message sent from here keeps its waveform and play state. Thus a message that plays
    // continues.
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

        // With no voice message, x:Load unloads its part.
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

    // Reads the audio of the shown message for its waveform and length, once for each shown message. The method keeps
    // no audio.
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
            // Another message shows now, and its part can be gone.
            if (load != _voiceLoad)
            {
                return;
            }

            AppLog.MediaFailed(App.ShellLog, "voice", Bubble?.Id ?? string.Empty, ex.GetType().Name);
            TestHooks.ShowState(VoiceLength, "Failed");
        }
    }

    // The voice player of the chat shows the play state back through VoiceRequest.
    private void OnVoicePlayClick(object sender, RoutedEventArgs e) => RequestVoice(null);

    private void OnVoiceSeekRequested(object? sender, double share) => RequestVoice(share);

    // The audio comes from the media store again. The store keeps it in its memory cache after the waveform read.
    private async void RequestVoice(double? seekTo)
    {
        if (_voicePart is not { IsPending: false } part || _voiceWave is not { } wave || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        // The request goes out only while this view shows the same message. A recycled row moves to another message,
        // but an echo does not.
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

    // While the message plays, the play button becomes Pause. The waveform fills to the played share.
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
