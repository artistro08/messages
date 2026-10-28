using System.Security.Cryptography;
using System.Text.Json;
using GMessages.Protocol.Crypto;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Session;

namespace GMessages.Protocol.Tests.Session;

/// <summary>
/// Checks <see cref="AuthData"/> against the Go LibGM's <c>AuthData</c> using <c>authdata.json</c>.
/// </summary>
/// <remarks>
/// Go's <c>CopyForRepair</c> creates new random keys, so only the deterministic parts are compared.
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go"/>.
/// </remarks>
[TestClass]
public class AuthDataGoldenTests
{
    private static readonly Guid DestRegId = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");

    private static byte[] Seq(int start, int count) => Enumerable.Range(start, count).Select(i => (byte)i).ToArray();

    /// <summary>
    /// Builds the same fixed AuthData the Go exporter uses, plus the extra fields it sets before clearing.
    /// </summary>
    internal static AuthData BuildGolden()
    {
        // Fixed Refresh Key (Scalar 0x01..0x20)
        using ECDsa ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = Seq(1, 32) });
        ECParameters p    = ecdsa.ExportParameters(true);

        AuthData ad = new()
        {
            RequestCrypto    = new AesCtrHelper(Seq(0x00, 32), Seq(0x20, 32)),
            RefreshKey       = RefreshKey.FromJwk(p.D!, p.Q.X!, p.Q.Y!),
            Browser          = new Device { UserID = 1, SourceID = "golden-src", Network = "GDitto" },
            Mobile           = new Device { UserID = 1, SourceID = "golden-src", Network = "GDitto" },
            TachyonAuthToken = "fake-tachyon"u8.ToArray(),
            TachyonTtlMicros = 86400000000,
            TachyonExpiry    = DateTimeOffset.FromUnixTimeSeconds(1700000000),
            WebEncryptionKey = [1, 2, 3],
            SessionId        = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            DestRegId        = DestRegId,
            DestPhoneId      = 42,
            PairingId        = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10"),
        };
        ad.Cookies.Set(new Dictionary<string, string> { ["SAPISID"] = "fake-sapisid", ["SID"] = "fake-sid" });
        return ad;
    }

    private static string Hex(Guid g) => Convert.ToHexStringLower(g.ToByteArray(bigEndian: true));

    private static void AssertCommon(JsonElement go, AuthData ad)
    {
        Assert.AreEqual(go.GetProperty("hasBrowser").GetBoolean(), ad.Browser is not null);
        Assert.AreEqual(go.GetProperty("hasMobile").GetBoolean(), ad.Mobile is not null);
        Assert.AreEqual(go.GetProperty("tachyonTokenHex").GetString(), Convert.ToHexStringLower(ad.TachyonAuthToken ?? []));
        Assert.AreEqual(go.GetProperty("tachyonExpiryZero").GetBoolean(), ad.TachyonExpiry == default);
        Assert.AreEqual(go.GetProperty("tachyonTTL").GetInt64(), ad.TachyonTtlMicros);
        Assert.AreEqual(go.GetProperty("pairingIdHex").GetString(), Hex(ad.PairingId));
        Assert.AreEqual(go.GetProperty("sessionIdHex").GetString(), Hex(ad.SessionId));
        Assert.AreEqual(go.GetProperty("destRegIdHex").GetString(), Hex(ad.DestRegId));
        Assert.AreEqual(go.GetProperty("destPhoneId").GetUInt64(), ad.DestPhoneId);
        Assert.AreEqual(go.GetProperty("cookiesNil").GetBoolean(), !ad.Cookies.HasCookies);
        Assert.IsNotNull(ad.RefreshKey);
        Assert.IsNotNull(ad.RequestCrypto);

        Dictionary<string, string> cookies = go.GetProperty("cookies").EnumerateObject().ToDictionary(o => o.Name, o => o.Value.GetString()!);
        CollectionAssert.AreEquivalent(cookies, ad.Cookies.Snapshot().ToDictionary(k => k.Key, k => k.Value));
    }

    /// <summary>
    /// ClearPairing resets pairing state and keeps identity, keys and cookies, like Go.
    /// </summary>
    [TestMethod]
    public void ClearPairing_MatchesGo()
    {
        JsonElement go = Golden.Load("authdata").GetProperty("cleared");
        AuthData ad    = BuildGolden();

        ad.ClearPairing();

        AssertCommon(go, ad);
        Assert.AreEqual(go.GetProperty("requestCryptoAesHex").GetString(), Convert.ToHexStringLower(ad.RequestCrypto.AesKey));
        Assert.IsNull(ad.WebEncryptionKey);
    }

    /// <summary>
    /// CopyForRepair keeps cookies and DestPhoneId only; keys are fresh and random in both Go and C#.
    /// </summary>
    [TestMethod]
    public void CopyForRepair_MatchesGo()
    {
        JsonElement go = Golden.Load("authdata").GetProperty("repaired");
        AuthData src   = BuildGolden();

        AuthData ad = src.CopyForRepair();

        AssertCommon(go, ad);
        CollectionAssert.AreNotEqual(src.RequestCrypto.AesKey, ad.RequestCrypto.AesKey);
        CollectionAssert.AreNotEqual(src.RefreshKey.D, ad.RefreshKey.D);
        Assert.AreNotSame(src.Cookies, ad.Cookies);
    }

    /// <summary>
    /// IsGoogleAccount, AuthNetwork and HasCookies match Go with and without DestRegID.
    /// </summary>
    [TestMethod]
    public void AuthNetwork_MatchesGo()
    {
        JsonElement go = Golden.Load("authdata").GetProperty("network");

        void Check(string name, AuthData ad)
        {
            JsonElement e = go.GetProperty(name);
            Assert.AreEqual(e.GetProperty("isGoogleAccount").GetBoolean(), ad.IsGoogleAccount, name);
            Assert.AreEqual(e.GetProperty("authNetwork").GetString(), ad.AuthNetwork, name);
            Assert.AreEqual(e.GetProperty("hasCookies").GetBoolean(), ad.HasCookies, name);
        }

        Check("withoutDestRegId", AuthData.CreateNew());

        AuthData with_reg = AuthData.CreateNew();
        with_reg.DestRegId = DestRegId;
        Check("withDestRegIdNoCookies", with_reg);

        AuthData with_empty = AuthData.CreateNew();
        with_empty.DestRegId = DestRegId;
        with_empty.Cookies.Set(new Dictionary<string, string>());
        Check("withDestRegIdEmptyCookies", with_empty);
    }
}
