using GMessages.App.Media;
using GMessages.App.Views.Controls;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;

namespace GMessages.App.Views;

/// <summary>
/// Handles the recording of the compose area: the mic, the recording bar, pause and cancel, and the voice preview chip.
/// </summary>
/// <remarks>
/// This part of <see cref="ChatPane"/> holds the recording members. ChatPane.xaml.cs holds the other members.
/// </remarks>
public sealed partial class ChatPane
{
    // The mic records to a new temp file in the private folder of the media store. When access is off or no microphone
    // exists, the settings message shows. Any other failure goes back to the mic. A cancel or a chat switch during the
    // start drops the recorder.
    private async void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (Chat is not { Voice.IsIdle: true } chat || (Application.Current as App) is not { Media: { } media } app)
        {
            return;
        }

        // The bar shows at once, empty, while the microphone opens. It does not show the waveform and time of the last
        // recording.
        string path = System.IO.Path.Combine(media.TempFolder, "voice-" + Guid.NewGuid().ToString("N") + ".m4a");
        RecordBars.Show(null);
        RecordTimer.Text = VoiceWave.LengthText(TimeSpan.Zero);
        chat.Voice.Start(path);
        chat.ClearAttachError();
        _recordChat = chat;

        VoiceRecorder recorder;
        try
        {
            recorder = await VoiceRecorder.StartAsync(path, app.MicrophoneId);
        }
        catch (MicrophoneUnavailableException ex)
        {
            AppLog.MediaFailed(App.ShellLog, "record", chat.ConversationId, ex.Message);
            chat.Voice.Cancel();
            await AskMicrophoneSettingsAsync();
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "record", chat.ConversationId, ex.GetType().Name);
            chat.Voice.Cancel();
            return;
        }

        // A cancel or a chat switch came during the start.
        if (!ReferenceEquals(chat, Chat) || !chat.Voice.IsCapturing || chat.Voice.FilePath != path)
        {
            recorder.Dispose();
            VoiceDraft.DeleteFile(path);
            return;
        }

        _recorder       = recorder;
        _recordPath     = path;
        _recordStart    = Environment.TickCount64;
        _recordedBefore = 0;
        ShowPaused(false);
        ShowRecording();
        _recordTimer.Start();

        // The bar covers the mic now, so focus goes to Done.
        StopRecordingButton.Focus(FocusState.Programmatic);
    }

    // The microphone is off or missing. A desktop app cannot show the prompt of Windows, so this dialog asks the user
    // to turn access on. Open settings goes to the microphone page of Windows.
    private async Task AskMicrophoneSettingsAsync()
    {
        if (XamlRoot is not { } root)
        {
            return;
        }

        ContentDialog dialog     = Dialogs.Create(root);
        dialog.Title             = ChatCopy.MicrophoneTitle;
        dialog.Content           = new TextBlock { Text = ChatCopy.MicrophoneText, TextWrapping = TextWrapping.Wrap };
        dialog.PrimaryButtonText = ChatCopy.OpenSettings;
        dialog.CloseButtonText   = ChatCopy.Cancel;
        dialog.DefaultButton     = ContentDialogButton.Primary;
        AutomationProperties.SetAutomationId(dialog, "Dialog.Microphone");

        if (await ConversationMenuFlyout.ShowAsync(dialog))
        {
            await OpenMicrophoneSettingsAsync();
        }
    }

    // Opens the microphone privacy page of Windows. A test run only notes the address. The log names only the error
    // type of a failure.
    private async Task OpenMicrophoneSettingsAsync()
    {
        var page               = new Uri("ms-settings:privacy-microphone");
        string conversationId  = Chat?.ConversationId ?? string.Empty;
        if (TestHooks.RecordLaunch(page.OriginalString))
        {
            return;
        }

        try
        {
            if (!await Launcher.LaunchUriAsync(page))
            {
                AppLog.MediaFailed(App.ShellLog, "settings", conversationId, "NotLaunched");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "settings", conversationId, ex.GetType().Name);
        }
    }

    // Done finishes the file and reads it back with its waveform. The recording then waits in the preview chip. From
    // here this handler owns the recording, so a hide of the window meanwhile does not drop it. A failure shows a note
    // over the chips.
    private async void OnStopRecordingClick(object sender, RoutedEventArgs e)
    {
        if (_recorder is not { } recorder || Chat is not { } chat)
        {
            return;
        }

        string path = _recordPath;
        _recorder   = null;
        _recordChat = null;
        _recordPath = string.Empty;
        _recordTimer.Stop();

        try
        {
            await recorder.StopAsync();
            recorder.Dispose();

            // The read shares any handle that Windows still holds on the finished file.
            byte[] audio;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
            {
                audio = new byte[stream.Length];
                await stream.ReadExactlyAsync(audio);
            }

            VoiceWave wave = await WaveformReader.ReadAsync(audio);

            // A chat switch dropped the recording meanwhile, so the file goes.
            if (!chat.Voice.Stop(audio, wave))
            {
                VoiceDraft.DeleteFile(path);
                return;
            }

            DraftBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The log names only the error type and code, because the message of a file error names the path.
            AppLog.MediaFailed(App.ShellLog, "record", chat.ConversationId, $"{ex.GetType().Name} 0x{ex.HResult:X8}");
            recorder.Dispose();
            chat.Voice.Cancel();
            VoiceDraft.DeleteFile(path);
            chat.NoteRecordingFailed();
        }
    }

    // Pause stops the recorder, and Resume restarts it into the same file. The time and the waveform hold still during
    // a pause, so the time counts only recorded time.
    private void OnPauseRecordingClick(object sender, RoutedEventArgs e)
    {
        if (_recorder is not { } recorder || Chat is not { } chat)
        {
            return;
        }

        if (chat.Voice.Pause())
        {
            recorder.Pause();
            _recordTimer.Stop();
            _recordedBefore += Environment.TickCount64 - _recordStart;
            ShowPaused(true);
            return;
        }

        if (chat.Voice.Resume())
        {
            recorder.Resume();
            _recordStart = Environment.TickCount64;
            _recordTimer.Start();
            ShowPaused(false);
        }
    }

    // While paused, Pause becomes Resume with a play glyph, to record more.
    private void ShowPaused(bool paused)
    {
        string name                = paused ? ChatCopy.Resume : ChatCopy.Pause;
        PauseRecordingGlyph.Glyph  = paused ? "" : "";
        AutomationProperties.SetName(PauseRecordingButton, name);
        ToolTipService.SetToolTip(PauseRecordingButton, name);
    }

    // Cancel recording releases the file of the recorder and deletes it.
    private void OnCancelRecordingClick(object sender, RoutedEventArgs e)
    {
        DropRecorder();
        Chat?.Voice.Cancel();
        DraftBox.Focus(FocusState.Programmatic);
    }

    // Stops a running recorder and deletes its file, on a cancel, a chat switch or an unload of the pane. Its chat also
    // goes back to idle. A chat kept over a closed window once stayed in the recording state with no recorder. Its bar
    // then came back in the next window with a Done that did nothing.
    private void DropRecorder()
    {
        if (_recordChat is { Voice.IsCapturing: true } recording)
        {
            recording.Voice.Cancel();
        }

        _recordChat = null;
        _recordTimer.Stop();
        _recorder?.Dispose();
        _recorder = null;
        VoiceDraft.DeleteFile(_recordPath);
        _recordPath = string.Empty;
    }

    // Shows the recorded time in whole seconds without the pauses, and the newest input level.
    private void ShowRecording()
    {
        if (_recorder is not { } recorder)
        {
            return;
        }

        long seconds     = (_recordedBefore + Environment.TickCount64 - _recordStart) / 1000;
        float level      = recorder.TakeLevel();
        RecordTimer.Text = VoiceWave.LengthText(TimeSpan.FromSeconds(seconds));
        RecordBars.Push(level);

        // A test run notes the newest bar, so a test sees the waveform move.
        TestHooks.NoteState("record-level", level.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));

        // A test run notes how far the bars strayed from the center line. Only a test run measures it, because the
        // measure walks every bar.
        if (TestHooks.BlockLaunches)
        {
            TestHooks.NoteState("record-drift", RecordBars.CenterDrift().ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    // The preview chip plays or pauses, and a click on its waveform seeks.
    private void OnPreviewPlayClick(object sender, RoutedEventArgs e) => RequestPreview(null);

    private void OnPreviewSeekRequested(object? sender, double share) => RequestPreview(share);

    private void RequestPreview(double? seekTo)
    {
        if (Chat is not { Voice: { IsPreview: true, Wave: { } wave } voice })
        {
            return;
        }

        OnVoiceRequested(this, new VoiceRequest(PreviewKey, voice.Audio, wave.Length, seekTo, ShowPreviewPlaying));
    }

    // The remove button of the preview chip drops the recording and its file.
    private void OnPreviewRemoveClick(object sender, RoutedEventArgs e)
    {
        Chat?.Voice.Cancel();
        DraftBox.Focus(FocusState.Programmatic);
    }

    // While the preview plays, Play becomes Pause and the waveform fills to the played part.
    private void ShowPreviewPlaying(bool playing, double share)
    {
        string name        = playing ? ChatCopy.Pause : ChatCopy.Play;
        PreviewGlyph.Glyph = playing ? "\uE769" : "\uE768";
        AutomationProperties.SetName(PreviewPlay, name);
        ToolTipService.SetToolTip(PreviewPlay, name);
        PreviewBars.ShowProgress(share, playing);
        TestHooks.ShowState(PreviewPlay, playing ? "Playing" : "Paused");
    }
}
