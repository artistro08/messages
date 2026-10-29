using GMessages.Presentation.Connection;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class AvatarCacheTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly byte[] AnnPhoto = [1, 2, 3];

    private static readonly string[] AnnAndBo = ["p-ann", "p-bo"];

    // =========================================================================
    // RIG
    // =========================================================================

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Controller = new ConnectionController(new AppPaths(Folder), GmEndpoints.Production, TimeProvider.System, NullLoggerFactory.Instance, a => a());
            Store      = new ChatStore(Controller, () => Client, NullLogger.Instance);
            Cache      = new AvatarCache(Store, Folder);
            Client.Photos["p-ann"] = AnnPhoto;
        }

        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "avatar-test-" + Guid.NewGuid().ToString("N"));

        public ConnectionController Controller { get; }

        public FakeMessagingClient Client { get; } = new();

        public ChatStore Store { get; }

        public AvatarCache Cache { get; }

        public void Dispose()
        {
            Store.Dispose();
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        }
    }

    // =========================================================================
    // TESTS
    // =========================================================================

    [TestMethod]
    public async Task RequestsWithin50ms_OneBatchCall()
    {
        using var rig = new Rig();

        Task<byte[]?> ann  = rig.Cache.GetAsync("p-ann", Ct);
        Task<byte[]?> bo   = rig.Cache.GetAsync("p-bo", Ct);
        Task<byte[]?> ann2 = rig.Cache.GetAsync("p-ann", Ct);
        await Task.WhenAll(ann, bo, ann2);

        Assert.HasCount(1, rig.Client.ThumbnailCalls);
        CollectionAssert.AreEquivalent(AnnAndBo, rig.Client.ThumbnailCalls[0].ToArray());
        CollectionAssert.AreEqual(AnnPhoto, ann.Result);
        CollectionAssert.AreEqual(AnnPhoto, ann2.Result);
        Assert.IsNull(bo.Result);
    }

    [TestMethod]
    public async Task NoPhoto_RememberedAsNull()
    {
        using var rig = new Rig();

        Assert.IsNull(await rig.Cache.GetAsync("p-bo", Ct));
        Assert.IsNull(await rig.Cache.GetAsync("p-bo", Ct));

        Assert.HasCount(1, rig.Client.ThumbnailCalls);
    }

    [TestMethod]
    public async Task Lru_EvictsOldest()
    {
        using var rig = new Rig();

        // 201 IDs In One Batch: The First Is The Oldest And Falls Out
        await Task.WhenAll(Enumerable.Range(0, 201).Select(i => rig.Cache.GetAsync("p-" + i, Ct)));
        await rig.Cache.GetAsync("p-200", Ct);
        Assert.HasCount(1, rig.Client.ThumbnailCalls, "newest still cached");

        await rig.Cache.GetAsync("p-0", Ct);
        Assert.HasCount(2, rig.Client.ThumbnailCalls, "oldest evicted");
    }

    [TestMethod]
    public async Task FileUri_WritesOnceAndReuses()
    {
        using var rig = new Rig();

        string? uri = await rig.Cache.GetFileUriAsync("p-ann", Ct);
        Assert.IsNotNull(uri);

        string path = new Uri(uri).LocalPath;
        CollectionAssert.AreEqual(AnnPhoto, File.ReadAllBytes(path));
        Assert.EndsWith(".png", path);
        Assert.MatchesRegex(@"^[0-9a-f]{32}\.png$", Path.GetFileName(path), "a hash, never the ID, names the file");

        // A Second Cache (Like The Notifications' Own) Reuses The File Without Asking The Phone
        var other = new AvatarCache(rig.Store, rig.Folder);
        Assert.AreEqual(uri, await other.GetFileUriAsync("p-ann", Ct));
        Assert.HasCount(1, rig.Client.ThumbnailCalls);

        // No Photo: No File
        Assert.IsNull(await rig.Cache.GetFileUriAsync("p-bo", Ct));
    }
}
