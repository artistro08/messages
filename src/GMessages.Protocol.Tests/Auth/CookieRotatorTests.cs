using System.Net;
using System.Text;
using GMessages.Protocol.Auth;
using GMessages.Protocol.Http;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using GMessages.Protocol.Tests.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Protocol.Tests.Auth;

/// <summary>
/// Checks <see cref="CookieRotator"/>: the browser's <c>RotateCookies</c> request, its 10 minute cadence, failures
/// that keep the session, and stopping.
/// </summary>
/// <remarks>
/// The expected request is the one the Python <c>gemini-webapi</c> project sends from <c>rotate_1psidts</c>:
/// <see href="https://github.com/HanaokaYuzu/Gemini-API/blob/master/src/gemini_webapi/utils/rotate_1psidts.py"/>.
/// </remarks>
[TestClass]
public class CookieRotatorTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private const string OldPsidts = "old-psidts-value";
    private const string NewPsidts = "new-psidts-value";

    private sealed record Rig(CookieRotator Rotator, AuthData Auth, FakeHttpHandler Handler, SignalingTimeProvider Time);

    private static Rig CreateRig(ILogger? log = null)
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        auth.Cookies.Set(new Dictionary<string, string>
        {
            ["SAPISID"]          = "fake-sapisid",
            ["SID"]              = "fake-sid",
            ["__Secure-1PSIDTS"] = OldPsidts,
            ["__Secure-1PSID"]   = "fake-1psid",
        });

        var time    = new SignalingTimeProvider();
        var handler = new FakeHttpHandler { Time = time };
        handler.Respond = (_, _) => Rotated();

        var http    = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
        var rotator = new CookieRotator(http, auth, GmEndpoints.Production, time, log ?? NullLogger.Instance);
        return new Rig(rotator, auth, handler, time);
    }

    // Google's Answer: A New __Secure-1PSIDTS For All Of google.com
    private static HttpResponseMessage Rotated(params string[] more) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json", "[]"u8.ToArray(), [$"__Secure-1PSIDTS={NewPsidts}; Domain=.google.com; Max-Age=31536000; Path=/; Secure; HttpOnly", .. more]);

    private static int Count(FakeHttpHandler handler)
    {
        lock (handler.Requests)
        {
            return handler.Requests.Count;
        }
    }

    /// <summary>
    /// The request matches the browser's (POST, JSON body, accounts.google.com origin, cookies, no SAPISIDHASH),
    /// and the returned <c>__Secure-1PSIDTS</c> replaces the old one in the jar, which asks for a save.
    /// </summary>
    [TestMethod]
    public async Task Rotate_SendsTheBrowserRequest_StoresNewPsidts()
    {
        Rig rig      = CreateRig();
        bool changed = false;
        rig.Auth.Cookies.Changed += (_, _) => changed = true;

        await rig.Rotator.RotateIfDueAsync(CancellationToken.None);

        RecordedRequest req = rig.Handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, req.Method);
        Assert.AreEqual("https://accounts.google.com/RotateCookies", req.Url);
        Assert.AreEqual("application/json", req.Headers["Content-Type"]);
        Assert.AreEqual("https://accounts.google.com", req.Headers["Origin"]);
        Assert.AreEqual("[000,\"-0000000000000000000\"]", Encoding.UTF8.GetString(req.Body));
        StringAssert.Contains(req.Headers["Cookie"], $"__Secure-1PSIDTS={OldPsidts}");
        StringAssert.Contains(req.Headers["Cookie"], "SID=fake-sid");
        StringAssert.Contains(req.Headers["Cookie"], "__Secure-1PSID=fake-1psid");
        Assert.IsFalse(req.Headers.ContainsKey("Authorization"), "no SAPISIDHASH on the rotation request");

        Assert.AreEqual(NewPsidts, rig.Auth.Cookies.Snapshot()["__Secure-1PSIDTS"]);
        Assert.IsTrue(changed, "the jar asks for a save");
    }

    /// <summary>
    /// The run rotates at once, then every 10 minutes; a check within 10 minutes of the last rotation sends nothing,
    /// and the first check after a long gap (start or resume) rotates right away.
    /// </summary>
    [TestMethod]
    public async Task Rotate_RunsEveryTenMinutes_AndBeforeFirstRequestAfterLongGap()
    {
        Rig rig       = CreateRig();
        using var cts = new CancellationTokenSource();

        // Start: Rotates At Once, Then Waits 10 Minutes
        Task run = rig.Rotator.RunAsync(cts.Token);
        Assert.IsTrue(await rig.Time.TimerCreated.WaitAsync(Wait));
        Assert.AreEqual(1, Count(rig.Handler));
        Assert.AreEqual(CookieRotator.Interval, rig.Time.DueTimes.Last());

        // A Check Before The First Request Within 10 Minutes Sends Nothing
        rig.Time.Advance(TimeSpan.FromMinutes(9));
        await rig.Rotator.RotateIfDueAsync(CancellationToken.None);
        Assert.AreEqual(1, Count(rig.Handler));

        // 10 Minutes: Rotates Again
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.IsTrue(await rig.Time.TimerCreated.WaitAsync(Wait));
        Assert.AreEqual(2, Count(rig.Handler));
        Assert.AreEqual(TimeSpan.FromMinutes(10), rig.Handler.Requests[1].At - rig.Handler.Requests[0].At);

        // Stop, Then A Long Gap (Asleep): The First Check On Resume Rotates
        await cts.CancelAsync();
        await run.WaitAsync(Wait);
        rig.Time.Advance(TimeSpan.FromHours(2));
        await rig.Rotator.RotateIfDueAsync(CancellationToken.None);
        Assert.AreEqual(3, Count(rig.Handler));
    }

    /// <summary>
    /// A refused rotation keeps the session and its cookies, logs the error type and status, never a cookie value,
    /// and is tried again at the next interval; the success after it logs the names and expiry only.
    /// </summary>
    [TestMethod]
    public async Task Rotate_Failure_KeepsSession_LogsStatus_NeverValues()
    {
        var log             = new CollectingLogger();
        Rig rig             = CreateRig(log);
        var success         = rig.Handler.Respond;
        rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.Unauthorized, "text/plain", "denied"u8.ToArray());

        // Failure: Session And Cookies Kept, Status Logged
        await rig.Rotator.RotateIfDueAsync(CancellationToken.None);
        Assert.IsNotNull(rig.Auth.Browser);
        Assert.AreEqual(OldPsidts, rig.Auth.Cookies.Snapshot()["__Secure-1PSIDTS"]);
        CollectionAssert.Contains(log.Messages, "Cookie rotation failed: HttpRequestException HTTP 401");

        // No Retry Before The Next Interval
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.Rotator.RotateIfDueAsync(CancellationToken.None);
        Assert.AreEqual(1, Count(rig.Handler));

        // Next Interval: Tried Again And Succeeds
        rig.Handler.Respond = success;
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.Rotator.RotateIfDueAsync(CancellationToken.None);
        Assert.AreEqual(2, Count(rig.Handler));
        Assert.AreEqual(NewPsidts, rig.Auth.Cookies.Snapshot()["__Secure-1PSIDTS"]);
        string expires = (rig.Time.GetUtcNow() + TimeSpan.FromSeconds(31536000)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        CollectionAssert.Contains(log.Messages, $"Cookies rotated: __Secure-1PSIDTS (expires {expires})");

        // Never A Value
        foreach (string line in log.Messages)
        {
            foreach (string secret in new[] { OldPsidts, NewPsidts, "fake-sapisid", "fake-sid", "fake-1psid" })
            {
                Assert.IsFalse(line.Contains(secret, StringComparison.Ordinal), $"log line shows a cookie value: {line}");
            }
        }
    }

    /// <summary>
    /// Stopping the run (sign-out, unpair, disconnect) sends nothing more, and an account that is no longer paired
    /// is never rotated.
    /// </summary>
    [TestMethod]
    public async Task Rotate_StopsOnSignOut()
    {
        Rig rig       = CreateRig();
        using var cts = new CancellationTokenSource();

        Task run = rig.Rotator.RunAsync(cts.Token);
        Assert.IsTrue(await rig.Time.TimerCreated.WaitAsync(Wait));
        Assert.AreEqual(1, Count(rig.Handler));

        // Stopped: Nothing More, However Long It Waits
        await cts.CancelAsync();
        await run.WaitAsync(Wait);
        rig.Time.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(1, Count(rig.Handler));

        // Unpaired: Never Rotated
        rig.Auth.ClearPairing();
        await rig.Rotator.RotateIfDueAsync(CancellationToken.None);
        Assert.AreEqual(1, Count(rig.Handler));
    }

    /// <summary>
    /// A caller that arrives while a rotation is in flight (the resume reconnect racing the wake timer) waits for it
    /// and returns with the new cookie in the jar; no second request goes out.
    /// </summary>
    [TestMethod]
    public async Task Rotate_SecondCallerWaitsForTheOneInFlight()
    {
        Rig rig             = CreateRig();
        using var release   = new ManualResetEventSlim();
        rig.Handler.Respond = (_, _) =>
        {
            release.Wait(Wait);
            return Rotated();
        };

        // First Caller Is Stuck On The Wire
        Task first = Task.Run(() => rig.Rotator.RotateIfDueAsync(CancellationToken.None));
        Assert.IsTrue(SpinWait.SpinUntil(() => Count(rig.Handler) == 1, Wait));

        // Second Caller Waits Instead Of Returning With The Old Cookie
        Task second = rig.Rotator.RotateIfDueAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.IsFalse(second.IsCompleted, "the second caller waits for the rotation in flight");
        Assert.AreEqual(OldPsidts, rig.Auth.Cookies.Snapshot()["__Secure-1PSIDTS"]);

        release.Set();
        await second.WaitAsync(Wait);
        Assert.AreEqual(NewPsidts, rig.Auth.Cookies.Snapshot()["__Secure-1PSIDTS"]);
        await first.WaitAsync(Wait);
        Assert.AreEqual(1, Count(rig.Handler));
    }

    /// <summary>
    /// A rotation canceled midway (the run stopping) is not counted as a try: a caller waiting on it with its own
    /// token rotates itself instead of going out with the old cookie.
    /// </summary>
    [TestMethod]
    public async Task Rotate_CanceledMidway_NextCallerRotates()
    {
        Rig rig          = CreateRig();
        using var cts    = new CancellationTokenSource();
        rig.Handler.Hang = _ => Count(rig.Handler) == 1;

        Task first = rig.Rotator.RotateIfDueAsync(cts.Token);
        Assert.IsTrue(SpinWait.SpinUntil(() => Count(rig.Handler) == 1, Wait));
        Task second = rig.Rotator.RotateIfDueAsync(CancellationToken.None);

        // Cancel The First: It Throws, The Waiting Caller Rotates
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first.WaitAsync(Wait));
        await second.WaitAsync(Wait);
        Assert.AreEqual(2, Count(rig.Handler));
        Assert.AreEqual(NewPsidts, rig.Auth.Cookies.Snapshot()["__Secure-1PSIDTS"]);
    }

    /// <summary>
    /// Only cookies for all of google.com are kept from the rotation answer: host-only cookies (<c>__Host-*</c>,
    /// accounts.google.com-only ones) never reach the jar, so they are never sent to the Messages hosts.
    /// </summary>
    [TestMethod]
    public async Task Rotate_KeepsOnlyGoogleWideCookies()
    {
        var log             = new CollectingLogger();
        Rig rig             = CreateRig(log);
        rig.Handler.Respond = (_, _) => Rotated("__Host-GAPS=host-value; Path=/; Secure", "ACCOUNT_CHOOSER=chooser-value; Domain=accounts.google.com; Path=/", "LSID=lsid-value; Path=/; Secure");

        await rig.Rotator.RotateIfDueAsync(CancellationToken.None);

        IReadOnlyDictionary<string, string> jar = rig.Auth.Cookies.Snapshot();
        Assert.AreEqual(NewPsidts, jar["__Secure-1PSIDTS"]);
        Assert.IsFalse(jar.ContainsKey("__Host-GAPS"));
        Assert.IsFalse(jar.ContainsKey("ACCOUNT_CHOOSER"));
        Assert.IsFalse(jar.ContainsKey("LSID"));
        Assert.IsTrue(log.Messages.Any(m => m.StartsWith("Cookies rotated: __Secure-1PSIDTS (expires ", StringComparison.Ordinal) && !m.Contains(',', StringComparison.Ordinal)));
    }

    /// <summary>
    /// A session without <c>__Secure-1PSID</c> (paired before it was captured) sends nothing, which Google would only
    /// refuse with a 403, and says so once, however often it is checked.
    /// </summary>
    [TestMethod]
    public async Task Rotate_WithoutPsid_SkipsAndSaysSoOnce()
    {
        var log     = new CollectingLogger();
        Rig rig     = CreateRig(log);
        var cookies = new Dictionary<string, string>(rig.Auth.Cookies.Snapshot());
        cookies.Remove("__Secure-1PSID");
        rig.Auth.Cookies.Set(cookies);

        await rig.Rotator.RotateIfDueAsync(CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromMinutes(30));
        await rig.Rotator.RotateIfDueAsync(CancellationToken.None);

        Assert.AreEqual(0, Count(rig.Handler));
        Assert.AreEqual(1, log.Messages.Count(m => m == "Cookie rotation skipped: __Secure-1PSID missing"));
    }

    /// <summary>
    /// A refused rotation logs the answer's header names, and which of them belong to a device-bound session
    /// (DBSC), so a live 403 tells a bound session apart from a bad request. Never a header value.
    /// </summary>
    [TestMethod]
    public async Task Rotate_Refused_LogsHeaderNamesAndBoundSessionHeaders_NeverValues()
    {
        var log             = new CollectingLogger();
        Rig rig             = CreateRig(log);
        rig.Handler.Respond = (_, _) =>
        {
            HttpResponseMessage res = FakeHttpHandler.Response(HttpStatusCode.Forbidden, "text/html", "no"u8.ToArray(), "NID=secret-nid; Domain=.google.com; Path=/");
            res.Headers.TryAddWithoutValidation("Secure-Session-Challenge", "\"secret-challenge\";id=\"secret-id\"");
            res.Headers.TryAddWithoutValidation("X-Frame-Options", "secret-xfo");
            return res;
        };

        await rig.Rotator.RotateIfDueAsync(CancellationToken.None);

        CollectionAssert.Contains(log.Messages, "Cookie rotation failed: HttpRequestException HTTP 403");
        CollectionAssert.Contains(log.Messages, "Cookie rotation answer headers: Content-Length, Content-Type, Secure-Session-Challenge, Set-Cookie, X-Frame-Options; device-bound session headers: Secure-Session-Challenge", string.Join(" | ", log.Messages));
        Assert.IsFalse(log.Messages.Any(m => m.Contains("secret", StringComparison.Ordinal)), "a header value was logged");
    }

    /// <summary>
    /// A refused answer's cookies never reach the jar: a 403 that clears or replaces <c>__Secure-1PSIDTS</c> leaves
    /// the jar as it was and asks for no save, since a cleared cookie there would sign the user out.
    /// </summary>
    [TestMethod]
    public async Task Rotate_RefusedAnswer_NeverTouchesTheJar()
    {
        foreach (string set_cookie in new[] { "__Secure-1PSIDTS=; Domain=.google.com; Max-Age=0; Path=/; Secure", "__Secure-1PSIDTS=refused-value; Domain=.google.com; Path=/; Secure" })
        {
            Rig rig                                    = CreateRig();
            IReadOnlyDictionary<string, string> before = rig.Auth.Cookies.Snapshot();
            bool changed                               = false;
            rig.Auth.Cookies.Changed += (_, _) => changed = true;
            rig.Handler.Respond       = (_, _) => FakeHttpHandler.Response(HttpStatusCode.Forbidden, "text/html", "no"u8.ToArray(), set_cookie, "NID=nid-value; Domain=.google.com; Path=/");

            await rig.Rotator.RotateIfDueAsync(CancellationToken.None);

            CollectionAssert.AreEquivalent(before.ToArray(), rig.Auth.Cookies.Snapshot().ToArray(), set_cookie);
            Assert.IsFalse(changed, "a refused answer asks for no save");
        }
    }

    /// <summary>
    /// After 3 refusals in a row the rotation backs off to every 6 hours: one Warning per refusal (3, then 1 per
    /// back-off period) plus one saying it backs off; a connect never sends, starts or waits on a rotation while
    /// backed off, even one that is due or in flight; the background run tries at the 6 hour mark, and a 2xx there
    /// returns to the 10 minute cadence.
    /// </summary>
    [TestMethod]
    public async Task Rotate_ThreeRefusals_BackOffToSixHours_Quietly_AndConnectNeverWaits()
    {
        var log             = new CollectingLogger();
        Rig rig             = CreateRig(log);
        using var release   = new ManualResetEventSlim();
        using var cts       = new CancellationTokenSource();
        rig.Handler.Respond = (index, _) =>
        {
            if (index < 3)
            {
                return FakeHttpHandler.Response(HttpStatusCode.Forbidden, "text/html", "no"u8.ToArray());
            }

            release.Wait(Wait);
            return Rotated();
        };

        // Three Refusals, 10 Minutes Apart
        DateTimeOffset third = rig.Time.GetUtcNow() + TimeSpan.FromMinutes(20);
        for (int i = 0; i < 3; i++)
        {
            await rig.Rotator.RotateIfDueAsync(CancellationToken.None);
            rig.Time.Advance(i < 2 ? CookieRotator.Interval : TimeSpan.Zero);
        }

        Assert.AreEqual(3, Count(rig.Handler));
        Assert.AreEqual(3, log.Messages.Count(m => m == "Cookie rotation failed: HttpRequestException HTTP 403"));
        CollectionAssert.Contains(log.Messages, "Cookie rotation refused 3 times in a row; next try in 6 h");

        // Backed Off: A Due-Looking Connect 10 Minutes Later Sends Nothing
        rig.Time.Advance(CookieRotator.Interval);
        DateTimeOffset next = await rig.Rotator.RotateIfDueAsync(CancellationToken.None);
        Assert.AreEqual(3, Count(rig.Handler));
        Assert.AreEqual(third + CookieRotator.BackOff, next);

        // The Background Run Waits Out The Back-Off
        Task run = rig.Rotator.RunAsync(cts.Token);
        Assert.IsTrue(await rig.Time.TimerCreated.WaitAsync(Wait));
        Assert.AreEqual(CookieRotator.BackOff - CookieRotator.Interval, rig.Time.DueTimes.Last());
        Assert.AreEqual(3, Count(rig.Handler));

        // At 6 Hours It Tries; A Connect Meanwhile Neither Waits Nor Sends
        Task advance = Task.Run(() => rig.Time.Advance(CookieRotator.BackOff - CookieRotator.Interval));
        Assert.IsTrue(SpinWait.SpinUntil(() => Count(rig.Handler) == 4, Wait));
        Task connect = rig.Rotator.RotateIfDueAsync(CancellationToken.None);
        Assert.IsTrue(connect.IsCompleted, "a connect never waits on a rotation while backed off");
        Assert.AreEqual(4, Count(rig.Handler));

        // A 2xx Returns To The 10 Minute Cadence
        release.Set();
        await advance.WaitAsync(Wait);
        Assert.IsTrue(await rig.Time.TimerCreated.WaitAsync(Wait));
        Assert.AreEqual(CookieRotator.Interval, rig.Time.DueTimes.Last());
        Assert.AreEqual(NewPsidts, rig.Auth.Cookies.Snapshot()["__Secure-1PSIDTS"]);
        Assert.AreEqual(3, log.Messages.Count(m => m.StartsWith("Cookie rotation failed", StringComparison.Ordinal)));

        await cts.CancelAsync();
        await run.WaitAsync(Wait);
    }

    /// <summary>
    /// The DBSC header names (the W3C draft's <c>Secure-Session-*</c> and <c>Sec-Secure-Session-Id</c>, and
    /// Chrome's Google-only <c>Sec-Session-Google-*</c>) are recognized, any case; other headers are not.
    /// </summary>
    [TestMethod]
    public void IsBoundSessionHeader_KnowsTheDbscNames()
    {
        foreach (string name in new[] { "Secure-Session-Registration", "secure-session-challenge", "Sec-Secure-Session-Id", "Sec-Session-Google-Registration", "Sec-Session-Registration" })
        {
            Assert.IsTrue(CookieRotator.IsBoundSessionHeader(name), name);
        }

        foreach (string name in new[] { "Set-Cookie", "Sec-Fetch-Site", "X-Session" })
        {
            Assert.IsFalse(CookieRotator.IsBoundSessionHeader(name), name);
        }
    }
}
