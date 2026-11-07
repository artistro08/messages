using System.Globalization;
using System.Net;
using System.Text.Json;
using GMessages.Protocol.Http;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GMessages.Protocol.Tests.Http;

/// <summary>
/// Checks the HTTP layer against the requests that the Go LibGM sent. The golden exporter captured them.
/// </summary>
/// <remarks>
/// The tests compare header names without case, and only for the headers that libgm sets. The capture does not see
/// the headers that the Go transport adds, such as Content-Length. The Go cookie order comes from a random map
/// iteration, so the tests compare the Cookie header as a set. The SAPISIDHASH timestamp comes from the time. Thus
/// the tests set the fake clock to the timestamp in the Go value. The retry wait check of 1 s, then 2 s, is C#-only. A
/// fake clock drives it.
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/http.go"/>.
/// </remarks>
[TestClass]
public class GmHttpClientTests
{
    private static readonly Dictionary<string, string> GoCookies = new() { ["SAPISID"] = "fake-sapisid", ["SID"] = "fake-sid" };

    // This response records whether something disposed it.
    private sealed class TrackingResponse(HttpStatusCode status) : HttpResponseMessage(status)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed record Rig(GmHttpClient Client, FakeHttpHandler Handler, FakeHttpHandler LongPollHandler, CookieJar Jar, SignalingTimeProvider Time);

    private static Rig CreateRig(Dictionary<string, string>? cookies = null, ILogger? log = null)
    {
        var time = new SignalingTimeProvider();
        var handler = new FakeHttpHandler { Time = time };
        var longPollHandler = new FakeHttpHandler { Time = time };
        var jar = new CookieJar();
        if (cookies is not null)
        {
            jar.Set(cookies);
        }

        var client = new GmHttpClient(new HttpClient(handler), new HttpClient(longPollHandler), jar, time, log ?? NullLogger.Instance);
        return new Rig(client, handler, longPollHandler, jar, time);
    }

    /// <summary>
    /// A response that sets cookies writes their names and expiry times to the log, but never the values.
    /// </summary>
    [TestMethod]
    public async Task SetCookie_LogsNamesAndExpiry_NeverValues()
    {
        var log = new CollectingLogger();
        Rig rig = CreateRig(new Dictionary<string, string>(GoCookies), log);
        rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(
            HttpStatusCode.OK,
            "application/json+protobuf",
            "[]"u8.ToArray(),
            "SID=secret-sid; Expires=Fri, 30 Oct 2026 15:07:55 GMT; Path=/",
            "SIDCC=secret-sidcc; Path=/");

        using HttpResponseMessage _ = await rig.Client.PostAsync(new Uri("https://messages.google.com/x/Ping"), RelayBody, GmContentType.PbLite, false, CancellationToken.None);

        CollectionAssert.Contains(log.Messages, "Cookies set by /x/Ping: SID (expires 2026-10-30T15:07:55Z), SIDCC (session)");
        Assert.IsFalse(log.Messages.Any(m => m.Contains("secret", StringComparison.Ordinal)));
    }

