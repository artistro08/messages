using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GMessages.Protocol.Auth;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using GMessages.Protocol.Tests.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Protocol.Tests.Auth;

/// <summary>
/// Checks <see cref="TokenRefresher"/> against Go's <c>refreshAuthToken</c>, using <c>refresh.json</c>.
/// </summary>
/// <remarks>
/// The refresh request carries a random request ID, the current time and a random ECDSA signature. Go's signature is
/// verified with the golden public key; then those three fields are copied from Go's request into the C# one before
/// comparing the rest exactly. The C# signature is verified on its own.
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go"/>.
/// </remarks>
[TestClass]
public class TokenRefresherTests
{
    private sealed record Rig(TokenRefresher Refresher, AuthData Auth, FakeHttpHandler Handler, SignalingTimeProvider Time, List<object> Events);

    private static Rig CreateRig(TimeSpan timeLeft, long ttl = 3600000000, ILogger? log = null)
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        var time      = new SignalingTimeProvider();
        var handler   = new FakeHttpHandler { Time = time };
        handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", PbLiteSerializer.Serialize(new RegisterRefreshResponse
        {
            TokenData = new TokenData { TachyonAuthToken = ByteString.CopyFromUtf8("new-tachyon"), TTL = ttl },
        }));
        auth.TachyonExpiry = time.GetUtcNow() + timeLeft;

        var http      = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
        var refresher = new TokenRefresher(http, auth, GmEndpoints.Production, time, log ?? NullLogger.Instance);
        var events    = new List<object>();
        refresher.EventRaised += events.Add;
        return new Rig(refresher, auth, handler, time, events);
    }

    private static bool Verifies(AuthData auth, RegisterRefreshRequest req)
    {
        using ECDsa key = auth.RefreshKey.ToECDsa();
        byte[] signed   = Encoding.UTF8.GetBytes(req.MessageAuth.RequestID + ":" + req.UnixTimestamp.ToString(CultureInfo.InvariantCulture));
        return key.VerifyData(signed, req.Signature.ToByteArray(), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>
    /// At 30 minutes left, the request matches Go's once the random fields are copied, and both signatures verify.
    /// </summary>
    [TestMethod]
    public async Task Refresh_RequestMatchesGo_AndGoSignatureVerifies()
    {
        JsonElement go = Golden.Load("refresh").GetProperty("request");
        Rig rig        = CreateRig(TimeSpan.FromMinutes(30));

        await rig.Refresher.RefreshIfNeededAsync(CancellationToken.None);

        var go_req = PbLiteSerializer.Deserialize<RegisterRefreshRequest>(Encoding.UTF8.GetBytes(go.GetProperty("bodyText").GetString()!));
        var cs_req = PbLiteSerializer.Deserialize<RegisterRefreshRequest>(rig.Handler.Requests[0].Body);
        Assert.IsTrue(Verifies(rig.Auth, go_req), "Go signature");
        Assert.IsTrue(Verifies(rig.Auth, cs_req), "C# signature");
        Assert.AreEqual(rig.Time.GetUtcNow().ToUnixTimeMilliseconds() * 1000, cs_req.UnixTimestamp);

        // Normalize Request ID, Timestamp And Signature
        cs_req.MessageAuth.RequestID = go_req.MessageAuth.RequestID;
        cs_req.UnixTimestamp         = go_req.UnixTimestamp;
        cs_req.Signature             = go_req.Signature;
        Assert.AreEqual(go.GetProperty("bodyText").GetString(), Encoding.UTF8.GetString(PbLiteSerializer.Serialize(cs_req)));
        Assert.AreEqual(go.GetProperty("url").GetString(), rig.Handler.Requests[0].Url);
    }

    /// <summary>
    /// A refresh stores the token, expiry and TTL as one new value (C#-only: one swap, so a concurrent save can't
    /// tear them), and that value's text never shows the token.
    /// </summary>
    [TestMethod]
    public async Task Refresh_SwapsTokenExpiryAndTtlAsOneValue()
    {
        Rig rig              = CreateRig(TimeSpan.FromMinutes(30));
        TachyonToken before  = rig.Auth.Tachyon;

        await rig.Refresher.RefreshIfNeededAsync(CancellationToken.None);

        TachyonToken after = rig.Auth.Tachyon;
        Assert.AreNotSame(before, after);
        CollectionAssert.AreEqual("new-tachyon"u8.ToArray(), after.Token);
        Assert.AreEqual(rig.Time.GetUtcNow() + TimeSpan.FromHours(1), after.Expiry);
        Assert.AreEqual(3600000000, after.TtlMicros);
        Assert.IsFalse(after.ToString().Contains("new-tachyon", StringComparison.Ordinal));
        Assert.IsFalse(after.ToString().Contains("Byte", StringComparison.Ordinal), "no array type name either");
    }

    /// <summary>
    /// At 2 hours left nothing is sent, as in Go.
    /// </summary>
    [TestMethod]
    public async Task Refresh_NotNeededAt2h_NoRequest()
    {
        Rig rig = CreateRig(TimeSpan.FromHours(2));

        await rig.Refresher.RefreshIfNeededAsync(CancellationToken.None);

        Assert.AreEqual(Golden.Load("refresh").GetProperty("requestsAt2h").GetInt32(), rig.Handler.Requests.Count);
        Assert.AreEqual(0, rig.Events.Count);
    }

    /// <summary>
    /// TTL 0 means 24 hours; any other TTL is used as is. Expiry, TTL, token and event match Go.
    /// </summary>
    [TestMethod]
    public async Task Refresh_TtlHandling_MatchesGo()
    {
        foreach (JsonProperty c in Golden.Load("refresh").GetProperty("ttl").EnumerateObject())
        {
            Rig rig = CreateRig(TimeSpan.FromMinutes(30), long.Parse(c.Name, CultureInfo.InvariantCulture));
            DateTimeOffset before = rig.Time.GetUtcNow();

            await rig.Refresher.RefreshIfNeededAsync(CancellationToken.None);

            Assert.AreEqual(c.Value.GetProperty("expiryOffsetSeconds").GetDouble(), (rig.Auth.TachyonExpiry - before).TotalSeconds, c.Name);
            Assert.AreEqual(c.Value.GetProperty("tachyonTTL").GetInt64(), rig.Auth.TachyonTtlMicros, c.Name);
            Assert.AreEqual(c.Value.GetProperty("tokenHex").GetString(), Convert.ToHexStringLower(rig.Auth.TachyonAuthToken!), c.Name);
            CollectionAssert.AreEqual(c.Value.GetProperty("events").EnumerateArray().Select(e => e.GetString()).ToArray(), rig.Events.Select(e => e.GetType().Name).ToArray(), c.Name);
            Assert.IsInstanceOfType<AuthTokenRefreshed>(rig.Events[0]);
        }
    }

    /// <summary>
    /// A refresh logs the new expiry and TTL, never the token or its base64.
    /// </summary>
    [TestMethod]
    public async Task Refresh_LogsExpiryAndTtl_NeverToken()
    {
        var log = new CollectingLogger();
        Rig rig = CreateRig(TimeSpan.FromMinutes(30), log: log);
        rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", PbLiteSerializer.Serialize(new RegisterRefreshResponse
        {
            TokenData = new TokenData { TachyonAuthToken = ByteString.CopyFromUtf8("tok-123"), TTL = 3600000000 },
        }));

        await rig.Refresher.RefreshIfNeededAsync(CancellationToken.None);

        string expected = $"Tachyon token valid until {rig.Auth.TachyonExpiry:o} (TTL 01:00:00)";
        CollectionAssert.Contains(log.Messages, expected);
        Assert.IsFalse(log.Messages.Any(m => m.Contains("tok-123", StringComparison.Ordinal) || m.Contains(Convert.ToBase64String("tok-123"u8), StringComparison.Ordinal)));
    }

    /// <summary>
    /// A refused refresh logs the error type and the HTTP status.
    /// </summary>
    [TestMethod]
    public async Task Refresh_Failure_LogsStatus()
    {
        var log = new CollectingLogger();
        Rig rig = CreateRig(TimeSpan.FromMinutes(30), log: log);
        rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.BadRequest);

        await Assert.ThrowsAsync<GmRequestException>(() => rig.Refresher.RefreshIfNeededAsync(CancellationToken.None));

        CollectionAssert.Contains(log.Messages, "Token refresh failed: GmRequestException HTTP 400");
    }

    /// <summary>
    /// The same failures Go sees are fatal (or not) here too.
    /// </summary>
    [TestMethod]
    public async Task IsFatal_MatchesGo()
    {
        foreach (JsonElement c in Golden.Load("refresh").GetProperty("isFatal").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            int status  = c.GetProperty("status").GetInt32();
            Rig rig     = CreateRig(TimeSpan.FromMinutes(30));
            rig.Handler.Respond = (_, _) => status == 0
                ? throw new HttpRequestException("golden network error")
                : FakeHttpHandler.Response((HttpStatusCode)status, c.GetProperty("contentType").GetString() is { Length: > 0 } ct ? ct : null, Golden.Hex(c.GetProperty("bodyHex")));

            // 5xx Is Retried With Waits On The Fake Clock; Advance Past Each One
            Task refresh = rig.Refresher.RefreshIfNeededAsync(CancellationToken.None);
            for (int i = 0; !refresh.IsCompleted && i < 200; i++)
            {
                if (await rig.Time.TimerCreated.WaitAsync(TimeSpan.FromMilliseconds(50)))
                {
                    rig.Time.Advance(TimeSpan.FromSeconds(5));
                }
            }

            Exception error = await Assert.ThrowsAsync<Exception>(() => refresh);
            Assert.AreEqual(c.GetProperty("fatal").GetBoolean(), TokenRefresher.IsFatal(error), name);
        }
    }
}
