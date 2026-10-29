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
        });

        var time    = new SignalingTimeProvider();
        var handler = new FakeHttpHandler { Time = time };
        handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json", "[]"u8.ToArray(), $"__Secure-1PSIDTS={NewPsidts}; Max-Age=31536000; Path=/; Secure; HttpOnly");

        var http    = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
        var rotator = new CookieRotator(http, auth, GmEndpoints.Production, time, log ?? NullLogger.Instance);
        return new Rig(rotator, auth, handler, time);
    }

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
            foreach (string secret in new[] { OldPsidts, NewPsidts, "fake-sapisid", "fake-sid" })
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
}
