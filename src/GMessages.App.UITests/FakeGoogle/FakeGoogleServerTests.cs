using System.Net;
using GMessages.Protocol;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.App.UITests.FakeGoogle;

/// <summary>
/// Protocol-level checks of <see cref="FakeGoogleServer"/> and <see cref="FakePhone"/>: a real <see cref="GmClient"/>
/// pairs and connects against them over real HTTP on 127.0.0.1.
/// </summary>
/// <remarks>
/// No UI: these run in the normal <c>dotnet test</c> of this project. Every wait is a signal with a 30 s hang guard,
/// never a sleep. The client saves its session to a temp file, never the owner's data folder.
/// </remarks>
[TestClass]
public sealed class FakeGoogleServerTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static readonly string[] CookieNames = ["SAPISID", "SID", "HSID", "SSID", "APISID", "OSID", "__Secure-1PSIDTS"];

    // =========================================================================
    // RIG
    // =========================================================================

    private sealed class Rig : IAsyncDisposable
    {
        public Rig()
        {
            Auth = AuthData.CreateNew();
            Auth.Cookies.Set(FakeGoogleServer.SignInCookies);
            SessionPath = Path.Combine(Path.GetTempPath(), "fake-google-test-" + Guid.NewGuid().ToString("N") + ".bin");
            Endpoints   = new GmEndpoints(Server.BaseUrl, Server.BaseUrl, Server.BaseUrl);
            Client      = GmClient.Create(Auth, new SessionStore(SessionPath), Endpoints, TimeProvider.System, NullLoggerFactory.Instance);
            Client.EventRaised += e =>
            {
                if (e is ListenFatalError)
                {
                    Fatal.TrySetResult();
                }
            };
        }

        public FakeGoogleServer Server { get; } = new();

        public AuthData Auth { get; }

        public GmEndpoints Endpoints { get; }

        public GmClient Client { get; }

        public string SessionPath { get; }

        public TaskCompletionSource Fatal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            File.Delete(SessionPath);
        }
    }

    // Starts Pairing; The Returned Task Gives The Emoji The Client Showed
    private static (Task Pair, Task<string> Shown) StartPairing(Rig rig)
    {
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pair = rig.Client.PairAsync(e => shown.TrySetResult(e), CancellationToken.None);
        return (pair, shown.Task);
    }

    private static int Count(FakeGoogleServer server, string suffix) => server.RequestedPaths.Count(p => p.EndsWith(suffix, StringComparison.Ordinal));

    // Waits For A Condition Without Sleeping A Fixed Time
    private static void WaitUntil(Func<bool> condition, string what) => Assert.IsTrue(SpinWait.SpinUntil(condition, HangGuard), what);

    // =========================================================================
    // PAIRING
    // =========================================================================

    /// <summary>
    /// The right tap pairs: the client's emoji is the phone's, both sides hold the same request keys, and the client
    /// ends connected.
    /// </summary>
    [TestMethod]
    public async Task Pair_TapCorrect_Pairs_AndEmojiMatches()
    {
        await using var rig = new Rig();

        (Task pair, Task<string> shown) = StartPairing(rig);
        string emoji = await shown.WaitAsync(HangGuard);
        await rig.Server.Phone.FinishReceived.WaitAsync(HangGuard);

        Assert.AreEqual(rig.Server.Phone.ShownEmoji, emoji);
        rig.Server.Phone.Tap(true);
        await pair.WaitAsync(HangGuard);

        Assert.IsNotNull(rig.Server.Phone.RequestCrypto);
        CollectionAssert.AreEqual(rig.Server.Phone.RequestCrypto.AesKey, rig.Auth.RequestCrypto.AesKey, "same AES key");
        CollectionAssert.AreEqual(rig.Server.Phone.RequestCrypto.HmacKey, rig.Auth.RequestCrypto.HmacKey, "same HMAC key");
        WaitUntil(() => rig.Client.IsConnected, "connected after pairing");
        Assert.AreEqual(1, Count(rig.Server, "/web/config"));
        Assert.AreEqual(1, Count(rig.Server, "Registration/SignInGaia"));
    }

    /// <summary>
    /// The wrong tap fails the pairing with <see cref="PairingFailure.IncorrectEmoji"/>.
    /// </summary>
    [TestMethod]
    public async Task Pair_TapWrong_IsIncorrectEmoji()
    {
        await using var rig = new Rig();

        (Task pair, Task<string> shown) = StartPairing(rig);
        await shown.WaitAsync(HangGuard);
        await rig.Server.Phone.FinishReceived.WaitAsync(HangGuard);
        rig.Server.Phone.Tap(false);

        var ex = await Assert.ThrowsExactlyAsync<PairingException>(() => pair.WaitAsync(HangGuard));
        Assert.AreEqual(PairingFailure.IncorrectEmoji, ex.Failure);
        Assert.IsNull(rig.Server.Phone.RequestCrypto);
    }

    /// <summary>
    /// After a wrong tap, a second attempt on the same server gets a fresh signal and emoji and pairs.
    /// </summary>
    [TestMethod]
    public async Task Pair_RetryAfterWrongTap_Pairs()
    {
        await using var rig = new Rig();
        (Task first, Task<string> first_shown) = StartPairing(rig);
        await first_shown.WaitAsync(HangGuard);
        await rig.Server.Phone.FinishReceived.WaitAsync(HangGuard);
        rig.Server.Phone.Tap(false);
        await Assert.ThrowsExactlyAsync<PairingException>(() => first.WaitAsync(HangGuard));

        (Task second, Task<string> second_shown) = StartPairing(rig);
        string emoji = await second_shown.WaitAsync(HangGuard);
        await rig.Server.Phone.FinishReceived.WaitAsync(HangGuard);
        Assert.AreEqual(rig.Server.Phone.ShownEmoji, emoji);
        rig.Server.Phone.Tap(true);

        await second.WaitAsync(HangGuard);
        CollectionAssert.AreEqual(rig.Server.Phone.RequestCrypto!.AesKey, rig.Auth.RequestCrypto.AesKey);
    }

    /// <summary>
    /// <see cref="FakeGoogleServer.RejectCookies"/> makes the sign-in a 401.
    /// </summary>
    [TestMethod]
    public async Task Pair_RejectCookies_Is401()
    {
        await using var rig = new Rig();
        rig.Server.RejectCookies = true;

        var ex = await Assert.ThrowsExactlyAsync<GmRequestException>(() => rig.Client.PairAsync(_ => { }, CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual(401, ex.StatusCode);
    }

    // =========================================================================
    // CONNECTION
    // =========================================================================

    /// <summary>
    /// After pairing, connect reopens the long poll; killing it with 401 raises <see cref="ListenFatalError"/>.
    /// </summary>
    [TestMethod]
    public async Task Connect_AfterPairing_ThenKill401_IsListenFatalError()
    {
        await using var rig = new Rig();
        (Task pair, Task<string> shown) = StartPairing(rig);
        await shown.WaitAsync(HangGuard);
        await rig.Server.Phone.FinishReceived.WaitAsync(HangGuard);
        rig.Server.Phone.Tap(true);
        await pair.WaitAsync(HangGuard);
        WaitUntil(() => rig.Client.IsConnected, "connected after pairing");
        int polls = Count(rig.Server, "Messaging/ReceiveMessages");

        await rig.Client.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        WaitUntil(() => Count(rig.Server, "Messaging/ReceiveMessages") > polls && rig.Client.IsConnected, "reconnected");

        rig.Server.KillLongPoll(401);
        await rig.Fatal.Task.WaitAsync(HangGuard);
        Assert.IsFalse(rig.Client.IsConnected);
    }

    // =========================================================================
    // SIGN-IN PAGE
    // =========================================================================

    /// <summary>
    /// The sign-in page sets no cookies and has the "signin" button; the login POST sets all seven cookies.
    /// </summary>
    [TestMethod]
    public async Task SignInPage_SetsCookiesOnlyAfterLogin()
    {
        await using var server = new FakeGoogleServer();
        using var handler      = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        using var http         = new HttpClient(handler);

        // Page: No Cookies
        using HttpResponseMessage page = await http.GetAsync(new Uri(server.BaseUrl, "/web/authentication")).WaitAsync(HangGuard);
        string html = await page.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.IsFalse(page.Headers.Contains("Set-Cookie"), "no cookies before login");
        StringAssert.Contains(html, "id=\"signin\"");

        // Login: All Seven
        using HttpResponseMessage login = await http.PostAsync(new Uri(server.BaseUrl, "/web/authentication/login"), null).WaitAsync(HangGuard);
        string[] names = [.. login.Headers.GetValues("Set-Cookie").Select(c => c[..c.IndexOf('=', StringComparison.Ordinal)])];
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        CollectionAssert.AreEquivalent(CookieNames, names);
        StringAssert.Contains(await login.Content.ReadAsStringAsync(), "Signed in");
    }
}
