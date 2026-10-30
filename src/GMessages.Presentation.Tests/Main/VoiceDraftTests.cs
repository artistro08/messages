using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Tests.Main;

[TestClass]
public sealed class VoiceDraftTests
{
    private static readonly VoiceWave Wave = new(new float[VoiceWave.BarCount], TimeSpan.FromSeconds(3));

    private string _path = string.Empty;

    [TestInitialize]
    public void Init()
    {
        _path = Path.Combine(Path.GetTempPath(), "voice-" + Guid.NewGuid().ToString("N") + ".m4a");
        File.WriteAllBytes(_path, [1, 2, 3]);
    }

    [TestCleanup]
    public void Cleanup() => File.Delete(_path);

    /// <summary>
    /// Idle, then Recording, then Preview, then Idle when taken for sending: the audio comes out and the temp file is gone.
    /// </summary>
    [TestMethod]
    public void RecordStopTake_IdleRecordingPreviewIdle_DeletesFile()
    {
        var voice = new VoiceDraft();
        Assert.IsTrue(voice.IsIdle);

        Assert.IsTrue(voice.Start(_path));
        Assert.IsTrue(voice.IsRecording);
        Assert.AreEqual(_path, voice.FilePath);

        Assert.IsTrue(voice.Stop([7, 8], Wave));
        Assert.IsTrue(voice.IsPreview);
        Assert.AreEqual("0:03", voice.LengthText);

        CollectionAssert.AreEqual(new byte[] { 7, 8 }, voice.Take());
        Assert.IsTrue(voice.IsIdle);
        Assert.IsFalse(File.Exists(_path), "The temp file outlived the send.");
        Assert.IsEmpty(voice.Audio);
        Assert.IsNull(voice.Wave);
    }

    /// <summary>
    /// Cancel from Recording or from Preview goes back to Idle and deletes the temp file.
    /// </summary>
    [TestMethod]
    public void Cancel_FromRecordingOrPreview_IdleAndDeletesFile()
    {
        var voice = new VoiceDraft();
        voice.Start(_path);
        voice.Cancel();
        Assert.IsTrue(voice.IsIdle);
        Assert.IsFalse(File.Exists(_path), "Cancel while recording kept the temp file.");

        File.WriteAllBytes(_path, [1]);
        voice.Start(_path);
        voice.Stop([1], Wave);
        voice.Cancel();
        Assert.IsTrue(voice.IsIdle);
        Assert.IsFalse(File.Exists(_path), "Removing the preview kept the temp file.");
    }

    /// <summary>
    /// Steps from the wrong state change nothing: a second start, a stop that comes after Cancel, a take with nothing recorded.
    /// </summary>
    [TestMethod]
    public void WrongState_ChangesNothing()
    {
        var voice = new VoiceDraft();
        Assert.IsFalse(voice.Stop([1], Wave), "Stopped without recording.");
        Assert.IsNull(voice.Take());

        voice.Start(_path);
        Assert.IsFalse(voice.Start("other.m4a"), "Started twice.");
        Assert.AreEqual(_path, voice.FilePath);

        voice.Cancel();
        Assert.IsFalse(voice.Stop([1], Wave), "A stop after Cancel brought the recording back.");
        Assert.IsTrue(voice.IsIdle);
    }

    /// <summary>
    /// The microphone off (or missing) goes back to Idle with the settings message, deleting any file; the next start hides it.
    /// </summary>
    [TestMethod]
    public void MicrophoneOff_IdleWithMessage_UntilNextStart()
    {
        var voice = new VoiceDraft();
        voice.Start(_path);

        voice.MicrophoneOff();
        Assert.IsTrue(voice.IsIdle);
        Assert.IsTrue(voice.IsMicrophoneOff);
        Assert.IsFalse(File.Exists(_path));

        voice.Start(_path);
        Assert.IsFalse(voice.IsMicrophoneOff);
    }

    /// <summary>
    /// Send sends the recording first as "Voice message.m4a" (audio/mp4), then the text; closing the chat drops a
    /// recording in progress with its file.
    /// </summary>
    [TestMethod]
    public async Task ChatSend_SendsVoiceAsAudioMp4_CloseDeletesRecording()
    {
        using var rig = new MainRig();
        rig.Client.Conversations["a"] = MainRig.Conv("a", "Ann");
        rig.Client.MessagesPage       = new GMessages.Protocol.Proto.ListMessagesResponse();
        using (var chat = new ChatViewModel(rig.Store, "a", TimeProvider.System, a => a()))
        {
            await chat.LoadAsync();
            Assert.IsFalse(chat.CanSend);
            chat.Voice.Start(_path);
            chat.Voice.Stop([4, 5, 6], Wave);
            Assert.IsTrue(chat.CanSend, "A recording waiting doesn't turn Send on.");

            await chat.SendAsync();

            Assert.IsTrue(chat.Voice.IsIdle);
            Assert.IsFalse(File.Exists(_path));
            GMessages.Protocol.Proto.MediaContent sent = rig.Client.Sent.Single().MessagePayload.MessageInfo.Single().MediaContent;
            Assert.AreEqual("Voice message.m4a", sent.MediaName);
            Assert.AreEqual("audio/mp4", sent.MimeType);
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, rig.Client.Media[sent.MediaID]);
            Assert.IsTrue(chat.Messages.Single().HasVoice, "The sent voice message doesn't show as one.");

            File.WriteAllBytes(_path, [1]);
            chat.Voice.Start(_path);
        }

        Assert.IsFalse(File.Exists(_path), "Closing the chat kept the recording's temp file.");
    }
}
