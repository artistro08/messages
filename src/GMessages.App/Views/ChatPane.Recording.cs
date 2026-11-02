using GMessages.App.Media;
using GMessages.App.Views.Controls;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace GMessages.App.Views;

/// <summary>
/// The compose area's recording: the mic, the recording bar, pause and cancel, and the voice preview chip.
/// </summary>
/// <remarks>
/// Part of <see cref="ChatPane"/>; the other members are in ChatPane.xaml.cs.
/// </remarks>
public sealed partial class ChatPane
{

    // =========================================================================
    // RECORDING
    // =========================================================================

    // The Mic: Records To A New Temp File In The Media Store's Private Folder. Access Off Or No Microphone Shows The
    // Settings Message; Any Other Failure Goes Back To The Mic. A Cancel Or Chat Switch While Starting Drops The Recorder
    private async void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (Chat is not { Voice.IsIdle: true } chat || (Application.Current as App) is not { Media: { } media } app)
        {
            return;
        }

        string path = System.IO.Path.Combine(media.TempFolder, "voice-" + Guid.NewGuid().ToString("N") + ".m4a");
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

        // Cancelled Or Switched Away While Starting
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
        RecordBars.Show(null);
        ShowRecording();
        _recordTimer.Start();

        // The Mic Is Covered Now: Focus Goes To Done
        StopRecordingButton.Focus(FocusState.Programmatic);
    }

    // Microphone Off Or Missing: A Desktop App Can't Show Windows' Own Prompt, So This Dialog Asks The User To Turn It On,
    // And Open Settings Goes To Windows' Microphone Page
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

    // Windows' Microphone Privacy Page. A Test Run Only Notes The Address; A Failure Logs Its Error Type Only
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

    // Done: The File Is Finished, Read Back With Its Waveform, And Waits In The Preview Chip. From Here The Recording Is
    // This Handler's, So Hiding The Window Meanwhile Doesn't Drop It. A Failure Says So Over The Chips, Never Silently
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

            // Read Sharing Whatever Handle Windows Still Holds On The Finished File
            byte[] audio;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
            {
                audio = new byte[stream.Length];
                await stream.ReadExactlyAsync(audio);
            }

            VoiceWave wave = await WaveformReader.ReadAsync(audio);

            // Dropped Meanwhile (A Chat Switch): The File Goes
            if (!chat.Voice.Stop(audio, wave))
            {
                VoiceDraft.DeleteFile(path);
                return;
            }

            DraftBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The Error Type And Code Only (A File Error's Message Names The Path)
            AppLog.MediaFailed(App.ShellLog, "record", chat.ConversationId, $"{ex.GetType().Name} 0x{ex.HResult:X8}");
            recorder.Dispose();
            chat.Voice.Cancel();
            VoiceDraft.DeleteFile(path);
            chat.NoteRecordingFailed();
        }
    }

    // Pause Or Resume: The Recorder Stops Or Restarts Into The Same File, And The Time And Waveform Hold Still Meanwhile,
    // So The Time Counts Recorded Time Only
    private void OnPauseRecordingClick(object sender, RoutedEventArgs e)
    {
        if (_recorder is not { } recorder || Chat is not { } chat)
        {
            return;
        }

        // Pause
        if (chat.Voice.Pause())
        {
            recorder.Pause();
            _recordTimer.Stop();
            _recordedBefore += Environment.TickCount64 - _recordStart;
            ShowPaused(true);
            return;
        }

        // Resume
        if (chat.Voice.Resume())
        {
            recorder.Resume();
            _recordStart = Environment.TickCount64;
            _recordTimer.Start();
            ShowPaused(false);
        }
    }

    // Pause Becomes Resume (The Mic, To Record More) While Paused, And Back
    private void ShowPaused(bool paused)
    {
        string name                = paused ? ChatCopy.Resume : ChatCopy.Pause;
        PauseRecordingGlyph.Glyph  = paused ? "" : "";
        AutomationProperties.SetName(PauseRecordingButton, name);
        ToolTipService.SetToolTip(PauseRecordingButton, name);
    }

    // Cancel Recording: The Recorder Lets Go Of The File, Which Is Deleted
    private void OnCancelRecordingClick(object sender, RoutedEventArgs e)
    {
        DropRecorder();
        Chat?.Voice.Cancel();
        DraftBox.Focus(FocusState.Programmatic);
    }

    // Stops A Running Recorder And Deletes Its File (A Cancel, A Chat Switch Or The Pane Unloading). Its Chat Goes Back
    // To Idle Too: A Chat Kept Over A Closed Window Was Left Recording With No Recorder, So Its Bar Came Back In The Next
    // Window With A Done That Did Nothing
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

    // The Recorded Time (Whole Seconds, Pauses Left Out) And The Newest Input Level
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

        // Test Run: The Newest Bar, So A Test Sees The Waveform Move
        TestHooks.NoteState("record-level", level.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));

        // Test Run: How Far The Bars Strayed From The Center Line (Measured Only Then: It Walks Every Bar)
        if (TestHooks.BlockLaunches)
        {
            TestHooks.NoteState("record-drift", RecordBars.CenterDrift().ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    // The Preview Chip: Play Or Pause, And A Click On Its Waveform Seeks
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

    // The Preview Chip's Remove: The Recording And Its File Go
    private void OnPreviewRemoveClick(object sender, RoutedEventArgs e)
    {
        Chat?.Voice.Cancel();
        DraftBox.Focus(FocusState.Programmatic);
    }

    // The Preview's Play Becomes Pause While It Plays, And Its Waveform Fills Up To How Much Has Played
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
