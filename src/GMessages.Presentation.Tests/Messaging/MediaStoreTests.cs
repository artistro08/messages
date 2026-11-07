using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Presentation.Tests.Messaging;

/// <summary>
/// Tests <see cref="MediaStore"/>: the source order, the memory cache, shared downloads, the temp folder,
/// the file names, the full-size request, and the clear.
/// </summary>
[TestClass]
public sealed class MediaStoreTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Folder = Path.Combine(Path.GetTempPath(), "media-test-" + Guid.NewGuid().ToString("N"));
            Store  = new MediaStore(() => Client, Folder, NullLogger.Instance);
        }

        public FakeMessagingClient Client { get; } = new();

        public string Folder { get; }

        public MediaStore Store { get; }

        public void Dispose()
        {
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        }
    }

    private static MediaPart Part(string mediaId = "", string thumbnailId = "", byte[]? inline = null, string name = "a.jpg") =>
        new(mediaId, new byte[32], thumbnailId, new byte[32], inline ?? [], name, 0, 0, 0, MessageKind.Photo, "image/jpeg");

    private static ChatMessage Message(string id, MediaPart? media) =>
        new(id, string.Empty, "c1", "p1", DateTimeOffset.UnixEpoch, false, MessageKind.Photo, string.Empty, MessageStatusType.IncomingComplete) { Media = media };

    /// <summary>
    /// Two callers that ask for one media ID at the same time share one download. A later request uses the memory.
    /// </summary>
    [TestMethod]
    public async Task Get_CachesAndDedupes()
    {
        using var rig           = new Rig();
        rig.Client.Media["m-1"] = [1, 2, 3];
        rig.Client.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<byte[]> first  = rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct);
        Task<byte[]> second = rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct);
        rig.Client.DownloadGate.SetResult();
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await first);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await second);

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct));
        Assert.HasCount(1, rig.Client.DownloadCalls);
    }

    /// <summary>
    /// The store prefers the full file and uses the thumbnail when no full file exists. A request for the thumbnail
    /// prefers the thumbnail.
    /// </summary>
    [TestMethod]
    public async Task Get_SourceOrder()
    {
        using var rig             = new Rig();
        rig.Client.Media["full"]  = [1];
        rig.Client.Media["thumb"] = [2];

        CollectionAssert.AreEqual(new byte[] { 1 }, await rig.Store.GetAsync(Part("full", "thumb"), thumbnail: false, Ct));
        CollectionAssert.AreEqual(new byte[] { 2 }, await rig.Store.GetAsync(Part("full", "thumb"), thumbnail: true, Ct));
        CollectionAssert.AreEqual(new byte[] { 2 }, await rig.Store.GetAsync(Part(thumbnailId: "thumb"), thumbnail: false, Ct));
    }

    /// <summary>
    /// Inline data returns unchanged and needs no download. A pending part throws.
    /// </summary>
    [TestMethod]
    public async Task Get_InlineData_NoDownload()
    {
        using var rig = new Rig();

        CollectionAssert.AreEqual(new byte[] { 7, 8 }, await rig.Store.GetAsync(Part(inline: [7, 8]), thumbnail: false, Ct));
        CollectionAssert.AreEqual(new byte[] { 7, 8 }, await rig.Store.GetAsync(Part(inline: [7, 8]), thumbnail: true, Ct));
        Assert.IsEmpty(rig.Client.DownloadCalls);
        Assert.ThrowsExactly<MediaPendingException>(() => { _ = rig.Store.GetAsync(Part(), thumbnail: false, Ct); });
    }

    /// <summary>
    /// The store does not remember a failed download. The next request tries again.
    /// </summary>
    [TestMethod]
    public async Task Get_FailedDownload_TriesAgain()
    {
        using var rig = new Rig();

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct));
        rig.Client.Media["m-1"] = [5];

        CollectionAssert.AreEqual(new byte[] { 5 }, await rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct));
    }

    /// <summary>
    /// A file in the temp folder has the mark of the web, as a browser download has.
    /// </summary>
    [TestMethod]
    public async Task SaveToTemp_WritesMarkOfTheWeb()
    {
        using var rig           = new Rig();
        rig.Client.Media["m-1"] = [1, 2, 3];
        string path             = await rig.Store.SaveToTempAsync(Part("m-1"), Ct);

        string mark = File.ReadAllText(path + ":Zone.Identifier");

        Assert.AreEqual("[ZoneTransfer]\r\nZoneId=3\r\n", mark);
    }

    /// <summary>
    /// A download writes the file to the folder under its own name. When the name is taken, the names are
    /// "name (1)" and "name (2)". Each file has the bytes and the mark of the web.
    /// </summary>
    [TestMethod]
    public async Task SaveToFolder_UniqueNamesAndMark()
    {
        using var rig           = new Rig();
        rig.Client.Media["m-1"] = [1, 2, 3];
        string folder           = Path.Combine(rig.Folder, "Downloads");
        Directory.CreateDirectory(folder);

        string first  = await rig.Store.SaveToFolderAsync(Part("m-1", name: "beach.jpg"), folder, Ct);
        string second = await rig.Store.SaveToFolderAsync(Part("m-1", name: "beach.jpg"), folder, Ct);
        string third  = await rig.Store.SaveToFolderAsync(Part("m-1", name: "beach.jpg"), folder, Ct);

        Assert.AreEqual(Path.Combine(folder, "beach.jpg"), first);
        Assert.AreEqual(Path.Combine(folder, "beach (1).jpg"), second);
        Assert.AreEqual(Path.Combine(folder, "beach (2).jpg"), third);
        foreach (string path in (string[])[first, second, third])
        {
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            Assert.AreEqual("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(path + ":Zone.Identifier"));
        }
    }

    /// <summary>
    /// Clear empties the memory and the temp folder. The next request downloads again.
    /// </summary>
    [TestMethod]
    public async Task Clear_EmptiesMemoryAndTemp()
    {
        using var rig           = new Rig();
        rig.Client.Media["m-1"] = [1, 2, 3];
        string path             = await rig.Store.SaveToTempAsync(Part("m-1"), Ct);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));

        rig.Store.Clear();

        Assert.IsTrue(Directory.Exists(rig.Folder));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(rig.Folder));
        await rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct);
        Assert.HasCount(2, rig.Client.DownloadCalls);
    }

    /// <summary>
    /// A Clear during a download, as on sign-out, aborts the request. The caller gets a cancel and the store keeps
    /// nothing. The next request downloads again.
    /// </summary>
    [TestMethod]
    public async Task Clear_DuringDownload_CancelsRequest()
    {
        using var rig           = new Rig();
        rig.Client.Media["m-1"] = [1, 2, 3];
        rig.Client.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<byte[]> held = rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct);
        rig.Store.Clear();

        await Assert.ThrowsAsync<OperationCanceledException>(() => held.WaitAsync(TimeSpan.FromSeconds(5)));
        rig.Client.DownloadGate.SetResult();
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct));
        Assert.HasCount(2, rig.Client.DownloadCalls);
    }

    /// <summary>
    /// A download that never answers ends after the download timeout with a <see cref="TimeoutException"/>. The photo
    /// shows this as failed. A later request tries again.
    /// </summary>
    [TestMethod]
    public async Task StalledDownload_TimesOut()
    {
        using var rig             = new Rig();
        rig.Client.Media["m-1"]   = [1, 2, 3];
        rig.Client.DownloadGate   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Store.DownloadTimeout = () => TimeSpan.FromMilliseconds(200);

        Task<byte[]> stalled = rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct);
        Assert.AreSame(stalled, await Task.WhenAny(stalled, Task.Delay(TimeSpan.FromSeconds(5))), "The stalled download never gave up.");
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => stalled);

        rig.Client.DownloadGate.SetResult();
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct));
    }

    /// <summary>
    /// Google answers with nothing until the upload of the sender is done, for example for a video that just arrives.
    /// The store does not keep the empty answer and asks again until the file is there.
    /// </summary>
    [TestMethod]
    public async Task NotReadyYet_AsksAgain_KeepsNothingEmpty()
    {
        using var rig           = new Rig();
        rig.Client.Media["m-1"] = [];

        Task<byte[]> download = rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct);
        while (true)
        {
            lock (rig.Client.DownloadCalls)
            {
                if (rig.Client.DownloadCalls.Count > 0)
                {
                    break;
                }
            }

            await Task.Delay(20, Ct);
        }

        rig.Client.Media["m-1"] = [1, 2, 3];

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await download.WaitAsync(TimeSpan.FromSeconds(10)));
        lock (rig.Client.DownloadCalls)
        {
            Assert.IsGreaterThanOrEqualTo(2, rig.Client.DownloadCalls.Count);
        }
    }

    /// <summary>
    /// Seeded bytes answer their media ID and need no download.
    /// </summary>
    [TestMethod]
    public async Task Seed_AnswersWithoutDownload()
    {
        using var rig = new Rig();
        rig.Store.Seed("m-1", [4, 5]);

        CollectionAssert.AreEqual(new byte[] { 4, 5 }, await rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct));
        Assert.IsEmpty(rig.Client.DownloadCalls);
    }

    /// <summary>
    /// A seed for a media ID that is still downloading does not fail the download. The download gives its own bytes.
    /// </summary>
    [TestMethod]
    public async Task Seed_DuringDownload_DownloadStillAnswers()
    {
        using var rig           = new Rig();
        rig.Client.Media["m-1"] = [1, 2, 3];
        rig.Client.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<byte[]> download = rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct);
        rig.Store.Seed("m-1", [4, 5]);
        rig.Client.DownloadGate.SetResult();

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await download);
        CollectionAssert.AreEqual(new byte[] { 4, 5 }, await rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct));
    }

    /// <summary>
    /// ClearMemory empties the memory cache. The next request downloads again.
    /// </summary>
    [TestMethod]
    public async Task ClearMemory_EmptiesCache()
    {
        using var rig           = new Rig();
        rig.Client.Media["m-1"] = [9];
        rig.Store.Seed("m-1", [4, 5]);

        rig.Store.ClearMemory();

        CollectionAssert.AreEqual(new byte[] { 9 }, await rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct));
        Assert.HasCount(1, rig.Client.DownloadCalls);
    }

    /// <summary>
    /// A Clear during the download of a save, as on sign-out, stops the store from writing a decrypted file.
    /// </summary>
    [TestMethod]
    public async Task SaveToTemp_AfterClear_WritesNothing()
    {
        using var rig           = new Rig();
        rig.Client.Media["m-1"] = [1, 2, 3];
        rig.Client.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> save = rig.Store.SaveToTempAsync(Part("m-1"), Ct);
        rig.Store.Clear();
        rig.Client.DownloadGate.SetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(() => save);
        Assert.IsFalse(Directory.Exists(rig.Folder) && Directory.EnumerateFileSystemEntries(rig.Folder).Any());
    }

    /// <summary>
    /// A saved file goes to its own folder under the temp folder and has a safe name.
    /// </summary>
    [TestMethod]
    public async Task SaveToTemp_SanitizesName()
    {
        using var rig           = new Rig();
        rig.Client.Media["m-1"] = [1];

        string path = await rig.Store.SaveToTempAsync(Part("m-1", name: @"..\..\evil/CON.txt"), Ct);

        Assert.AreEqual(rig.Folder, Path.GetDirectoryName(Path.GetDirectoryName(path)));
        Assert.AreEqual("______evil_CON.txt", Path.GetFileName(path));
    }

    /// <summary>
    /// The safe name handles separators, refused characters, device names, long names and an empty name.
    /// </summary>
    /// <param name="name">The name that the sender gives.</param>
    /// <param name="mime">The mime type.</param>
    /// <param name="expected">The safe name.</param>
    [TestMethod]
    [DataRow("photo.jpg", "image/jpeg", "photo.jpg")]
    [DataRow("a/b\\c:d*e?f\"g<h>i|j.png", "image/png", "a_b_c_d_e_f_g_h_i_j.png")]
    [DataRow("..\\up.txt", "text/plain", "___up.txt")]
    [DataRow("CON", "text/plain", "_CON.txt")]
    [DataRow("CON", "application/x-unknown-thing", "_CON")]
    [DataRow("image", "image/jpeg", "image.jpg")]
    [DataRow("IMG_0042", "video/quicktime", "IMG_0042.mov")]
    [DataRow("voice", "audio/mpeg", "voice.mp3")]
    [DataRow("party", "image/gif", "party.gif")]
    [DataRow("photo.jpg", "image/png", "photo.jpg")]
    [DataRow("nul.txt", "text/plain", "_nul.txt")]
    [DataRow("com1.tar.gz", "application/gzip", "_com1.tar.gz")]
    [DataRow("console.txt", "text/plain", "console.txt")]
    [DataRow(" . ", "image/jpeg", "attachment.jpg")]
    [DataRow("", "audio/mp4", "attachment.m4a")]
    [DataRow("", "application/vnd.ms-excel", "attachment.xls")]
    [DataRow("tab\there.txt", "text/plain", "tab_here.txt")]
    public void SanitizeName_Cases(string name, string mime, string expected) =>
        Assert.AreEqual(expected, MediaStore.SanitizeName(name, mime));

    /// <summary>
    /// A program, script, installer or shortcut never opens, even when the name hides the extension. Other files open.
    /// </summary>
    [TestMethod]
    [DataRow("setup.exe", "application/octet-stream", false)]
    [DataRow("SETUP.EXE", "application/octet-stream", false)]
    [DataRow("run.bat. . ", "text/plain", false)]
    [DataRow("a.cmd", "text/plain", false)]
    [DataRow("a.ps1", "text/plain", false)]
    [DataRow("a.vbs", "text/plain", false)]
    [DataRow("a.js", "text/javascript", false)]
    [DataRow("a.msi", "application/octet-stream", false)]
    [DataRow("a.msix", "application/octet-stream", false)]
    [DataRow("a.lnk", "application/octet-stream", false)]
    [DataRow("a.scr", "application/octet-stream", false)]
    [DataRow("a.reg", "text/plain", false)]
    [DataRow("a.hta", "text/html", false)]
    [DataRow("a.jar", "application/java-archive", false)]
    [DataRow("photo.jpg.exe", "image/jpeg", false)]
    [DataRow("", "text/js", false)]
    [DataRow("a.url", "application/octet-stream", false)]
    [DataRow("x.exe.", "application/octet-stream", false)]
    [DataRow("x.exe .", "application/octet-stream", false)]
    [DataRow("Setup.ExE", "application/octet-stream", false)]
    [DataRow("notes.pdf", "application/pdf", true)]
    [DataRow("photo.jpg", "image/jpeg", true)]
    [DataRow("clip.mp4", "video/mp4", true)]
    [DataRow("", "image/jpeg", true)]
    public void CanOpen_NeverPrograms(string name, string mime, bool expected) =>
        Assert.AreEqual(expected, MediaStore.CanOpen(name, mime));

    /// <summary>
    /// Every extension that the mime table can give opens, except SVG.
    /// </summary>
    [TestMethod]
    [DataRow("image/jpeg")]
    [DataRow("image/x-ms-bmp")]
    [DataRow("audio/mp4")]
    [DataRow("audio/mpeg")]
    [DataRow("audio/x-wav")]
    [DataRow("video/quicktime")]
    [DataRow("video/3gpp")]
    [DataRow("video/3gpp2")]
    [DataRow("video/x-matroska")]
    [DataRow("video/x-msvideo")]
    [DataRow("text/plain")]
    [DataRow("text/vcard")]
    [DataRow("text/calendar")]
    [DataRow("image/png")]
    [DataRow("image/heic")]
    [DataRow("application/pdf")]
    [DataRow("application/msword")]
    [DataRow("application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [DataRow("application/vnd.ms-excel")]
    [DataRow("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [DataRow("application/vnd.ms-powerpoint")]
    [DataRow("application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [DataRow("audio/flac")]
    [DataRow("audio/opus")]
    [DataRow("video/mpeg")]
    public void CanOpen_EveryExtensionOfType_ExceptSvg_True(string mime) =>
        Assert.IsTrue(MediaStore.CanOpen("file" + MediaStore.ExtensionOf(mime), mime));

    /// <summary>
    /// Photo, video, audio, document and contact types open.
    /// </summary>
    [TestMethod]
    [DataRow(".jpg")]
    [DataRow(".jpeg")]
    [DataRow(".png")]
    [DataRow(".gif")]
    [DataRow(".webp")]
    [DataRow(".bmp")]
    [DataRow(".tif")]
    [DataRow(".tiff")]
    [DataRow(".heic")]
    [DataRow(".heif")]
    [DataRow(".avif")]
    [DataRow(".mp4")]
    [DataRow(".mov")]
    [DataRow(".m4v")]
    [DataRow(".3gp")]
    [DataRow(".3g2")]
    [DataRow(".mkv")]
    [DataRow(".avi")]
    [DataRow(".webm")]
    [DataRow(".mp3")]
    [DataRow(".m4a")]
    [DataRow(".ogg")]
    [DataRow(".wav")]
    [DataRow(".aac")]
    [DataRow(".amr")]
    [DataRow(".flac")]
    [DataRow(".opus")]
    [DataRow(".mpeg")]
    [DataRow(".mpg")]
    [DataRow(".3gpp")]
    [DataRow(".pdf")]
    [DataRow(".txt")]
    [DataRow(".csv")]
    [DataRow(".rtf")]
    [DataRow(".vcf")]
    [DataRow(".ics")]
    [DataRow(".doc")]
    [DataRow(".docx")]
    [DataRow(".xls")]
    [DataRow(".xlsx")]
    [DataRow(".ppt")]
    [DataRow(".pptx")]
    [DataRow(".odt")]
    public void CanOpen_Allowlisted_True(string extension) =>
        Assert.IsTrue(MediaStore.CanOpen("file" + extension, "application/octet-stream"));

    /// <summary>
    /// Web pages, SVG, themes, packages, certificates, shortcuts and programs never open.
    /// </summary>
    [TestMethod]
    [DataRow(".html")]
    [DataRow(".htm")]
    [DataRow(".mht")]
    [DataRow(".svg")]
    [DataRow(".theme")]
    [DataRow(".themepack")]
    [DataRow(".ppkg")]
    [DataRow(".wsb")]
    [DataRow(".cer")]
    [DataRow(".crt")]
    [DataRow(".lnk")]
    [DataRow(".exe")]
    [DataRow(".udl")]
    [DataRow(".contact")]
    public void CanOpen_Risky_False(string extension) =>
        Assert.IsFalse(MediaStore.CanOpen("file" + extension, "application/octet-stream"));

    /// <summary>
    /// A right-to-left override cannot disguise an extension, because the name loses the control character.
    /// </summary>
    [TestMethod]
    public void SanitizeName_StripsBidiOverride() =>
        Assert.DoesNotContain("‮", MediaStore.SanitizeName("photo‮gpj.exe", "image/jpeg"));

    /// <summary>
    /// The extension of a mime type, as Save as offers it. Each common photo, video and audio type has its usual
    /// extension. A type with no short extension has none.
    /// </summary>
    /// <param name="mime">The mime type.</param>
    /// <param name="expected">The extension.</param>
    [TestMethod]
    [DataRow("image/jpeg", ".jpg")]
    [DataRow("image/png", ".png")]
    [DataRow("image/gif", ".gif")]
    [DataRow("image/webp", ".webp")]
    [DataRow("image/heic", ".heic")]
    [DataRow("video/mp4", ".mp4")]
    [DataRow("video/quicktime", ".mov")]
    [DataRow("video/3gpp", ".3gp")]
    [DataRow("audio/mp4", ".m4a")]
    [DataRow("audio/mpeg", ".mp3")]
    [DataRow("audio/amr", ".amr")]
    [DataRow("audio/ogg", ".ogg")]
    [DataRow("application/pdf", ".pdf")]
    [DataRow("text/x-vcard", ".vcf")]
    [DataRow("IMAGE/JPEG; charset=binary", ".jpg")]
    [DataRow("application/msword", ".doc")]
    [DataRow("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx")]
    [DataRow("application/vnd.ms-excel", ".xls")]
    [DataRow("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx")]
    [DataRow("application/vnd.ms-powerpoint", ".ppt")]
    [DataRow("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx")]
    [DataRow("audio/flac", ".flac")]
    [DataRow("video/mpeg", ".mpeg")]
    [DataRow("", "")]
    public void ExtensionOf_CommonTypes(string mime, string expected) =>
        Assert.AreEqual(expected, MediaStore.ExtensionOf(mime));

    /// <summary>
    /// A long name is cut to the limit and keeps its extension.
    /// </summary>
    [TestMethod]
    public void SanitizeName_CapsLength()
    {
        string safe = MediaStore.SanitizeName(new string('x', 300) + ".jpeg", "image/jpeg");

        Assert.HasCount(MediaStore.MaxNameLength, safe);
        Assert.EndsWith(".jpeg", safe);
    }

    /// <summary>
    /// A photo with only a thumbnail asks the phone for the full size one time. A full photo, or a repeat request,
    /// asks nothing.
    /// </summary>
    [TestMethod]
    public async Task FullSize_RequestedOnce()
    {
        using var rig      = new Rig();
        MediaPart thumb    = Part(thumbnailId: "thumb") with { ActionMessageId = "a-1" };
        ChatMessage photo  = Message("m-1", thumb);

        await rig.Store.RequestFullSizeAsync(photo);
        await rig.Store.RequestFullSizeAsync(photo);
        await rig.Store.RequestFullSizeAsync(Message("m-2", Part("full", "thumb")));
        await rig.Store.RequestFullSizeAsync(Message("m-3", null));

        Assert.HasCount(1, rig.Client.FullSizeCalls);
        Assert.AreEqual(("m-1", "a-1"), rig.Client.FullSizeCalls[0]);

        rig.Store.Clear();
        await rig.Store.RequestFullSizeAsync(photo);
        Assert.HasCount(2, rig.Client.FullSizeCalls);
    }

    /// <summary>
    /// A video without a thumbnail from the phone gets one frame, made one time from its bytes and kept as a file.
    /// A repeat request reads the file and makes or downloads nothing. A clear deletes the file.
    /// </summary>
    [TestMethod]
    public async Task Frame_MadeOnce_KeptUntilClear()
    {
        using var rig           = new Rig();
        rig.Client.Media["v-1"] = [1, 2, 3];
        var made                = new List<byte[]>();
        Task<byte[]> Extract(byte[] video, string mime)
        {
            made.Add(video);
            return Task.FromResult<byte[]>([9, 9]);
        }

        CollectionAssert.AreEqual(new byte[] { 9, 9 }, await rig.Store.GetFrameAsync(Part("v-1"), Extract, Ct));
        CollectionAssert.AreEqual(new byte[] { 9, 9 }, await rig.Store.GetFrameAsync(Part("v-1"), Extract, Ct));
        Assert.HasCount(1, made);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, made[0]);
        Assert.HasCount(1, rig.Client.DownloadCalls);
        Assert.HasCount(1, Directory.GetFiles(Path.Combine(rig.Folder, "Frames")));

        rig.Store.Clear();
        Assert.IsFalse(Directory.Exists(Path.Combine(rig.Folder, "Frames")), "Clear left the frames.");
    }

    /// <summary>
    /// The thumbnail from the phone is the frame when the phone sent one. A video that is still on its way gets a frame
    /// from its own bytes. The store keeps neither frame as a file.
    /// </summary>
    [TestMethod]
    public async Task Frame_PhoneThumbnail_ThenInlineBytes()
    {
        using var rig             = new Rig();
        rig.Client.Media["thumb"] = [5];
        int made                  = 0;
        Task<byte[]> Extract(byte[] video, string mime)
        {
            made++;
            return Task.FromResult<byte[]>([.. video, 7]);
        }

        CollectionAssert.AreEqual(new byte[] { 5 }, await rig.Store.GetFrameAsync(Part("full", "thumb"), Extract, Ct));
        CollectionAssert.AreEqual(new byte[] { 4, 7 }, await rig.Store.GetFrameAsync(Part(inline: [4]), Extract, Ct));
        Assert.AreEqual(1, made);
        Assert.IsFalse(Directory.Exists(Path.Combine(rig.Folder, "Frames")), "A frame not made from a downloaded video was kept.");
    }
}