    /// <summary>
    /// 2 identical Set-Cookie responses write the cookie line 1 time. A changed expiry writes it again.
    /// </summary>
    [TestMethod]
    public async Task SetCookie_SameResponseTwice_LogsOnce()
    {
        var log = new CollectingLogger();
        Rig rig = CreateRig(new Dictionary<string, string>(GoCookies), log);
        string cookie = "SID=v; Expires=Fri, 30 Oct 2026 15:07:55 GMT; Path=/";
        rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", "[]"u8.ToArray(), cookie);

        using HttpResponseMessage a = await rig.Client.PostAsync(new Uri("https://messages.google.com/x/Ping"), RelayBody, GmContentType.PbLite, false, CancellationToken.None);
        using HttpResponseMessage b = await rig.Client.PostAsync(new Uri("https://messages.google.com/x/Ping"), RelayBody, GmContentType.PbLite, false, CancellationToken.None);
        Assert.AreEqual(1, log.Messages.Count(m => m.StartsWith("Cookies set by", StringComparison.Ordinal)));

        cookie = "SID=v; Expires=Sat, 31 Oct 2026 15:07:55 GMT; Path=/";
        using HttpResponseMessage c = await rig.Client.PostAsync(new Uri("https://messages.google.com/x/Ping"), RelayBody, GmContentType.PbLite, false, CancellationToken.None);
        Assert.AreEqual(2, log.Messages.Count(m => m.StartsWith("Cookies set by", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A Max-Age or Expires out of range never makes the request throw. The log shows the cookie with an unknown expiry.
    /// </summary>
    [TestMethod]
    public async Task SetCookie_AbsurdExpiry_DoesNotFailTheRequest()
    {
        var log = new CollectingLogger();
        Rig rig = CreateRig(new Dictionary<string, string>(GoCookies), log);
        rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(
            HttpStatusCode.OK,
            "application/json+protobuf",
            "[]"u8.ToArray(),
            "A=v; Max-Age=9223372036854775807; Path=/");

        using HttpResponseMessage res = await rig.Client.PostAsync(new Uri("https://messages.google.com/x/Ping"), RelayBody, GmContentType.PbLite, false, CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.IsTrue(log.Messages.Any(m => m == "Cookies set by /x/Ping: A (expires ?)"), string.Join("\n", log.Messages));
    }

    /// <summary>
    /// A typed API answer that declares a body over the 16 MiB limit throws before the client reads the body.
    /// </summary>
    [TestMethod]
    public async Task ReadTyped_OverCap_Throws()
    {
        Rig rig = CreateRig(new Dictionary<string, string>(GoCookies));
        rig.Handler.Respond = (_, _) =>
        {
            HttpResponseMessage res          = FakeHttpHandler.Response(HttpStatusCode.OK, "application/x-protobuf", new byte[16]);
            res.Content.Headers.ContentLength = GmHttpClient.MaxApiBodyBytes + 1;
            return res;
        };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => rig.Client.GetTypedAsync<Config>(GmEndpoints.Production.Config, CancellationToken.None));
    }

    private static long GoTimestamp(string authorization) =>
        long.Parse(authorization["SAPISIDHASH ".Length..authorization.IndexOf('_', StringComparison.Ordinal)], CultureInfo.InvariantCulture);

    private static AuthMessage RelayBody => new() { RequestID = "r1" };

    private static void AssertHeadersMatchGo(JsonElement goRequest, RecordedRequest actual)
    {
        Assert.AreEqual(goRequest.GetProperty("method").GetString(), actual.Method.Method);
        Assert.AreEqual(goRequest.GetProperty("url").GetString(), actual.Url);

        var goHeaders = goRequest.GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
        CollectionAssert.AreEquivalent(goHeaders.Keys.Select(k => k.ToLowerInvariant()).ToList(), actual.Headers.Keys.Select(k => k.ToLowerInvariant()).ToList(), "header names");
        foreach (var (name, value) in goHeaders)
        {
            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                CollectionAssert.AreEquivalent(value.Split("; "), actual.Headers[name].Split("; "), "cookies");
            }
            else
            {
                Assert.AreEqual(value, actual.Headers[name], name);
            }
        }

        CollectionAssert.AreEqual(Golden.Hex(goRequest.GetProperty("bodyHex")), actual.Body);
    }

    /// <summary>
    /// Each constant is equal to the Go value.
    /// </summary>
    [TestMethod]
    public void Constants_MatchGo()
    {
        var go = Golden.Load("http").GetProperty("constants");
        Assert.AreEqual(go.GetProperty("googleApiKey").GetString(), GmConstants.GoogleApiKey);
        Assert.AreEqual(go.GetProperty("userAgent").GetString(), GmConstants.UserAgent);
        Assert.AreEqual(go.GetProperty("secUA").GetString(), GmConstants.SecUA);
        Assert.AreEqual(go.GetProperty("uaPlatform").GetString(), GmConstants.UAPlatform);
        Assert.AreEqual(go.GetProperty("xUserAgent").GetString(), GmConstants.XUserAgent);
        Assert.AreEqual(go.GetProperty("secUAMobile").GetString(), GmConstants.SecUAMobile);
        Assert.AreEqual(go.GetProperty("googleNetwork").GetString(), GmConstants.GoogleNetwork);
        Assert.AreEqual(go.GetProperty("configMessagePblite").GetString(), System.Text.Encoding.UTF8.GetString(PbLiteSerializer.Serialize(GmConstants.ConfigVersion)));
        Assert.AreEqual(go.GetProperty("browserDetailsPblite").GetString(), System.Text.Encoding.UTF8.GetString(PbLiteSerializer.Serialize(GmConstants.BrowserDetails)));
    }

    /// <summary>
    /// The production endpoints give each URL in the Go paths.go.
    /// </summary>
    [TestMethod]
    public void Endpoints_Production_MatchGoPaths()
    {
        var go = Golden.Load("http").GetProperty("paths");
        GmEndpoints e = GmEndpoints.Production;
        var expected = new Dictionary<string, Uri>
        {
            ["messagesBase"]          = e.MessagesBase,
            ["googleAuthentication"]  = e.Authentication,
            ["uploadMedia"]           = e.UploadMedia,
            ["receiveMessages"]       = e.ReceiveMessages,
            ["sendMessage"]           = e.SendMessage,
            ["ackMessages"]           = e.AckMessages,
            ["receiveMessagesGoogle"] = e.ReceiveMessagesGoogle,
            ["sendMessageGoogle"]     = e.SendMessageGoogle,
            ["ackMessagesGoogle"]     = e.AckMessagesGoogle,
            ["signInGaia"]            = e.SignInGaia,
            ["registerRefresh"]       = e.RegisterRefresh,
            ["config"]                = e.Config,
        };

        // The client does not use these 5 Go paths. The QR relay pairing is not ported.
        string[] notPorted = ["googleTimesource", "registerPhoneRelay", "refreshPhoneRelay", "getWebEncryptionKey", "revokeRelayPairing"];
        Assert.AreEqual(go.EnumerateObject().Count(p => !notPorted.Contains(p.Name)), expected.Count);
        foreach (var (key, uri) in expected)
        {
            // The origin has no path, so the Go string has no trailing slash.
            Assert.AreEqual(go.GetProperty(key).GetString(), key == "messagesBase" ? uri.GetLeftPart(UriPartial.Authority) : uri.AbsoluteUri, key);
        }
    }

    /// <summary>
    /// The relay POST has exactly the Go headers, cookies, Authorization and body.
    /// </summary>
    [TestMethod]
    public async Task RelayRequest_HeadersCookiesAuthorizationAndBody_MatchGo()
    {
        var go = Golden.Load("http").GetProperty("relayRequest");
        Rig rig = CreateRig(GoCookies);
        rig.Time.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(GoTimestamp(go.GetProperty("headers").GetProperty("Authorization").GetString()!)));

        using var res = await rig.Client.PostAsync(GmEndpoints.Production.SignInGaia, RelayBody, GmContentType.PbLite, false, CancellationToken.None);

        Assert.AreEqual(1, rig.Handler.Requests.Count);
        AssertHeadersMatchGo(go, rig.Handler.Requests[0]);
    }

    /// <summary>
    /// The config GET has the Go headers: same-origin, no x-user-agent and no origin. It decodes the PBLite response.
    /// </summary>
    [TestMethod]
    public async Task ConfigRequest_HeadersMatchGo()
    {
        var go = Golden.Load("http").GetProperty("configRequest");
        Rig rig = CreateRig(GoCookies);
        rig.Time.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(GoTimestamp(go.GetProperty("headers").GetProperty("Authorization").GetString()!)));
        var config = new Config { ClientVersion = "20260902_00_RC00", CountryCode = "US" };
        rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", PbLiteSerializer.Serialize(config));

        Config result = await rig.Client.GetTypedAsync<Config>(GmEndpoints.Production.Config, CancellationToken.None);

        AssertHeadersMatchGo(go, rig.Handler.Requests[0]);
        Assert.AreEqual(config, result);
    }

    /// <summary>
    /// The C# code gives each Go SAPISIDHASH again from its own timestamp.
    /// </summary>
    [TestMethod]
    public void SapisidHash_ReproducesEveryGoOutput()
    {
        var outputs = Golden.Load("http").GetProperty("sapisid").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.AreEqual(3, outputs.Count);
        foreach (string go in outputs)
        {
            Assert.AreEqual(go, CookieJar.SapisidHash("https://messages.google.com", "fake-sapisid", GoTimestamp(go)));
        }
    }

    /// <summary>
    /// Set-Cookie values replace cookies by name, as in the Go AuthData. A jar without cookies stays empty and sends none.
    /// </summary>
    [TestMethod]
    public async Task SetCookie_UpdatesJarLikeGo()
    {
        var go = Golden.Load("http").GetProperty("setCookie");

        Rig rig = CreateRig(GoCookies);
        int changed = 0;
        rig.Jar.Changed += (_, _) => changed++;
        rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, null, null, "SIDCC=new; Path=/", "SID=rotated");
        using (await rig.Client.PostAsync(GmEndpoints.Production.SignInGaia, RelayBody, GmContentType.PbLite, false, CancellationToken.None))
        {
        }

        var expected = go.GetProperty("cookiesAfter").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        CollectionAssert.AreEquivalent(expected, rig.Jar.Snapshot().ToDictionary());
        Assert.AreEqual(1, changed);

        Rig empty = CreateRig();
        empty.Handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, null, null, "SIDCC=new; Path=/");
        using (await empty.Client.PostAsync(GmEndpoints.Production.SignInGaia, RelayBody, GmContentType.PbLite, false, CancellationToken.None))
        {
        }

