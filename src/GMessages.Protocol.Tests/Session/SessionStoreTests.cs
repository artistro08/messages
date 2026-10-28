using System.Text;
using GMessages.Protocol.Session;

namespace GMessages.Protocol.Tests.Session;

/// <summary>
/// Tests the DPAPI session file. These behaviors are C#-only (Go has no equivalent), so expectations are hand-written.
/// </summary>
[TestClass]
public class SessionStoreTests
{
    private string _dir = "";

    private string FilePath => Path.Combine(_dir, "session.bin");

    /// <summary>Creates a temp folder for the test.</summary>
    [TestInitialize]
    public void Setup() => _dir = Directory.CreateTempSubdirectory("gmsession").FullName;

    /// <summary>Removes the temp folder.</summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// Every field survives a save and load.
    /// </summary>
    [TestMethod]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        AuthData a         = AuthDataGoldenTests.BuildGolden();
        SessionStore store = new(FilePath);

        store.Save(a);
        AuthData? b = store.Load();

        Assert.IsNotNull(b);
        CollectionAssert.AreEqual(a.RequestCrypto.AesKey, b.RequestCrypto.AesKey);
        CollectionAssert.AreEqual(a.RequestCrypto.HmacKey, b.RequestCrypto.HmacKey);
        CollectionAssert.AreEqual(a.RefreshKey.D, b.RefreshKey.D);
        CollectionAssert.AreEqual(a.RefreshKey.X, b.RefreshKey.X);
        CollectionAssert.AreEqual(a.RefreshKey.Y, b.RefreshKey.Y);
        Assert.AreEqual(a.Browser, b.Browser);
        Assert.AreEqual(a.Mobile, b.Mobile);
        CollectionAssert.AreEqual(a.TachyonAuthToken, b.TachyonAuthToken);
        Assert.AreEqual(a.TachyonExpiry, b.TachyonExpiry);
        Assert.AreEqual(a.TachyonTtlMicros, b.TachyonTtlMicros);
        CollectionAssert.AreEqual(a.WebEncryptionKey, b.WebEncryptionKey);
        Assert.AreEqual(a.SessionId, b.SessionId);
        Assert.AreEqual(a.DestRegId, b.DestRegId);
        Assert.AreEqual(a.DestPhoneId, b.DestPhoneId);
        Assert.AreEqual(a.PairingId, b.PairingId);
        CollectionAssert.AreEquivalent(a.Cookies.Snapshot().ToList(), b.Cookies.Snapshot().ToList());
    }

    /// <summary>
    /// A QR-paired session (no cookie map) stays "no cookies" after a round trip.
    /// </summary>
    [TestMethod]
    public void SaveThenLoad_NoCookieMap_StaysNull()
    {
        SessionStore store = new(FilePath);
        store.Save(AuthData.CreateNew());

        Assert.IsFalse(store.Load()!.Cookies.HasCookies);
    }

    /// <summary>
    /// The file is encrypted, so cookie values are not readable in it.
    /// </summary>
    [TestMethod]
    public void SavedFile_DoesNotContainCookieValueInPlaintext()
    {
        new SessionStore(FilePath).Save(AuthDataGoldenTests.BuildGolden());

        byte[] raw = File.ReadAllBytes(FilePath);

        Assert.IsFalse(Encoding.Latin1.GetString(raw).Contains("fake-sapisid", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(FilePath + ".tmp"));
    }

    /// <summary>A missing file gives null.</summary>
    [TestMethod]
    public void Load_MissingFile_ReturnsNull() => Assert.IsNull(new SessionStore(FilePath).Load());

    /// <summary>A cut-off file gives null instead of throwing.</summary>
    [TestMethod]
    public void Load_TruncatedFile_ReturnsNull()
    {
        SessionStore store = new(FilePath);
        store.Save(AuthDataGoldenTests.BuildGolden());
        byte[] raw = File.ReadAllBytes(FilePath);
        File.WriteAllBytes(FilePath, raw[..(raw.Length / 2)]);

        Assert.IsNull(store.Load());
    }

    /// <summary>Plain JSON that was never DPAPI-protected gives null.</summary>
    [TestMethod]
    public void Load_PlainJsonNotDpapi_ReturnsNull()
    {
        File.WriteAllText(FilePath, "{\"version\":1}");

        Assert.IsNull(new SessionStore(FilePath).Load());
    }

    /// <summary>Saving again replaces the old file.</summary>
    [TestMethod]
    public void Save_Twice_ReplacesFile()
    {
        SessionStore store = new(FilePath);
        store.Save(AuthData.CreateNew());
        AuthData second = AuthDataGoldenTests.BuildGolden();

        store.Save(second);

        Assert.AreEqual(second.SessionId, store.Load()!.SessionId);
    }
}
