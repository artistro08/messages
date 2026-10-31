using System.IO.Compression;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace GMessages.App.UITests;

/// <summary>
/// Photos, videos and files in the chat: the photo bubble (blurred, then sharp), its reserved size, its failure and
/// retry, its caption, and the file card that opens in its app.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed partial class MediaTests : UiTest
{
    // The Photo Bubble's Load States, As The Test Hooks Report Them In Its Item Status
    private const string Blurred = "Blurred";
    private const string Full    = "Full";
    private const string Failed  = "Failed";

    /// <summary>
    /// The seeded photo in Mia's chat shows: its bubble loads to the full image, and the image is on screen (blue).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ReceivedPhoto_ShowsImage()
    {
        await OpenMediaChatAsync();

        AutomationElement? photo = null;
        Wait.Until(() => (photo = FirstPhoto()) is not null, AppDriver.DefaultTimeout, () => "Mia's chat showed no photo.");
        WaitForStatus(photo!, Full);
        Assert.AreEqual(ChatCopy.Photo, AppDriver.SafeName(photo), "The photo without a caption isn't named Photo.");

        System.Drawing.Color center = CenterPixel(photo!);
        Assert.IsTrue(center.B > 180 && center.B > center.R + 100, $"The photo's middle reads {center}, not the photo's blue.");
    }

    /// <summary>
    /// A thumbnail-only photo shows blurred first, asks the phone for its full size, and turns sharp when the phone sends
    /// it (the message edit swaps its media). The bubble keeps the photo's shape from its dimensions throughout.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ReceivedPhoto_ThumbnailThenFull()
    {
        await OpenMediaChatAsync();
        Server.Phone.FullSizeDelay = TimeSpan.FromSeconds(2);

        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(300, 200, SplitColors), "split.png", withThumbnail: true);
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Blurred);
        WaitForStatus(photo, Full);

        System.Drawing.Rectangle box = photo.BoundingRectangle;
        Assert.AreEqual(1.5, (double)box.Width / box.Height, 0.03, $"The photo's box {box} doesn't keep the photo's 3:2 shape.");
    }

    /// <summary>
    /// A photo that arrives while its download fails already takes its full size in the chat, so when it does load (after
    /// a retry) nothing moves and the chat stays on its newest message.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ReceivedPhoto_ReservesHeight_NoJump()
    {
        await OpenMediaChatAsync();
        Server.FailMediaDownloads = true;

        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(400, 300, SplitColors), "tall.png");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Failed);
        Thread.Sleep(500);
        System.Drawing.Rectangle before = photo.BoundingRectangle;
        Assert.AreEqual(4.0 / 3, (double)before.Width / before.Height, 0.03, $"The unloaded photo's box {before} isn't the photo's 4:3 shape.");

        // Loads On Retry, In The Same Place
        Server.FailMediaDownloads = false;
        photo.Click();
        WaitForStatus(photo, Full);
        Thread.Sleep(500);
        System.Drawing.Rectangle after = photo.BoundingRectangle;
        Assert.AreEqual(before, after, "The photo moved or changed size when it loaded.");
        AssertAtNewest("Loading the photo");
    }

    /// <summary>
    /// A photo whose download fails says so over its placeholder, and a click tries again and shows it.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ReceivedPhoto_DownloadFails_ShowsRetry()
    {
        await OpenMediaChatAsync();
        Server.FailMediaDownloads = true;

        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(64, 64, SplitColors), "small.png");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Failed);
        Wait.Until(() => HasText(photo, ChatCopy.PhotoLoadFailed), AppDriver.DefaultTimeout, () => $"'{ChatCopy.PhotoLoadFailed}' didn't show on the photo.");

        Server.FailMediaDownloads = false;
        photo.Click();
        WaitForStatus(photo, Full);
        Assert.IsFalse(HasText(photo, ChatCopy.PhotoLoadFailed), "The failure note stayed after the photo loaded.");
    }

    /// <summary>
    /// A photo sent with a caption shows both in one bubble: the photo, named by the caption, with the caption under it.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task PhotoWithCaption_ShowsBoth()
    {
        await OpenMediaChatAsync();

        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(200, 200, SplitColors), "beach.png", caption: "At the beach");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        AutomationElement text  = App.Find("Bubble." + message.MessageID);
        WaitForStatus(photo, Full);

        Assert.AreEqual("At the beach", AppDriver.SafeName(text), "The caption isn't the bubble's text.");
        Assert.AreEqual("At the beach", AppDriver.SafeName(photo), "The photo isn't named by its caption.");
        Assert.IsTrue(text.BoundingRectangle.Top >= photo.BoundingRectangle.Bottom, $"The caption ({text.BoundingRectangle}) isn't under the photo ({photo.BoundingRectangle}).");
    }

    /// <summary>
    /// A received file shows as a card with its name and size; a click downloads it, writes it to the private temp folder
    /// and opens it (the test hook notes the path instead), and the file holds the bytes sent.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ReceivedFile_Card_OpensDownloaded()
    {
        await OpenMediaChatAsync();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 notes for the trip");

        Message message        = Server.Phone.PushIncomingMedia("c-media", "application/pdf", bytes, "notes.pdf");
        AutomationElement card = App.Find("Bubble.File." + message.MessageID);
        Assert.AreEqual("notes.pdf", AppDriver.SafeName(card), "The card isn't named by the file name.");
        Wait.Until(() => HasText(card, "27 bytes"), AppDriver.DefaultTimeout, () => "The card doesn't show the file's size.");

        card.Click();
        string launched = Path.Combine(App.DataDir, "launched.txt");
        Wait.Until(() => File.Exists(launched), AppDriver.DefaultTimeout, () => "Clicking the card opened nothing.");
        string path = File.ReadAllLines(launched)[^1];
        Assert.AreEqual("notes.pdf", Path.GetFileName(path), "The opened file lost its name.");
        Assert.IsTrue(path.StartsWith(App.DataDir, StringComparison.OrdinalIgnoreCase), $"The file was written outside the app's private folder: {path}.");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), "The opened file doesn't hold the bytes sent.");
        Wait.Until(() => card.Properties.ItemStatus.ValueOrDefault == "Ready" && HasText(card, "27 bytes"), AppDriver.DefaultTimeout,
            () => $"The card stayed '{card.Properties.ItemStatus.ValueOrDefault}' after opening.");
    }

    /// <summary>
    /// The photo menu's Save as writes the photo's bytes to the picked file (the test hook picks the path; the dialog is
    /// still made and set up) and marks it as downloaded from the internet.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task SaveAs_WritesPickedFile()
    {
        await OpenMediaChatAsync();
        byte[] bytes            = Png(64, 64, SplitColors);
        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", bytes, "keep.png");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Full);

        string target = Path.Combine(App.DataDir, "saved-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllText(Path.Combine(App.DataDir, "save-pick.txt"), target);
        OpenMenu(photo);
        App.Find("Bubble.SaveAs").Patterns.Invoke.Pattern.Invoke();

        Wait.Until(() => File.Exists(target) && new FileInfo(target).Length == bytes.Length, AppDriver.DefaultTimeout, () => "Save as wrote nothing to the picked file.");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(target), "The saved file doesn't hold the photo.");
        Assert.IsTrue(File.Exists(target + ":Zone.Identifier"), "The saved file has no mark of the web.");
    }

    /// <summary>
    /// The menu's Download saves a photo, and a file, to the Downloads folder (the test hook's) with the bytes and the
    /// mark of the web; a second copy gets "name (1)". "Saved to Downloads" shows, and its Show in folder opens the
    /// folder (the test hook notes it).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Download_FromMenu_SavesToDownloads()
    {
        await OpenMediaChatAsync();
        string downloads        = Path.Combine(App.DataDir, "Downloads");
        byte[] bytes            = Png(64, 64, SplitColors);
        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", bytes, "keep.png");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Full);

        // Twice: The Second Copy Gets Its Own Name
        foreach (string name in (string[])["keep.png", "keep (1).png"])
        {
            OpenMenu(photo);
            App.Find("Bubble.Download").Patterns.Invoke.Pattern.Invoke();
            string path = Path.Combine(downloads, name);
            Wait.Until(() => File.Exists(path) && new FileInfo(path).Length == bytes.Length, AppDriver.DefaultTimeout, () => $"Download didn't save '{name}'.");
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), $"'{name}' doesn't hold the photo.");
            Assert.IsTrue(File.Exists(path + ":Zone.Identifier"), $"'{name}' has no mark of the web.");
        }

        // The Confirmation, And Show In Folder
        AutomationElement bar = App.Find("Main.Downloaded");
        Assert.IsTrue(HasText(bar, ChatCopy.SavedToDownloads), $"The confirmation doesn't read '{ChatCopy.SavedToDownloads}'.");
        App.Find("Main.Downloaded.ShowInFolder").Patterns.Invoke.Pattern.Invoke();
        string launched = Path.Combine(App.DataDir, "launched.txt");
        Wait.Until(() => File.Exists(launched), AppDriver.DefaultTimeout, () => "Show in folder opened nothing.");
        Assert.AreEqual(downloads, File.ReadAllLines(launched)[^1], true, "Show in folder opened another folder.");

        // A File's Menu Has It Too
        byte[] pdf             = System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 notes for the trip");
        Message file           = Server.Phone.PushIncomingMedia("c-media", "application/pdf", pdf, "notes.pdf");
        AutomationElement card = App.Find("Bubble.File." + file.MessageID);
        OpenMenu(card);
        App.Find("Bubble.Download").Patterns.Invoke.Pattern.Invoke();
        string pdf_path = Path.Combine(downloads, "notes.pdf");
        Wait.Until(() => File.Exists(pdf_path) && new FileInfo(pdf_path).Length == pdf.Length, AppDriver.DefaultTimeout, () => "Download didn't save the file.");
    }

    /// <summary>
    /// A picked photo shows as a chip; Send uploads it, the phone gets it as a media message and then the text as its own
    /// message (Mia's chat is RCS, which refuses captions), the bubble shows the photo in its shape, and the chip is gone.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Attach_Photo_SendsAndShows()
    {
        await OpenMediaChatAsync();
        byte[] bytes = Png(300, 200, SplitColors);
        Pick(("split.png", bytes));

        App.Find("Chat.Attach").Patterns.Invoke.Pattern.Invoke();
        AutomationElement chip = App.Find("Chat.Attachment.1");
        Assert.AreEqual("split.png", AppDriver.SafeName(chip), "The chip isn't named by the file name.");

        App.Find("Chat.Draft").Patterns.Value.Pattern.SetValue("Look");
        App.Find("Chat.Send").Patterns.Invoke.Pattern.Invoke();

        Wait.Until(() => Server.Phone.Sent.Count == 2, AppDriver.DefaultTimeout, () => $"The phone got {Server.Phone.Sent.Count} sends, not the photo and the text.");
        MessageInfo media = Server.Phone.Sent[0].MessagePayload.MessageInfo.Single();
        Assert.AreEqual("split.png", media.MediaContent.MediaName, "The phone got another attachment.");
        Assert.AreEqual("image/png", media.MediaContent.MimeType, "The photo went with another type.");
        Assert.AreEqual("Look", FakeGoogle.FakePhone.SentText(Server.Phone.Sent[1]), "The text didn't follow the photo.");
        Wait.Until(() => App.TryFind("Chat.Attachment.1") is null, AppDriver.DefaultTimeout, () => "The chip stayed after Send.");

        // The Photo's Echo (Before The Text's) Keeps Showing The Photo, In The Photo's Shape
        string echo_id          = Server.Phone.Chats.Page("c-media", 2, null).Page[1].MessageID;
        AutomationElement photo = App.Find("Bubble.Photo." + echo_id);
        WaitForStatus(photo, Full);
        System.Drawing.Rectangle box = photo.BoundingRectangle;
        Assert.AreEqual(1.5, (double)box.Width / box.Height, 0.03, $"The sent photo's box {box} doesn't keep its 3:2 shape.");
    }

    /// <summary>
    /// A sent photo's echo (which brings its media ID) keeps the photo on screen: it never clears, and nothing downloads
    /// it again, since the bytes sent are the photo.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task SentPhoto_Echo_NoReload()
    {
        await OpenMediaChatAsync();
        AutomationElement? first = null;
        Wait.Until(() => (first = FirstPhoto()) is not null, AppDriver.DefaultTimeout, () => "Mia's chat showed no photo.");
        string seeded = first!.Properties.AutomationId.ValueOrDefault ?? string.Empty;
        Pick(("split.png", Png(300, 200, SplitColors)));
        App.Find("Chat.Attach").Patterns.Invoke.Pattern.Invoke();
        App.Find("Chat.Attachment.1");
        App.Find("Chat.Send").Patterns.Invoke.Pattern.Invoke();

        // The Sent Photo, Shown From Its Own Bytes
        AutomationElement? photo = null;
        Wait.Until(() => (photo = SentPhoto(seeded)) is not null, AppDriver.DefaultTimeout, () => "The sent photo didn't show.");
        WaitForStatus(photo!, Full);
        Wait.Until(() => Server.Phone.Sent.Count == 1, AppDriver.DefaultTimeout, () => "The phone didn't get the photo.");
        string media_id = Server.Phone.Sent[0].MessagePayload.MessageInfo.Single().MediaContent.MediaID;
        string echo_id  = Server.Phone.Chats.Page("c-media", 1, null).Page[0].MessageID;

        // Through The Echo And A Moment After, It Stays Full
        bool echoed    = false;
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < until)
        {
            Assert.AreEqual(Full, photo!.Properties.ItemStatus.ValueOrDefault, "The sent photo cleared when its echo arrived.");
            echoed |= photo.Properties.AutomationId.ValueOrDefault == "Bubble.Photo." + echo_id;
            Thread.Sleep(30);
        }

        Assert.IsTrue(echoed, "The sent photo's echo never arrived.");
        CollectionAssert.DoesNotContain(Server.DownloadedMediaIds.ToList(), media_id, "The sent photo downloaded again after its echo.");
    }

    /// <summary>
    /// A download that stalls gives up after the download timeout (shortened by the test hook) and shows the photo's
    /// failure; a click tries again and shows it.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task StalledDownload_ShowsFailed()
    {
        await OpenMediaChatAsync();
        File.WriteAllText(Path.Combine(App.DataDir, "download-timeout.txt"), "2");
        Server.MediaDownloadDelay = TimeSpan.FromSeconds(60);

        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(64, 64, SplitColors), "stalled.png");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Failed);
        Assert.IsTrue(HasText(photo, ChatCopy.PhotoLoadFailed), $"'{ChatCopy.PhotoLoadFailed}' didn't show on the stalled photo.");

        Server.MediaDownloadDelay = TimeSpan.Zero;
        photo.Click();
        WaitForStatus(photo, Full);
    }

    /// <summary>
    /// A thumbnail-only photo whose full size never comes from the phone offers to try again once the wait (shortened by
    /// the test hook) runs out.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ReceivedPhoto_FullSizeNeverArrives_ShowsRetry()
    {
        await OpenMediaChatAsync();
        File.WriteAllText(Path.Combine(App.DataDir, "full-size-wait.txt"), "2");
        Server.Phone.FullSizeDelay = Timeout.InfiniteTimeSpan;

        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(300, 200, SplitColors), "never.png", withThumbnail: true);
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Blurred);
        WaitForStatus(photo, Failed);
        Assert.IsTrue(HasText(photo, ChatCopy.PhotoLoadFailed), $"'{ChatCopy.PhotoLoadFailed}' didn't show when the full size never came.");
    }

    /// <summary>
    /// The paperclip sits inside the message box's right edge, left of the mic.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Attach_InsideDraftBox_LeftOfMic()
    {
        await OpenMediaChatAsync();

        System.Drawing.Rectangle box    = App.Find("Chat.Draft").BoundingRectangle;
        System.Drawing.Rectangle attach = App.Find("Chat.Attach").BoundingRectangle;
        System.Drawing.Rectangle record = App.Find("Chat.Record").BoundingRectangle;
        Assert.IsTrue(box.Contains(attach), $"The paperclip {attach} isn't inside the message box {box}.");
        Assert.IsTrue(attach.Right <= record.Left, $"The paperclip {attach} isn't left of the mic {record}.");
        Assert.IsTrue(attach.Left > box.Left + (box.Width / 2), $"The paperclip {attach} isn't on the message box's right side {box}.");
    }

    /// <summary>
    /// Two picked files show as two chips; each chip's remove button drops only it, and with none left Send turns off.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Attach_RemoveChip()
    {
        await OpenMediaChatAsync();
        Pick(("blue.png", Png(20, 20, (_, _, _) => (40, 60, 220))), ("notes.pdf", System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 notes")));

        App.Find("Chat.Attach").Patterns.Invoke.Pattern.Invoke();
        App.Find("Chat.Attachment.1");
        Assert.AreEqual("notes.pdf", AppDriver.SafeName(App.Find("Chat.Attachment.2")), "The file chip isn't named by the file name.");
        Assert.AreEqual(ChatCopy.RemoveAttachment, AppDriver.SafeName(App.Find("Chat.RemoveAttachment.1")), "The remove button has another name.");
        Assert.IsTrue(App.Find("Chat.Send").IsEnabled, "Send is off with attachments waiting.");

        App.Find("Chat.RemoveAttachment.1").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => App.TryFind("Chat.Attachment.1") is null, AppDriver.DefaultTimeout, () => "The removed chip stayed.");
        Assert.IsNotNull(App.TryFind("Chat.Attachment.2"), "Removing one chip took the other too.");

        App.Find("Chat.RemoveAttachment.2").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => !App.Find("Chat.Send").IsEnabled, AppDriver.DefaultTimeout, () => "Send stayed on with nothing to send.");
        Assert.IsEmpty(Server.Phone.Sent, "Removing chips sent something.");
    }

    /// <summary>
    /// Ctrl + Shift + a opens the picker for any files, and Ctrl + Shift + p the photo picker, whose pick becomes a chip.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CtrlShiftA_OpensPicker()
    {
        await OpenMediaChatAsync();
        App.Find("Chat.Draft").Focus();
        string opened       = Path.Combine(App.DataDir, "picker.txt");
        List<string> expect = [];

        // Another App's Global Hotkey Takes Ctrl + Shift + a Before Any Window Sees It: Then Only Ctrl + Shift + p Is Tried
        if (HotkeyFree(VirtualKeyShort.KEY_A))
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_A);
            expect.Add("files");
            Wait.Until(() => File.Exists(opened) && File.ReadAllLines(opened).SequenceEqual(expect), AppDriver.DefaultTimeout,
                () => "Ctrl + Shift + a didn't open the file picker.");
        }
        else
        {
            TestContext.WriteLine("Ctrl + Shift + a is another app's global hotkey on this PC; only Ctrl + Shift + p was tried.");
        }

        Pick(("blue.png", Png(20, 20, (_, _, _) => (40, 60, 220))));
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_P);
        expect.Add("photos");
        Wait.Until(() => File.Exists(opened) && File.ReadAllLines(opened).SequenceEqual(expect), AppDriver.DefaultTimeout,
            () => "Ctrl + Shift + p didn't open the photo picker.");
        Assert.AreEqual("blue.png", AppDriver.SafeName(App.Find("Chat.Attachment.1")), "The photo picked didn't become a chip.");
    }

    /// <summary>
    /// A received voice message shows its length, read from the audio (the proto has none), and its play button plays and
    /// pauses it, named Play or Pause to match.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ReceivedVoice_PlayPause_ShowsLength()
    {
        await OpenMediaChatAsync();

        Message message          = Server.Phone.PushIncomingMedia("c-media", "audio/mp4", await VoiceFixtureAsync(), "Voice message.m4a");
        AutomationElement length = App.Find("Bubble.Voice." + message.MessageID);
        WaitForStatus(length, "Ready");
        Assert.AreEqual("0:03", AppDriver.SafeName(length), "The voice message shows another length.");

        AutomationElement play = App.Find("Voice.Play." + message.MessageID);
        Assert.AreEqual(ChatCopy.Play, AppDriver.SafeName(play), "The play button has another name.");
        play.Patterns.Invoke.Pattern.Invoke();
        WaitForStatus(play, "Playing");
        Assert.AreEqual(ChatCopy.Pause, AppDriver.SafeName(play), "The playing message's button isn't Pause.");
        WaitForPlayback();

        play.Patterns.Invoke.Pattern.Invoke();
        WaitForStatus(play, "Paused");
        Assert.AreEqual(ChatCopy.Play, AppDriver.SafeName(play), "The paused message's button isn't Play.");
    }

    /// <summary>
    /// The mic records (the test hook's microphone copies the fixture), Stop puts the recording in the preview chip with
    /// its length, and Send sends it as "Voice message.m4a" (audio/mp4); the temp file is gone after.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Record_StopSend_SendsAudioMp4()
    {
        await FakeMicrophoneAsync();
        await OpenMediaChatAsync();
        Assert.AreEqual(ChatCopy.RecordVoiceMessage, AppDriver.SafeName(App.Find("Chat.Record")), "The mic has another name.");

        App.Find("Chat.Record").Patterns.Invoke.Pattern.Invoke();
        AutomationElement timer = App.Find("Chat.RecordTimer");
        Wait.Until(() => VoiceTempFiles().Length == 1, AppDriver.DefaultTimeout, () => "Recording wrote no temp file.");

        // The Live Waveform Moves (The Fake Recorder's Level Follows A Wave) And The Timer Runs
        string level_file    = Path.Combine(App.DataDir, "record-level.txt");
        HashSet<string> seen = [];
        Wait.Until(() =>
        {
            string level = File.Exists(level_file) ? ReadShared(level_file) : string.Empty;
            if (double.TryParse(level, System.Globalization.CultureInfo.InvariantCulture, out double value) && value > 0)
            {
                seen.Add(level);
            }

            return seen.Count >= 3;
        }, AppDriver.DefaultTimeout, () => $"The live waveform didn't move: levels seen {string.Join(", ", seen)}.");
        Wait.Until(() => AppDriver.SafeName(timer) == "0:01", AppDriver.DefaultTimeout, () => $"The timer stayed '{AppDriver.SafeName(timer)}'.");
        App.Find("Chat.StopRecording").Patterns.Invoke.Pattern.Invoke();

        AutomationElement length = App.Find("Chat.VoiceLength");
        Wait.Until(() => AppDriver.SafeName(length) == "0:03", AppDriver.DefaultTimeout, () => $"The preview chip shows '{AppDriver.SafeName(length)}', not 0:03.");
        Assert.AreEqual(ChatCopy.Play, AppDriver.SafeName(App.Find("Chat.VoicePlay")), "The preview's play button has another name.");
        App.Find("Chat.VoicePlay").Patterns.Invoke.Pattern.Invoke();
        WaitForPlayback();

        App.Find("Chat.Send").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => Server.Phone.Sent.Count == 1, AppDriver.DefaultTimeout, () => $"The phone got {Server.Phone.Sent.Count} sends, not the voice message.");
        MediaContent media = Server.Phone.Sent[0].MessagePayload.MessageInfo.Single().MediaContent;
        Assert.AreEqual("Voice message.m4a", media.MediaName, "The voice message went with another name.");
        Assert.AreEqual("audio/mp4", media.MimeType, "The voice message went with another type.");
        Wait.Until(() => App.TryFind("Chat.VoiceLength") is null, AppDriver.DefaultTimeout, () => "The preview chip stayed after Send.");
        Assert.IsEmpty(VoiceTempFiles(), "The recording's temp file outlived the send.");
    }

    /// <summary>
    /// The live waveform grows up and down from one fixed center line (owner: "the middle line shifts when recording
    /// gets loud"): its bounds stay put between a quiet and a loud level, and no bar strays from the center line.
    /// </summary>
    /// <remarks>
    /// The fake microphone's level follows a wave, so both a quiet and a loud level come by. The bars' drift comes from
    /// the app's test hook (<c>record-drift.txt</c>), measured on every tick from the last layout.
    /// </remarks>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Record_Waveform_KeepsCenterLine()
    {
        await FakeMicrophoneAsync();
        await OpenMediaChatAsync();

        App.Find("Chat.Record").Patterns.Invoke.Pattern.Invoke();
        AutomationElement wave = App.Find("Chat.RecordWave");

        // Watch The Levels Go By: The Wave's Bounds At A Quiet And A Loud One, And The Worst Drift Seen
        string level_file               = Path.Combine(App.DataDir, "record-level.txt");
        string drift_file               = Path.Combine(App.DataDir, "record-drift.txt");
        System.Drawing.Rectangle? quiet = null;
        System.Drawing.Rectangle? loud  = null;
        double worst                    = 0;
        Wait.Until(() =>
        {
            if (File.Exists(drift_file) && double.TryParse(ReadShared(drift_file), System.Globalization.CultureInfo.InvariantCulture, out double drift))
            {
                worst = Math.Max(worst, drift);
            }

            if (!File.Exists(level_file) || !double.TryParse(ReadShared(level_file), System.Globalization.CultureInfo.InvariantCulture, out double level))
            {
                return false;
            }

            quiet ??= level < 0.2 ? wave.BoundingRectangle : null;
            loud  ??= level > 0.7 ? wave.BoundingRectangle : null;
            return quiet is not null && loud is not null;
        }, AppDriver.DefaultTimeout, () => $"The levels never went both quiet and loud (quiet {quiet}, loud {loud}).");

        Assert.IsLessThanOrEqualTo(1, Math.Abs(quiet!.Value.Top - loud!.Value.Top), $"The waveform moved: quiet {quiet}, loud {loud}.");
        Assert.IsLessThanOrEqualTo(1, Math.Abs(quiet.Value.Height - loud.Value.Height), $"The waveform changed height: quiet {quiet}, loud {loud}.");
        Assert.IsLessThan(0.05, worst, $"A bar strayed {worst} px from the center line.");
    }

    /// <summary>
    /// Pause (reached from Done with Shift + Tab, pressed with Space) holds the timer and the live waveform still and
    /// becomes Resume; Resume counts on from there; Done from the resumed recording still lands in the preview chip.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Record_PauseResume_TimerHoldsThenCountsOn()
    {
        await FakeMicrophoneAsync();
        await OpenMediaChatAsync();

        App.Find("Chat.Record").Patterns.Invoke.Pattern.Invoke();
        AutomationElement timer = App.Find("Chat.RecordTimer");
        AutomationElement pause = App.Find("Chat.RecordPause");
        Wait.Until(() => AppDriver.SafeName(timer) == "0:01", AppDriver.DefaultTimeout, () => $"The timer stayed '{AppDriver.SafeName(timer)}'.");
        Assert.AreEqual(ChatCopy.Pause, AppDriver.SafeName(pause), "The pause button has another name.");
        System.Drawing.Rectangle timer_box = timer.BoundingRectangle;

        // Pause From The Keyboard: Focus Starts On Done, Pause Is Just Before It
        Wait.Until(() => App.Find("Chat.StopRecording").Properties.HasKeyboardFocus.ValueOrDefault, AppDriver.DefaultTimeout, () => "Done didn't get focus.");
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        Wait.Until(() => pause.Properties.HasKeyboardFocus.ValueOrDefault, AppDriver.DefaultTimeout, () => "Shift + Tab from Done didn't reach Pause.");
        Keyboard.Type(VirtualKeyShort.SPACE);
        Wait.Until(() => AppDriver.SafeName(pause) == ChatCopy.Resume, AppDriver.DefaultTimeout, () => $"Pause didn't become Resume: '{AppDriver.SafeName(pause)}'.");

        // Paused: The Time And The Waveform Hold Still
        string level_file = Path.Combine(App.DataDir, "record-level.txt");
        string held_time  = AppDriver.SafeName(timer);
        string held_level = ReadShared(level_file);
        Thread.Sleep(2500);
        Assert.AreEqual(held_time, AppDriver.SafeName(timer), "The timer ran while paused.");
        Assert.AreEqual(held_level, ReadShared(level_file), "The live waveform moved while paused.");

        // Resume: The Time Counts On From Where It Held
        pause.Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => AppDriver.SafeName(pause) == ChatCopy.Pause, AppDriver.DefaultTimeout, () => "Resume didn't become Pause.");
        string next = held_time == "0:01" ? "0:02" : "0:03";
        Wait.Until(() => AppDriver.SafeName(timer) == next, AppDriver.DefaultTimeout, () => $"The timer didn't count on to {next}: '{AppDriver.SafeName(timer)}'.");
        Assert.AreEqual(timer_box, timer.BoundingRectangle, "The timer changed size as it counted, which moves the live waveform.");

        App.Find("Chat.StopRecording").Patterns.Invoke.Pattern.Invoke();
        AutomationElement length = App.Find("Chat.VoiceLength");
        Wait.Until(() => AppDriver.SafeName(length) == "0:03", AppDriver.DefaultTimeout, () => $"The preview chip shows '{AppDriver.SafeName(length)}', not 0:03.");
    }

    /// <summary>
    /// A recording whose file can't be finished (the test hook's "unfinished" microphone) isn't lost silently: Done says
    /// "Couldn't save this recording. Try again." over the chips, the mic is back and the temp file is gone.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Record_DoneFails_SaysSo()
    {
        await File.WriteAllTextAsync(Path.Combine(App.DataDir, "microphone.txt"), "unfinished");
        await OpenMediaChatAsync();

        App.Find("Chat.Record").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => VoiceTempFiles().Length == 1, AppDriver.DefaultTimeout, () => "Recording wrote no temp file.");
        App.Find("Chat.StopRecording").Patterns.Invoke.Pattern.Invoke();

        AutomationElement note = App.Find("Chat.AttachError");
        Wait.Until(() => AppDriver.SafeName(note) == ChatCopy.RecordingFailed, AppDriver.DefaultTimeout, () => $"The failed recording said '{AppDriver.SafeName(note)}'.");
        Wait.Until(() => App.TryFind("Chat.StopRecording") is null, AppDriver.DefaultTimeout, () => "The recording bar stayed after the failure.");
        Assert.IsNull(App.TryFind("Chat.VoiceLength"), "A failed recording left a preview chip.");
        Assert.IsEmpty(VoiceTempFiles(), "A failed recording left its temp file.");
        Assert.IsTrue(App.Find("Chat.Record").IsEnabled, "The mic isn't back after the failure.");
    }

    /// <summary>
    /// Closing the window while recording drops the recording with its file, and the reopened window's chat is back at
    /// the mic: before, the kept chat stayed recording with no recorder, so its bar came back with a Done that did nothing.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Record_CloseWindow_ReopensAtMic()
    {
        await FakeMicrophoneAsync();
        await OpenMediaChatAsync();

        App.Find("Chat.Record").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => VoiceTempFiles().Length == 1, AppDriver.DefaultTimeout, () => "Recording wrote no temp file.");
        App.Find("Chat.RecordTimer");

        CloseMainWindow();
        Tray.LeftClick();
        App.Find("Chat.Record");
        Wait.Until(() => App.TryFind("Chat.StopRecording") is null, AppDriver.DefaultTimeout, () => "The reopened chat still shows the recording bar.");
        Assert.IsEmpty(VoiceTempFiles(), "Closing the window kept the recording's temp file.");
    }

    /// <summary>
    /// Cancel recording drops the recording: its temp file is deleted, nothing is sent and the mic is back.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Record_Cancel_DeletesTemp()
    {
        await FakeMicrophoneAsync();
        await OpenMediaChatAsync();

        App.Find("Chat.Record").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => VoiceTempFiles().Length == 1, AppDriver.DefaultTimeout, () => "Recording wrote no temp file.");
        Assert.AreEqual(ChatCopy.CancelRecording, AppDriver.SafeName(App.Find("Chat.CancelRecording")), "Cancel recording has another name.");
        Assert.AreEqual(ChatCopy.Done, AppDriver.SafeName(App.Find("Chat.StopRecording")), "Done has another name.");

        App.Find("Chat.CancelRecording").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => VoiceTempFiles().Length == 0, AppDriver.DefaultTimeout, () => "Cancel recording kept the temp file.");
        Wait.Until(() => App.TryFind("Chat.StopRecording") is null, AppDriver.DefaultTimeout, () => "The recording bar stayed after Cancel.");
        Assert.IsNull(App.TryFind("Chat.VoiceLength"), "A cancelled recording left a preview chip.");
        Assert.IsEmpty(Server.Phone.Sent, "Cancel recording sent something.");
    }

    /// <summary>
    /// With microphone access off, the mic shows the app's microphone dialog (title and text), no recording starts,
    /// and Cancel closes it without opening anything.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task MicDenied_ShowsMicrophoneDialog()
    {
        File.WriteAllText(Path.Combine(App.DataDir, "microphone.txt"), "denied");
        await OpenMediaChatAsync();

        App.Find("Chat.Record").Patterns.Invoke.Pattern.Invoke();
        AutomationElement dialog = App.Find("Dialog.Microphone");
        foreach (string text in (string[])[ChatCopy.MicrophoneTitle, ChatCopy.MicrophoneText])
        {
            Wait.Until(() => AppDriver.Search(dialog, e => AppDriver.SafeName(e) == text) is not null, AppDriver.DefaultTimeout,
                () => $"The microphone dialog didn't show '{text}'.");
        }

        Assert.IsNull(App.TryFind("Chat.StopRecording"), "A recording started with the microphone off.");
        Assert.IsEmpty(VoiceTempFiles(), "A refused recording left a temp file.");

        DialogButton(dialog, ChatCopy.Cancel).Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Dialog.Microphone");
        Assert.IsFalse(File.Exists(Path.Combine(App.DataDir, "launched.txt")), "Cancel opened something.");
        Assert.IsTrue(App.Find("Chat.Record").IsEnabled, "The mic isn't back after Cancel.");
    }

    /// <summary>
    /// The microphone dialog's Open settings opens Windows' microphone privacy page (the test hook notes the address
    /// instead of launching it).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task MicDenied_OpenSettings_OpensMicrophonePage()
    {
        File.WriteAllText(Path.Combine(App.DataDir, "microphone.txt"), "denied");
        await OpenMediaChatAsync();

        App.Find("Chat.Record").Patterns.Invoke.Pattern.Invoke();
        DialogButton(App.Find("Dialog.Microphone"), ChatCopy.OpenSettings).Patterns.Invoke.Pattern.Invoke();
        string launched = Path.Combine(App.DataDir, "launched.txt");
        Wait.Until(() => File.Exists(launched), AppDriver.DefaultTimeout, () => "Open settings opened nothing.");
        Assert.AreEqual("ms-settings:privacy-microphone", File.ReadAllLines(launched)[^1], "Open settings opened another page.");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // The Test Hook's Microphone Records The Voice Fixture
    private async Task FakeMicrophoneAsync()
    {
        string fixture = Path.Combine(App.DataDir, "voice-fixture.m4a");
        await File.WriteAllBytesAsync(fixture, await VoiceFixtureAsync());
        await File.WriteAllTextAsync(Path.Combine(App.DataDir, "microphone.txt"), fixture);
    }

    // The Voice Player Really Plays: Its Own State Is Playing, Its Position Passes 0.3 s, It Knows Its Length, And It Plays
    // As Media (A Communications Stream Goes To The Communications Device, Silent On The Owner's Speakers)
    private void WaitForPlayback()
    {
        string state  = Path.Combine(App.DataDir, "voice-player.txt");
        string failed = Path.Combine(App.DataDir, "voice-failed.txt");
        string last   = string.Empty;
        Wait.Until(() =>
        {
            Assert.IsFalse(File.Exists(failed), $"The voice player failed: {(File.Exists(failed) ? File.ReadAllText(failed) : string.Empty)}.");
            last = File.Exists(state) ? ReadShared(state) : string.Empty;
            string[] parts = last.Split(' ');
            return parts.Length == 4
                && parts[0] == "Playing"
                && double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) > 0.3
                && double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) > 0
                && parts[3] == "Media";
        }, AppDriver.DefaultTimeout, () => $"The voice player never played: '{last}'.");
    }

    // A Dialog's Button, By Name
    private static AutomationElement DialogButton(AutomationElement dialog, string name)
    {
        AutomationElement? button = null;
        Wait.Until(() => (button = AppDriver.Search(dialog, e => e.ControlType == FlaUI.Core.Definitions.ControlType.Button && AppDriver.SafeName(e) == name)) is not null,
            AppDriver.DefaultTimeout, () => $"The dialog has no '{name}' button.");
        return button!;
    }

    // A File The App May Be Writing
    private static string ReadShared(string path)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    // Recordings Waiting In The App's Private Temp Folder
    private string[] VoiceTempFiles()
    {
        string folder = Path.Combine(App.DataDir, "Temp", "Media");
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "voice-*.m4a") : [];
    }

    /// <summary>
    /// Makes a 3-second voice message: a tone that swells and fades (so its waveform has a shape), 8 kHz mono, encoded
    /// to M4A/AAC by Windows' transcoder as the app's recorder would.
    /// </summary>
    /// <returns>The M4A file's bytes.</returns>
    internal static async Task<byte[]> VoiceFixtureAsync()
    {
        const int Rate    = 8000;
        const int Seconds = 3;

        // A 16-Bit PCM WAV File
        using var wav    = new MemoryStream();
        using var writer = new BinaryWriter(wav);
        int samples      = Rate * Seconds;
        writer.Write("RIFF"u8);
        writer.Write(36 + (samples * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (int i = 0; i < samples; i++)
        {
            double swell = Math.Sin(Math.PI * i / samples);
            writer.Write((short)(12000 * swell * Math.Sin(2 * Math.PI * 300 * i / Rate)));
        }

        writer.Flush();

        // To M4A
        using var source = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var target = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await source.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(wav.ToArray()));
        source.Seek(0);
        var profile  = Windows.Media.MediaProperties.MediaEncodingProfile.CreateM4a(Windows.Media.MediaProperties.AudioEncodingQuality.Low);
        var prepared = await new Windows.Media.Transcoding.MediaTranscoder().PrepareStreamTranscodeAsync(source, target, profile);
        Assert.IsTrue(prepared.CanTranscode, $"Windows can't make the voice fixture: {prepared.FailureReason}.");
        await prepared.TranscodeAsync();

        using var m4a = new MemoryStream();
        target.Seek(0);
        await System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(target).CopyToAsync(m4a);
        return m4a.ToArray();
    }

    // Whether Ctrl + Shift + The Key Is Free: No Other App Holds It As A Global Hotkey (Which Windows Would Hand That App)
    private static bool HotkeyFree(VirtualKeyShort key)
    {
        const uint ControlShiftNoRepeat = 0x0002 | 0x0004 | 0x4000;
        const int ProbeId               = 0x7A11;

        if (!RegisterHotKey(IntPtr.Zero, ProbeId, ControlShiftNoRepeat, (uint)key))
        {
            return false;
        }

        UnregisterHotKey(IntPtr.Zero, ProbeId);
        return true;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr window, int id);

    // Writes Files For The App's Next Pick (The Test Hook Answers The Picker With Them)
    private void Pick(params (string Name, byte[] Bytes)[] files)
    {
        string folder = Path.Combine(App.DataDir, "picks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        List<string> paths = [];
        foreach ((string name, byte[] bytes) in files)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);
            paths.Add(path);
        }

        File.WriteAllLines(Path.Combine(App.DataDir, "pick.txt"), paths);
    }

    // A Photo's Or File Card's Bubble Menu, From The Keyboard (Shift + F10 On It, Focused)
    private void OpenMenu(AutomationElement part)
    {
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
        part.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.F10);
    }

    // Two Halves: Red Left, Blue Right
    private static (byte R, byte G, byte B) SplitColors(int x, int y, int width) => x < width / 2 ? ((byte)220, (byte)40, (byte)40) : ((byte)40, (byte)60, (byte)220);

    // Seeds Mia's Photo Chat, Pairs, Opens The Main Window And Then Her Chat
    private async Task OpenMediaChatAsync()
    {
        Server.Phone.Chats.AddPhotoChat();
        await OpenMainAsync();
        App.Find("Row.c-media").Patterns.SelectionItem.Pattern.Select();
        App.Find("Chat.Draft");
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
    }

    // The Chat's First Photo, Or Null
    private AutomationElement? FirstPhoto() =>
        App.TryFind("Chat.Messages") is { } list
            ? AppDriver.Search(list, e => e.Properties.AutomationId.ValueOrDefault?.StartsWith("Bubble.Photo.", StringComparison.Ordinal) == true)
            : null;

    // The Newest Photo That Isn't The Seeded One (A Sent Photo), Or Null
    private AutomationElement? SentPhoto(string seeded) =>
        App.TryFind("Chat.Messages") is { } list
            ? AppDriver.Search(list, e => e.Properties.AutomationId.ValueOrDefault is { } id && id.StartsWith("Bubble.Photo.", StringComparison.Ordinal) && id != seeded)
            : null;

    // Waits For The Photo's Load State (Its Item Status Under The Test Hooks)
    private static void WaitForStatus(AutomationElement photo, string status) =>
        Wait.Until(() => photo.Properties.ItemStatus.ValueOrDefault == status, AppDriver.DefaultTimeout,
            () => $"The photo stayed '{photo.Properties.ItemStatus.ValueOrDefault}', not '{status}'.");

    // Whether Any Text Under The Element Reads This
    private static bool HasText(AutomationElement root, string text) => AppDriver.Search(root, e => AppDriver.SafeName(e) == text) is not null;

    // The Screen Pixel In The Middle Of An Element, Read Once Its Fade Has Finished
    private System.Drawing.Color CenterPixel(AutomationElement element)
    {
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
        Thread.Sleep(500);
        System.Drawing.Rectangle box = element.BoundingRectangle;
        using FlaUI.Core.Capturing.CaptureImage capture = FlaUI.Core.Capturing.Capture.Rectangle(new System.Drawing.Rectangle(box.X + (box.Width / 2), box.Y + (box.Height / 2), 1, 1));
        return capture.Bitmap.GetPixel(0, 0);
    }

    // The Message List Sits On Its Newest Message: Less Than A Pixel Short Of Its End
    private void AssertAtNewest(string what)
    {
        AutomationElement list = App.Find("Chat.Messages");
        var scroll             = list.Patterns.Scroll.Pattern;
        double view_size       = scroll.VerticalViewSize.ValueOrDefault;
        double viewport        = list.BoundingRectangle.Height;
        double scrollable      = view_size is > 0 and < 100 ? viewport * 100 / view_size - viewport : 0;
        double short_by        = (100 - scroll.VerticalScrollPercent.ValueOrDefault) / 100 * scrollable;
        Assert.IsTrue(short_by < 1, $"{what} left the list {short_by:0.0} px short of the newest message.");
    }

    /// <summary>
    /// Makes a PNG of the given size (8-bit RGB, stored with zlib), each pixel colored by a function.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="color">Gives a pixel's color from its x, its y and the width.</param>
    /// <returns>The PNG file's bytes.</returns>
    internal static byte[] Png(int width, int height, Func<int, int, int, (byte R, byte G, byte B)> color)
    {
        // Rows: A Filter Byte (None), Then RGB
        byte[] raw = new byte[height * ((width * 3) + 1)];
        for (int y = 0, at = 0; y < height; y++)
        {
            raw[at++] = 0;
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = color(x, y, width);
                raw[at++] = r;
                raw[at++] = g;
                raw[at++] = b;
            }
        }

        using var data = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        byte[] header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", data.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream png, string type, byte[] body)
        {
            byte[] length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
            byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. body];
            byte[] crc   = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
            png.Write(length);
            png.Write(typed);
            png.Write(crc);
        }

        static uint Crc32(byte[] bytes)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
                }
            }

            return ~crc;
        }
    }
}
