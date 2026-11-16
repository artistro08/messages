using System.Net;
using System.Security.Cryptography;
using GMessages.Presentation.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Presentation.Tests.Updates;

/// <summary>
/// Checks <see cref="Updater"/>: a release counts only when it's newer and has the MSI for this PC, and a download is
/// kept only when it matches the digest GitHub keeps.
/// </summary>
[TestClass]
public sealed class UpdaterTests
{
    private static GitHubRelease Release(string tag, params string[] assets) =>
        new(tag, "https://github.com/artistro08/messages/releases/tag/" + tag, [.. assets.Select(name => new GitHubAsset(name, "https://example.test/" + name, null, 1))]);

    [TestMethod]
    [DataRow("v1.0.1", "1.0.0", true)]
    [DataRow("v1.1.0", "1.0.9", true)]
    [DataRow("v1.0.0", "1.0.0", false)]
    [DataRow("v1.0", "1.0.0", false)]
    [DataRow("v0.9.9", "1.0.0", false)]
    [DataRow("nightly", "1.0.0", false)]
    public void PickInstaller_OnlyForANewerRelease(string tag, string current, bool expected)
    {
        string version = tag.TrimStart('v');
        var release    = Release(tag, $"Messages-{version}-x64.msi", $"Messages-{version}-arm64.msi");

        Assert.AreEqual(expected, Updater.PickInstaller(release, current, "Messages", "x64") is not null);
    }

    [TestMethod]
    public void PickInstaller_PicksThisPcsArchitecture()
    {
        var release = Release("v1.0.1", "Messages-1.0.1-x64.msi", "Messages-1.0.1-arm64.msi");

        Assert.AreEqual("Messages-1.0.1-arm64.msi", Updater.PickInstaller(release, "1.0.0", "Messages", "arm64")?.Name);
    }

    [TestMethod]
    public void PickInstaller_NoMsiForThisPc_IsNull() =>
        Assert.IsNull(Updater.PickInstaller(Release("v1.0.1", "Messages-1.0.1-x64.msi"), "1.0.0", "Messages", "arm64"));

    [TestMethod]
    public async Task Download_KeepsAMatchingFile_AndRefusesAMismatch()
    {
        byte[] msi    = [1, 2, 3, 4];
        string digest = "sha256:" + Convert.ToHexString(SHA256.HashData(msi)).ToLowerInvariant();
        string folder = Path.Combine(Path.GetTempPath(), "updater-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A matching digest gives the checked file
            string? path = await DownloadWith(digest, msi, folder);
            Assert.IsNotNull(path);
            CollectionAssert.AreEqual(msi, await File.ReadAllBytesAsync(path));

            // A mismatch, or no digest at all, gives nothing and leaves no file
            Directory.Delete(folder, recursive: true);
            Assert.IsNull(await DownloadWith("sha256:" + new string('0', 64), msi, folder));
            Assert.IsNull(await DownloadWith(null, msi, folder));
            Assert.AreEqual(0, Directory.Exists(folder) ? Directory.GetFiles(folder).Length : 0);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    // A release one version above the running app, served by a fake GitHub
    private static async Task<string?> DownloadWith(string? digest, byte[] msi, string folder)
    {
        var next     = new Version(AppInfo.Version) is var v ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0) + 1}" : "";
        string name  = $"{AppInfo.DisplayName}-{next}-x64.msi";
        string json  = $$"""{"tag_name":"v{{next}}","html_url":"https://github.test/r","assets":[{"name":"{{name}}","browser_download_url":"https://github.test/{{name}}","digest":{{(digest is null ? "null" : $"\"{digest}\"")}},"size":{{msi.Length}}}]}""";
        using var handler = new FakeGitHub(json, msi);
        using var updater = new Updater(folder, "x64", NullLogger.Instance, handler);

        Assert.IsTrue(await updater.CheckAsync());
        Assert.AreEqual(UpdateState.Available, updater.State);
        string? path = await updater.DownloadAsync();
        Assert.AreEqual(path is null ? UpdateState.Available : UpdateState.Ready, updater.State);
        return path;
    }

    // The release JSON for the API address, the MSI bytes for anything else
    private sealed class FakeGitHub(string json, byte[] msi) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsoluteUri == Updater.LatestReleaseUrl ? new StringContent(json) : new ByteArrayContent(msi),
            });
    }
}
