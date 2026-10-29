using System.Security.Cryptography;
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
    /// A session with no Tachyon token stays token-less after a round trip (null, not an empty array).
    /// </summary>
    [TestMethod]
    public void SaveThenLoad_NoToken_StaysNull()
    {
        SessionStore store = new(FilePath);
        AuthData a         = AuthDataGoldenTests.BuildGolden();
        a.Tachyon          = TachyonToken.None;

        store.Save(a);

        Assert.IsNull(store.Load()!.TachyonAuthToken);
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

    private void WriteProtected(string text) =>
        File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));

    /// <summary>Valid DPAPI data holding bad JSON gives null.</summary>
    [TestMethod]
    public void Load_DpapiWithBadJson_ReturnsNull()
    {
        WriteProtected("{not json");

        Assert.IsNull(new SessionStore(FilePath).Load());
    }

    /// <summary>Valid JSON without the key fields gives null.</summary>
    [TestMethod]
    public void Load_DpapiJsonMissingKeys_ReturnsNull()
    {
        WriteProtected("{\"version\":1}");

        Assert.IsNull(new SessionStore(FilePath).Load());
    }

    /// <summary>Keys that are not 32 bytes give null.</summary>
    [TestMethod]
    public void Load_WrongKeyLength_ReturnsNull()
    {
        string b = Convert.ToBase64String(new byte[16]);
        WriteProtected($"{{\"aesKey\":\"{b}\",\"hmacKey\":\"{b}\",\"refreshD\":\"{b}\",\"refreshX\":\"{b}\",\"refreshY\":\"{b}\"}}");

        Assert.IsNull(new SessionStore(FilePath).Load());
    }

    /// <summary>A zero-byte file gives null.</summary>
    [TestMethod]
    public void Load_ZeroByteFile_ReturnsNull()
    {
        File.WriteAllBytes(FilePath, []);

        Assert.IsNull(new SessionStore(FilePath).Load());
    }

    /// <summary>An empty cookie map stays empty (not "no cookies").</summary>
    [TestMethod]
    public void SaveThenLoad_EmptyCookieMap_StaysEmpty()
    {
        AuthData a = AuthData.CreateNew();
        a.Cookies.Set(new Dictionary<string, string>());
        SessionStore store = new(FilePath);

        store.Save(a);
        AuthData? b = store.Load();

        Assert.IsTrue(b!.Cookies.HasCookies);
        Assert.AreEqual(0, b.Cookies.Snapshot().Count);
    }

    /// <summary>A saved <c>__Secure-1PSIDTS</c> (captured by older versions) is dropped on load; other cookies stay.</summary>
    [TestMethod]
    public void Load_DropsStaleSecure1PsidtsCookie()
    {
        AuthData a = AuthData.CreateNew();
        a.Cookies.Set(new Dictionary<string, string> { ["SAPISID"] = "s", ["__Secure-1PSIDTS"] = "stale" });
        SessionStore store = new(FilePath);

        store.Save(a);
        IReadOnlyDictionary<string, string> cookies = store.Load()!.Cookies.Snapshot();

        Assert.IsFalse(cookies.ContainsKey("__Secure-1PSIDTS"));
        Assert.AreEqual("s", cookies["SAPISID"]);
    }

    /// <summary>The largest DestPhoneId survives a round trip.</summary>
    [TestMethod]
    public void SaveThenLoad_MaxDestPhoneId_Survives()
    {
        AuthData a = AuthData.CreateNew();
        a.DestPhoneId = ulong.MaxValue;
        SessionStore store = new(FilePath);

        store.Save(a);

        Assert.AreEqual(ulong.MaxValue, store.Load()!.DestPhoneId);
    }

    /// <summary>A leftover .tmp from a crash does not break Save.</summary>
    [TestMethod]
    public void Save_LeftoverTmp_StillSaves()
    {
        File.WriteAllText(FilePath + ".tmp", "junk");
        SessionStore store = new(FilePath);

        store.Save(AuthDataGoldenTests.BuildGolden());

        Assert.IsNotNull(store.Load());
        Assert.IsFalse(File.Exists(FilePath + ".tmp"));
    }
}
