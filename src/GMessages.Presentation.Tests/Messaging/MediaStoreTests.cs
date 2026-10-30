using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Presentation.Tests.Messaging;

/// <summary>
/// Checks <see cref="MediaStore"/>: the source order, the memory cache and shared downloads, the temp folder and its
/// file names, the full-size ask, and clearing.
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
        new ChatMessage(id, string.Empty, "c1", "p1", DateTimeOffset.UnixEpoch, false, MessageKind.Photo, string.Empty, MessageStatusType.IncomingComplete) { Media = media };

    /// <summary>
    /// Two callers asking for one media ID at once share one download, and a later ask comes from memory.
    /// </summary>
    [TestMethod]
    public async Task Get_CachesAndDedupes()
    {
        using var rig           = new Rig();
        rig.Client.Media["m-1"] = [1, 2, 3];
        rig.Client.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Two At Once, One Download
        Task<byte[]> first  = rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct);
        Task<byte[]> second = rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct);
        rig.Client.DownloadGate.SetResult();
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await first);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await second);

        // From Memory
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct));
        Assert.HasCount(1, rig.Client.DownloadCalls);
    }

    /// <summary>
    /// The full file is preferred, else the thumbnail; asking for the thumbnail prefers it.
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
    /// Inline data comes back as it is, with no download; a pending part throws.
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
    /// A failed download is not remembered: the next ask tries again.
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
    /// Clear empties memory (the next ask downloads again) and the temp folder.
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
    /// A Clear during a download still hands the caller its bytes, but keeps nothing: the next ask downloads again.
    /// </summary>
    [TestMethod]
    public async Task Clear_DuringDownload_NotCached()
    {
        using var rig           = new Rig();
        rig.Client.Media["m-1"] = [1, 2, 3];
        rig.Client.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<byte[]> held = rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct);
        rig.Store.Clear();
        rig.Client.DownloadGate.SetResult();

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await held);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await rig.Store.GetAsync(Part("m-1"), thumbnail: false, Ct));
        Assert.HasCount(2, rig.Client.DownloadCalls);
    }

    /// <summary>
    /// A Clear (sign-out) during a save's download means no decrypted file is written.
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

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => save);
        Assert.IsFalse(Directory.Exists(rig.Folder) && Directory.EnumerateFileSystemEntries(rig.Folder).Any());
    }

    /// <summary>
    /// Saved files land in their own folder under the temp folder, with a safe name.
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
    /// File names: separators and refused characters, device names, length, and an empty name.
    /// </summary>
    /// <param name="name">The sender's name.</param>
    /// <param name="mime">The mime type.</param>
    /// <param name="expected">The safe name.</param>
    [TestMethod]
    [DataRow("photo.jpg", "image/jpeg", "photo.jpg")]
    [DataRow("a/b\\c:d*e?f\"g<h>i|j.png", "image/png", "a_b_c_d_e_f_g_h_i_j.png")]
    [DataRow("..\\up.txt", "text/plain", "___up.txt")]
    [DataRow("CON", "text/plain", "_CON")]
    [DataRow("nul.txt", "text/plain", "_nul.txt")]
    [DataRow("com1.tar.gz", "application/gzip", "_com1.tar.gz")]
    [DataRow("console.txt", "text/plain", "console.txt")]
    [DataRow(" . ", "image/jpeg", "attachment.jpg")]
    [DataRow("", "audio/mp4", "attachment.m4a")]
    [DataRow("", "application/vnd.ms-excel", "attachment")]
    [DataRow("tab\there.txt", "text/plain", "tab_here.txt")]
    public void SanitizeName_Cases(string name, string mime, string expected) =>
        Assert.AreEqual(expected, MediaStore.SanitizeName(name, mime));

    /// <summary>
    /// Programs, scripts, installers and shortcuts never open, however the name hides the extension; other files do.
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
    [DataRow("notes.pdf", "application/pdf", true)]
    [DataRow("photo.jpg", "image/jpeg", true)]
    [DataRow("clip.mp4", "video/mp4", true)]
    [DataRow("", "image/jpeg", true)]
    public void CanOpen_NeverPrograms(string name, string mime, bool expected) =>
        Assert.AreEqual(expected, MediaStore.CanOpen(name, mime));

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
    /// A thumbnail-only photo asks the phone for its full size once; a full one or a repeat asks nothing.
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

        // Asked Again After A Clear
        rig.Store.Clear();
        await rig.Store.RequestFullSizeAsync(photo);
        Assert.HasCount(2, rig.Client.FullSizeCalls);
    }
}