        Assert.IsTrue(go.GetProperty("nilCookiesStayNil").GetBoolean());
        Assert.IsFalse(empty.Jar.HasCookies);
        Assert.AreEqual(0, empty.Jar.Snapshot().Count);
        CollectionAssert.AreEquivalent(
            go.GetProperty("nilCookiesRequestHeaders").EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList(),
            empty.Handler.Requests[0].Headers.Keys.Select(k => k.ToLowerInvariant()).ToList());
    }

    /// <summary>
    /// The attempt counts and final statuses are the same as in Go for each scripted case. The waits are exactly 1 s, then 2 s.
    /// </summary>
    /// <remarks>
    /// The wait checks and the dispose checks of retried responses are C#-only. The test does not sleep. It waits for
    /// the client to create its retry timer, moves the fake clock by exactly the expected wait, and repeats. The real
    /// 10 s timeout only stops a hang.
    /// </remarks>
    [TestMethod]
    public async Task Retry_AttemptCountsMatchGo_DelaysAre1sThen2s()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int checkedCases = 0;
        foreach (var go in Golden.Load("http").GetProperty("retry").EnumerateArray().Where(c => !c.GetProperty("longPoll").GetBoolean()))
        {
            int[] statuses = [.. go.GetProperty("statuses").EnumerateArray().Select(s => s.GetInt32())];
            int expectedAttempts = go.GetProperty("attempts").GetInt32();
            Rig rig = CreateRig(GoCookies);
            var responses = new List<TrackingResponse>();
            rig.Handler.Respond = (i, _) =>
            {
                var response = new TrackingResponse((HttpStatusCode)statuses[Math.Min(i, statuses.Length - 1)]);
                responses.Add(response);
                return response;
            };

            Task<HttpResponseMessage> task = rig.Client.PostAsync(GmEndpoints.Production.SignInGaia, RelayBody, GmContentType.PbLite, false, CancellationToken.None);

            // Each retry has 1 wait. The timer appears, then the test moves the clock by exactly attempt x 1 s.
            for (int attempt = 1; attempt < expectedAttempts; attempt++)
            {
                await rig.Time.TimerCreated.WaitAsync(guard.Token);
                Assert.AreEqual(attempt, rig.Handler.Requests.Count, "no early retry");
                rig.Time.Advance(TimeSpan.FromSeconds(attempt));
            }

            using HttpResponseMessage res = await task.WaitAsync(guard.Token);

            Assert.AreEqual(expectedAttempts, rig.Handler.Requests.Count);
            Assert.AreEqual(go.GetProperty("finalStatus").GetInt32(), (int)res.StatusCode);

            for (int i = 1; i < rig.Handler.Requests.Count; i++)
            {
                Assert.AreEqual(TimeSpan.FromSeconds(i), rig.Handler.Requests[i].At - rig.Handler.Requests[i - 1].At, $"gap {i}");
            }

            // The client disposes each retried response, but not the returned response.
            Assert.AreEqual(expectedAttempts, responses.Count);
            Assert.IsTrue(responses.Take(expectedAttempts - 1).All(r => r.IsDisposed), "retried responses disposed");
            Assert.IsFalse(responses[^1].IsDisposed);

            checkedCases++;
        }

        Assert.AreEqual(3, checkedCases);
    }

    /// <summary>
    /// A long poll uses the long-poll client, and the client never retries it. The Go case has 1 attempt on 503.
    /// </summary>
    [TestMethod]
    public async Task LongPoll_IsNotRetried()
    {
        var go = Golden.Load("http").GetProperty("retry").EnumerateArray().Single(c => c.GetProperty("longPoll").GetBoolean());
        Rig rig = CreateRig(GoCookies);
        rig.LongPollHandler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.ServiceUnavailable);

        using var res = await rig.Client.PostAsync(GmEndpoints.Production.ReceiveMessagesGoogle, RelayBody, GmContentType.PbLite, true, CancellationToken.None);

        Assert.AreEqual(go.GetProperty("attempts").GetInt32(), rig.LongPollHandler.Requests.Count);
        Assert.AreEqual(0, rig.Handler.Requests.Count);
        Assert.AreEqual(go.GetProperty("finalStatus").GetInt32(), (int)res.StatusCode);
    }

    /// <summary>
    /// An error body decodes into the <see cref="GmRequestException"/> exactly when Go decodes it.
    /// </summary>
    [TestMethod]
    public async Task ErrorBodies_DecodeLikeGo_IntoGmRequestException()
    {
        int count = 0;
        foreach (var go in Golden.Load("http").GetProperty("errorBody").EnumerateArray())
        {
            Rig rig = CreateRig(GoCookies);
            rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(
                (HttpStatusCode)go.GetProperty("status").GetInt32(), go.GetProperty("contentType").GetString(), Golden.Hex(go.GetProperty("bodyHex")));

            var ex = await Assert.ThrowsExactlyAsync<GmRequestException>(
                () => rig.Client.GetTypedAsync<Config>(GmEndpoints.Production.Config, CancellationToken.None));

            string name = go.GetProperty("name").GetString()!;
            Assert.AreEqual(go.GetProperty("status").GetInt32(), ex.StatusCode, name);
            CollectionAssert.AreEqual(Golden.Hex(go.GetProperty("bodyHex")), ex.Body, name);
            if (go.GetProperty("decoded").GetBoolean())
            {
                Assert.IsNotNull(ex.Error, name);
                Assert.AreEqual(go.GetProperty("type").GetInt64(), ex.Error.Type, name);
                Assert.AreEqual(go.GetProperty("message").GetString(), ex.Error.Message, name);
                CollectionAssert.AreEqual(go.GetProperty("classes").EnumerateArray().Select(c => c.GetString()).ToList(), ex.Error.Class.Select(c => c.Class).ToList(), name);
            }
            else
            {
                Assert.IsNull(ex.Error, name);
            }

            count++;
        }

        Assert.AreEqual(6, count);
    }

    /// <summary>
    /// This test is C#-only. A protobuf POST sends binary, and the typed response decodes by content type.
    /// </summary>
    [TestMethod]
    public async Task PostTyped_Protobuf_RoundTrips()
    {
        Rig rig = CreateRig(GoCookies);
        var reply = new Config { ClientVersion = "v", CountryCode = "US" };
        rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/x-protobuf; charset=x", reply.ToByteArray());

        Config result = await rig.Client.PostTypedAsync<Config>(GmEndpoints.Production.SignInGaia, RelayBody, GmContentType.Protobuf, CancellationToken.None);

        Assert.AreEqual(reply, result);
        Assert.AreEqual("application/x-protobuf", rig.Handler.Requests[0].Headers["content-type"]);
        CollectionAssert.AreEqual(RelayBody.ToByteArray(), rig.Handler.Requests[0].Body);
    }
}
