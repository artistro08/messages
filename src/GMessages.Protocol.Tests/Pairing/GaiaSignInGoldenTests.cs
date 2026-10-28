using System.Net;
using System.Text;
using System.Text.Json;
using GMessages.Protocol.Http;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using GMessages.Protocol.Tests.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Protocol.Tests.Pairing;

/// <summary>
/// Checks <see cref="GaiaSignIn"/> against Go's <c>signInGaiaGetToken</c>, <c>getGaiaPrimaryDevices</c> and
/// <c>FetchConfig</c>, using <c>signin.json</c>.
/// </summary>
/// <remarks>
/// The sign-in request carries a random request ID. It is copied from Go's request into the C# one before comparing
/// the rest exactly. The token expiry depends on the clock, so it is compared as an offset from the fake clock.
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go"/>.
/// </remarks>
[TestClass]
public class GaiaSignInGoldenTests
{
    private sealed record Rig(GaiaSignIn SignIn, AuthData Auth, FakeHttpHandler Handler, SignalingTimeProvider Time);

    private static Rig CreateRig(byte[] body)
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        var time      = new SignalingTimeProvider();
        var handler   = new FakeHttpHandler { Time = time };
        handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", body);

        var http = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
        return new Rig(new GaiaSignIn(http, auth, GmEndpoints.Production, time, NullLogger.Instance), auth, handler, time);
    }

    private static Rig CreateSignInRig() =>
        CreateRig(Encoding.UTF8.GetBytes(Golden.Load("signin").GetProperty("response").GetProperty("bodyText").GetString()!));

    /// <summary>
    /// The device ID is "messages-web-" and the session ID's bytes in RFC 4122 order as lowercase hex.
    /// </summary>
    [TestMethod]
    public void DeviceId_MatchesGo()
    {
        string go = Golden.Load("signin").GetProperty("deviceId").GetString()!;

        Assert.AreEqual(go, GaiaSignIn.DeviceIdFor(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")));
    }

    /// <summary>
    /// The request matches Go's once the random request ID is copied over.
    /// </summary>
    [TestMethod]
    public async Task SignInRequest_MatchesGo()
    {
        JsonElement go = Golden.Load("signin").GetProperty("request");
        Rig rig        = CreateSignInRig();

        await rig.SignIn.GetPrimaryDevicesAsync(CancellationToken.None);

        var go_req = PbLiteSerializer.Deserialize<SignInGaiaRequest>(Encoding.UTF8.GetBytes(go.GetProperty("bodyText").GetString()!));
        var cs_req = PbLiteSerializer.Deserialize<SignInGaiaRequest>(rig.Handler.Requests[0].Body);

        // Normalize Request ID
        Assert.IsTrue(Guid.TryParse(cs_req.AuthMessage.RequestID, out _), "request ID is a UUID");
        cs_req.AuthMessage.RequestID = go_req.AuthMessage.RequestID;
        Assert.AreEqual(go.GetProperty("bodyText").GetString(), Encoding.UTF8.GetString(PbLiteSerializer.Serialize(cs_req)));
        Assert.AreEqual(go.GetProperty("url").GetString(), rig.Handler.Requests[0].Url);
    }

    /// <summary>
    /// Primary devices, both device identities, the token and the expiry match Go.
    /// </summary>
    [TestMethod]
    public async Task SignInResponse_DevicesAndAuthDataMatchGo()
    {
        JsonElement go        = Golden.Load("signin").GetProperty("response");
        Rig rig               = CreateSignInRig();
        DateTimeOffset before = rig.Time.GetUtcNow();

        IReadOnlyList<PrimaryDevice> devices = await rig.SignIn.GetPrimaryDevicesAsync(CancellationToken.None);

        JsonElement[] go_devices = [.. go.GetProperty("primaryDevices").EnumerateArray()];
        Assert.AreEqual(go_devices.Length, devices.Count);
        for (int i = 0; i < go_devices.Length; i++)
        {
            Assert.AreEqual(go_devices[i].GetProperty("regId").GetString(), devices[i].RegId);
            Assert.AreEqual(go_devices[i].GetProperty("unknownInt").GetUInt64(), devices[i].UnknownInt);
            Assert.AreEqual(go_devices[i].GetProperty("unknownTs").GetUInt64(), devices[i].UnknownTs);
            Assert.AreEqual(DateTimeOffset.UnixEpoch.AddTicks(go_devices[i].GetProperty("lastSeenUs").GetInt64() * 10), devices[i].LastSeen);
        }

        Assert.AreEqual(go.GetProperty("mobile").GetString(), Encoding.UTF8.GetString(PbLiteSerializer.Serialize(rig.Auth.Mobile!)));
        Assert.AreEqual(go.GetProperty("browser").GetString(), Encoding.UTF8.GetString(PbLiteSerializer.Serialize(rig.Auth.Browser!)));
        Assert.AreEqual(go.GetProperty("tokenHex").GetString(), Convert.ToHexStringLower(rig.Auth.TachyonAuthToken!));
        Assert.AreEqual(go.GetProperty("tachyonTTL").GetInt64(), rig.Auth.TachyonTtlMicros);
        Assert.AreEqual(go.GetProperty("expiryOffsetSeconds").GetDouble(), (rig.Auth.TachyonExpiry - before).TotalSeconds);
    }

    /// <summary>
    /// The config fetch sets the session ID from the device info, and the client version parses like Go's.
    /// </summary>
    [TestMethod]
    public async Task FetchConfig_SessionIdAndVersionMatchGo()
    {
        JsonElement go = Golden.Load("signin").GetProperty("config");
        Rig rig        = CreateRig(Encoding.UTF8.GetBytes(go.GetProperty("bodyText").GetString()!));

        await rig.SignIn.FetchConfigAsync(CancellationToken.None);

        Assert.AreEqual(go.GetProperty("url").GetString(), rig.Handler.Requests[0].Url);
        Assert.AreEqual(HttpMethod.Get, rig.Handler.Requests[0].Method);
        Assert.AreEqual(Guid.Parse(go.GetProperty("sessionId").GetString()!), rig.Auth.SessionId);

        JsonElement v        = go.GetProperty("version");
        ConfigVersion parsed = GaiaSignIn.ParseClientVersion("20260902");
        Assert.AreEqual(v.GetProperty("year").GetInt32(), parsed.Year);
        Assert.AreEqual(v.GetProperty("month").GetInt32(), parsed.Month);
        Assert.AreEqual(v.GetProperty("day").GetInt32(), parsed.Day);
        Assert.AreEqual(v.GetProperty("v1").GetInt32(), parsed.V1);
        Assert.AreEqual(v.GetProperty("v2").GetInt32(), parsed.V2);
    }

    /// <summary>
    /// C#-only behavior (no Go vector): an unparseable client version fails the fetch (Go's intent), and an
    /// unparseable device ID leaves the session ID alone (Go logs it).
    /// </summary>
    [TestMethod]
    public async Task FetchConfig_BadVersionThrows_BadDeviceIdKeepsSession()
    {
        Rig bad_version = CreateRig(PbLiteSerializer.Serialize(new Config { ClientVersion = "abc" }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => bad_version.SignIn.FetchConfigAsync(CancellationToken.None));

        Rig bad_id  = CreateRig(PbLiteSerializer.Serialize(new Config { ClientVersion = "20260902", DeviceInfo = new Config.Types.DeviceInfo { DeviceID = "nope" } }));
        Guid before = bad_id.Auth.SessionId;
        await bad_id.SignIn.FetchConfigAsync(CancellationToken.None);
        Assert.AreEqual(before, bad_id.Auth.SessionId);
    }
}
