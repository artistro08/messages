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
/// Checks the HTTP layer against requests the Go LibGM itself sent (captured by the golden exporter).
/// </summary>
/// <remarks>
/// Header names are compared case-insensitively and only for headers libgm sets (the capture does not see headers Go's
/// transport adds, such as Content-Length). Go's cookie order comes from a random map iteration, so the Cookie header
/// is compared as a set. The SAPISIDHASH timestamp is time-based, so the fake clock is set to the one in Go's value.
/// The "retry delays are 1 s then 2 s" check and the long-poll timeout are C#-only behavior driven by a fake clock.
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/http.go"/>.
/// </remarks>
[TestClass]
public class GmHttpClientTests
{
    private static readonly Dictionary<string, string> GoCookies = new() { ["SAPISID"] = "fake-sapisid", ["SID"] = "fake-sid" };


    // Response that remembers whether it was disposed.
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
        var long_poll_handler = new FakeHttpHandler { Time = time };
        var jar = new CookieJar();
        if (cookies is not null)
        {
            jar.Set(cookies);
        }

        var client = new GmHttpClient(new HttpClient(handler), new HttpClient(long_poll_handler), jar, time, log ?? NullLogger.Instance);
        return new Rig(client, handler, long_poll_handler, jar, time);
    }

    /// <summary>
    /// A response that sets cookies logs their names and expiry times, never the values.
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
    /// Two identical Set-Cookie responses log the cookie line once; a changed expiry logs again.
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
    /// An absurd Max-Age or Expires never throws into the request; the cookie is logged with an unknown expiry.
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

    private static long GoTimestamp(string authorization) =>
        long.Parse(authorization["SAPISIDHASH ".Length..authorization.IndexOf('_', StringComparison.Ordinal)], CultureInfo.InvariantCulture);

    private static AuthMessage RelayBody => new() { RequestID = "r1" };

    private static void AssertHeadersMatchGo(JsonElement go_request, RecordedRequest actual)
    {
        Assert.AreEqual(go_request.GetProperty("method").GetString(), actual.Method.Method);
        Assert.AreEqual(go_request.GetProperty("url").GetString(), actual.Url);

        var go_headers = go_request.GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
        CollectionAssert.AreEquivalent(go_headers.Keys.Select(k => k.ToLowerInvariant()).ToList(), actual.Headers.Keys.Select(k => k.ToLowerInvariant()).ToList(), "header names");
        foreach (var (name, value) in go_headers)
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

        CollectionAssert.AreEqual(Golden.Hex(go_request.GetProperty("bodyHex")), actual.Body);
    }

    /// <summary>
    /// Every constant equals Go's.
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
    /// Every URL in Go's paths.go is reproduced by the production endpoints.
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
            ["googleTimesource"]      = e.Timesource,
            ["uploadMedia"]           = e.UploadMedia,
            ["registerPhoneRelay"]    = e.RegisterPhoneRelay,
            ["refreshPhoneRelay"]     = e.RefreshPhoneRelay,
            ["getWebEncryptionKey"]   = e.GetWebEncryptionKey,
            ["revokeRelayPairing"]    = e.RevokeRelayPairing,
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

        Assert.AreEqual(go.EnumerateObject().Count(), expected.Count);
        foreach (var (key, uri) in expected)
        {
            // The origin has no path, so Go's string has no trailing slash
            Assert.AreEqual(go.GetProperty(key).GetString(), key == "messagesBase" ? uri.GetLeftPart(UriPartial.Authority) : uri.AbsoluteUri, key);
        }
    }

    /// <summary>
    /// The relay POST has Go's exact headers, cookies, Authorization and body.
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
    /// The config GET has Go's headers (same-origin, no x-user-agent or origin) and decodes the PBLite response.
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
    /// Every SAPISIDHASH Go produced is reproduced from its own timestamp.
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
    /// Set-Cookie values overwrite by name like Go's AuthData; a jar without cookies stays that way and sends none.
    /// </summary>
    [TestMethod]
    public async Task SetCookie_UpdatesJarLikeGo()
    {
        var go = Golden.Load("http").GetProperty("setCookie");

        // With Cookies
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

        // Without Cookies
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
    /// Attempt counts and final statuses match Go for every scripted case; waits are exactly 1 s then 2 s.
    /// </summary>
    /// <remarks>
    /// The waits and the disposal of retried responses are C#-only checks. No real sleeping: the test waits for the
    /// client to create its retry timer, advances the fake clock by exactly the expected delay, and repeats. The
    /// 10 s real timeout is only a hang guard.
    /// </remarks>
    [TestMethod]
    public async Task Retry_AttemptCountsMatchGo_DelaysAre1sThen2s()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int checked_cases = 0;
        foreach (var go in Golden.Load("http").GetProperty("retry").EnumerateArray().Where(c => !c.GetProperty("longPoll").GetBoolean()))
        {
            int[] statuses = go.GetProperty("statuses").EnumerateArray().Select(s => s.GetInt32()).ToArray();
            int expected_attempts = go.GetProperty("attempts").GetInt32();
            Rig rig = CreateRig(GoCookies);
            var responses = new List<TrackingResponse>();
            rig.Handler.Respond = (i, _) =>
            {
                var response = new TrackingResponse((HttpStatusCode)statuses[Math.Min(i, statuses.Length - 1)]);
                responses.Add(response);
                return response;
            };

            Task<HttpResponseMessage> task = rig.Client.PostAsync(GmEndpoints.Production.SignInGaia, RelayBody, GmContentType.PbLite, false, CancellationToken.None);

            // One Wait Per Retry: Timer Appears, Then Advance Exactly Attempt x 1 s
            for (int attempt = 1; attempt < expected_attempts; attempt++)
            {
                await rig.Time.TimerCreated.WaitAsync(guard.Token);
                Assert.AreEqual(attempt, rig.Handler.Requests.Count, "no early retry");
                rig.Time.Advance(TimeSpan.FromSeconds(attempt));
            }

            using HttpResponseMessage res = await task.WaitAsync(guard.Token);

            Assert.AreEqual(expected_attempts, rig.Handler.Requests.Count);
            Assert.AreEqual(go.GetProperty("finalStatus").GetInt32(), (int)res.StatusCode);

            // Exact Gaps Between Attempts
            for (int i = 1; i < rig.Handler.Requests.Count; i++)
            {
                Assert.AreEqual(TimeSpan.FromSeconds(i), rig.Handler.Requests[i].At - rig.Handler.Requests[i - 1].At, $"gap {i}");
            }

            // Every Retried Response Was Disposed; The Returned One Was Not
            Assert.AreEqual(expected_attempts, responses.Count);
            Assert.IsTrue(responses.Take(expected_attempts - 1).All(r => r.IsDisposed), "retried responses disposed");
            Assert.IsFalse(responses[^1].IsDisposed);

            checked_cases++;
        }

        Assert.AreEqual(3, checked_cases);
    }

    /// <summary>
    /// Long polls use the long-poll client and are never retried (Go case: one attempt on 503).
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

        // C#-only: the long-poll client has a 30 minute timeout
        using HttpClient long_poll = GmHttpClient.CreateLongPollClient(new FakeHttpHandler());
        Assert.AreEqual(TimeSpan.FromMinutes(30), long_poll.Timeout);
    }

    /// <summary>
    /// Error bodies decode into <see cref="GmRequestException"/> exactly when Go decodes them.
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
    /// C#-only: a protobuf POST sends binary and the typed response decodes by content type.
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
