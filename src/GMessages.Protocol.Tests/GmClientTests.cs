using System.Net;
using System.Text;
using System.Text.Json;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using GMessages.Protocol.Tests.Pairing;
using GMessages.Protocol.Tests.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Protocol.Tests;

/// <summary>
/// Checks how <see cref="GmClient"/> wires connect, pairing, unpairing and session saving together.
/// </summary>
/// <remarks>
/// <c>Connect_RequestOrderAndBodies_MatchGo</c> compares with <c>connect.json</c>, which records what Go's
/// <c>Connect()</c> sends up to <c>postConnect</c>'s <c>GET_UPDATES</c>. Before comparing, the C# test copies Go's
/// random values over its own: the refresh request ID, timestamp and signature, the listen request ID and the
/// session ID. The other tests are C#-only: the behavior was read from <c>Connect</c>, <c>Unpair</c>,
/// <c>DoGaiaPairing</c> and the owner's 5 second save debounce, and is driven by a fake clock.
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go"/>.
/// </remarks>
[TestClass]
public class GmClientTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private const string PbLiteMime = "application/json+protobuf";

    private sealed class Rig : IAsyncDisposable
    {
        public required GmClient Client { get; init; }

        public required AuthData Auth { get; init; }

        public required FakeHttpHandler Handler { get; init; }

        public required SignalingTimeProvider Time { get; init; }

        public required string SessionPath { get; init; }

        public List<object> Events { get; } = [];

        // Answer For A Request, Or Null For The Default (An Open Quiet Stream For The Long Poll, "[]" Otherwise)
        public Func<RecordedRequest, HttpResponseMessage?>? Respond { get; set; }

        public SessionStore Store => new(SessionPath);

        public RecordedRequest[] Requests(string suffix) { lock (Handler.Requests) { return [.. Handler.Requests.Where(r => r.Url.EndsWith(suffix, StringComparison.Ordinal))]; } }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            File.Delete(SessionPath);
        }
    }

    private static Rig CreateRig(AuthData? auth = null)
    {
        auth      ??= AuthDataGoldenTests.BuildGolden();
        var time    = new SignalingTimeProvider();
        var handler = new FakeHttpHandler { Time = time };
        string path = Path.Combine(Path.GetTempPath(), "gmclient-test-" + Guid.NewGuid().ToString("N") + ".bin");
        var client  = GmClient.Create(auth, new SessionStore(path), GmEndpoints.Production, time, NullLoggerFactory.Instance, handler);
        var rig     = new Rig { Client = client, Auth = auth, Handler = handler, Time = time, SessionPath = path };

        client.EventRaised += e => { lock (rig.Events) { rig.Events.Add(e); } };
        handler.Respond = (_, req) =>
        {
            if (rig.Respond?.Invoke(req) is { } custom)
            {
                return custom;
            }

            if (req.Url.EndsWith("/ReceiveMessages", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new OpenStream("[["u8.ToArray())) };
            }

            return FakeHttpHandler.Response(HttpStatusCode.OK, PbLiteMime, "[]"u8.ToArray());
        };
        return rig;
    }

    private static void WaitUntil(Func<bool> condition, string what) => Assert.IsTrue(SpinWait.SpinUntil(condition, HangGuard), what);

    private static ActionType ActionOf(RecordedRequest req) =>
        OutgoingRPCData.Parser.ParseFrom(PbLiteSerializer.Deserialize<OutgoingRPCMessage>(req.Body).Data.MessageData).Action;

    private static string Text(IMessage message) => Encoding.UTF8.GetString(PbLiteSerializer.Serialize(message));

    // =========================================================================
    // CONNECT
    // =========================================================================

    /// <summary>
    /// Connect sends the token refresh, opens the long poll, then (after 2 s and 1 s) sends GET_UPDATES, exactly as
    /// Go's Connect does once the random values are copied.
    /// </summary>
    [TestMethod]
    public async Task Connect_RequestOrderAndBodies_MatchGo()
    {
        JsonElement go        = Golden.Load("connect");
        JsonElement responses = go.GetProperty("responses");
        JsonElement[] go_reqs = [.. go.GetProperty("requests").EnumerateArray()];
        string go_session     = go.GetProperty("sessionId").GetString()!;

        AuthData auth = AuthDataGoldenTests.BuildGolden();
        await using Rig rig = CreateRig(auth);
        auth.TachyonExpiry = rig.Time.GetUtcNow() + TimeSpan.FromMinutes(30);
        rig.Respond = req =>
        {
            if (req.Url.EndsWith("/RegisterRefresh", StringComparison.Ordinal))
            {
                return FakeHttpHandler.Response(HttpStatusCode.OK, PbLiteMime, Encoding.UTF8.GetBytes(responses.GetProperty("refresh").GetString()!));
            }

            if (req.Url.EndsWith("/ReceiveMessages", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new OpenStream(Encoding.UTF8.GetBytes(responses.GetProperty("longPoll").GetString()!))) };
            }

            return null;
        };

        await rig.Client.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);

        // Post-Connect Waits: 2 s, Then 1 s
        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromSeconds(2)) > 0, "2 s post-connect wait");
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromSeconds(1)) > 0, "1 s post-connect wait");
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        WaitUntil(() => rig.Requests("/SendMessage").Length > 0, "GET_UPDATES");
        Assert.IsTrue(rig.Client.IsConnected);

        // Same Order And URLs
        RecordedRequest[] sent;
        lock (rig.Handler.Requests)
        {
            sent = [.. rig.Handler.Requests];
        }

        Assert.AreEqual(go_reqs.Length, sent.Length, "request count");
        for (int i = 0; i < go_reqs.Length; i++)
        {
            Assert.AreEqual(go_reqs[i].GetProperty("url").GetString(), sent[i].Url, $"request {i} URL");
            Assert.AreEqual(go_reqs[i].GetProperty("method").GetString(), sent[i].Method.Method, $"request {i} method");
        }

        // Refresh: Copy The Request ID, Timestamp And Signature (Signature Checks Live In The Refresh Tests)
        string go_refresh_text  = go_reqs[0].GetProperty("bodyText").GetString()!;
        var go_refresh          = PbLiteSerializer.Deserialize<RegisterRefreshRequest>(Encoding.UTF8.GetBytes(go_refresh_text));
        var cs_refresh          = PbLiteSerializer.Deserialize<RegisterRefreshRequest>(sent[0].Body);
        cs_refresh.MessageAuth.RequestID = go_refresh.MessageAuth.RequestID;
        cs_refresh.UnixTimestamp         = go_refresh.UnixTimestamp;
        cs_refresh.Signature             = go_refresh.Signature;
        Assert.AreEqual(go_refresh_text, Text(cs_refresh), "refresh body");

        // Long Poll: Copy The Listen Request ID
        string go_receive_text = go_reqs[1].GetProperty("bodyText").GetString()!;
        var go_receive         = PbLiteSerializer.Deserialize<ReceiveMessagesRequest>(Encoding.UTF8.GetBytes(go_receive_text));
        var cs_receive         = PbLiteSerializer.Deserialize<ReceiveMessagesRequest>(sent[1].Body);
        cs_receive.Auth.RequestID = go_receive.Auth.RequestID;
        Assert.AreEqual(go_receive_text, Text(cs_receive), "long poll body (new token)");

        // GET_UPDATES: Copy The Session ID
        var cs_send  = PbLiteSerializer.Deserialize<OutgoingRPCMessage>(sent[2].Body);
        var inner    = OutgoingRPCData.Parser.ParseFrom(cs_send.Data.MessageData);
        Assert.AreEqual(rig.Client.Rpc.SessionId, inner.RequestID, "request ID is the new session ID");
        inner.RequestID           = go_session;
        inner.SessionID           = go_session;
        cs_send.Data.MessageData  = inner.ToByteString();
        cs_send.Data.RequestID    = go_session;
        cs_send.Auth.RequestID    = go_session;
        Assert.AreEqual(go_reqs[2].GetProperty("bodyText").GetString(), Text(cs_send), "GET_UPDATES body");

        await rig.Client.DisconnectAsync().WaitAsync(HangGuard);
        Assert.IsFalse(rig.Client.IsConnected);
    }

    /// <summary>
    /// Without a browser device (never paired), connect fails before sending anything, as Go's checkLoggedIn does.
    /// </summary>
    [TestMethod]
    public async Task Connect_NotLoggedIn_Throws()
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        auth.Browser  = null;
        await using Rig rig = CreateRig(auth);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rig.Client.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard));

        Assert.IsFalse(rig.Client.IsLoggedIn);
        Assert.AreEqual(0, rig.Handler.Requests.Count);
    }

    /// <summary>
    /// A 401 on the connect-time token refresh throws <see cref="SessionExpiredException"/> and opens no long poll.
    /// </summary>
    [TestMethod]
    public async Task Connect_FatalRefresh_ThrowsSessionExpired()
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        await using Rig rig = CreateRig(auth);
        auth.TachyonExpiry = rig.Time.GetUtcNow() + TimeSpan.FromMinutes(30);
        rig.Respond        = req => req.Url.EndsWith("/RegisterRefresh", StringComparison.Ordinal) ? FakeHttpHandler.Response(HttpStatusCode.Unauthorized) : null;

        await Assert.ThrowsExactlyAsync<SessionExpiredException>(() => rig.Client.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual(0, rig.Requests("/ReceiveMessages").Length);
        Assert.IsFalse(rig.Client.IsConnected);
    }

    // =========================================================================
    // SESSION SAVING (C#-ONLY)
    // =========================================================================

    /// <summary>
    /// Cookie changes save the session once, 5 s after the first change; a second change inside that window rides
    /// along instead of starting another timer.
    /// </summary>
    [TestMethod]
    public async Task CookieChange_SavesSessionDebounced()
    {
        await using Rig rig = CreateRig();

        rig.Auth.Cookies.UpdateFrom(FakeHttpHandler.Response(HttpStatusCode.OK, setCookies: "SID=new-sid; Path=/"));
        rig.Auth.Cookies.UpdateFrom(FakeHttpHandler.Response(HttpStatusCode.OK, setCookies: "SID=newer-sid; Path=/"));

        Assert.AreEqual(1, rig.Time.CountTimers(GmClient.SaveDebounce), "one debounce timer");
        rig.Time.Advance(GmClient.SaveDebounce - TimeSpan.FromMilliseconds(1));
        Assert.IsFalse(File.Exists(rig.SessionPath), "not saved before 5 s");

        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        WaitUntil(() => File.Exists(rig.SessionPath), "saved after 5 s");
        Assert.AreEqual("newer-sid", rig.Store.Load()!.Cookies.Snapshot()["SID"]);

        // A Later Change Starts A New Window, Flushed On Dispose
        rig.Auth.Cookies.UpdateFrom(FakeHttpHandler.Response(HttpStatusCode.OK, setCookies: "SID=final-sid; Path=/"));
        await rig.Client.DisposeAsync();
        Assert.AreEqual("final-sid", rig.Store.Load()!.Cookies.Snapshot()["SID"]);
    }

    // =========================================================================
    // PAIRING AND UNPAIRING (C#-ONLY)
    // =========================================================================

    /// <summary>
    /// Unpair sends Go's UNPAIR_GAIA_PAIRING request and deletes the saved session.
    /// </summary>
    [TestMethod]
    public async Task Unpair_SendsUnpairGaia_AndDeletesSessionFile()
    {
        await using Rig rig = CreateRig();
        rig.Store.Save(rig.Auth);

        await rig.Client.UnpairAsync(CancellationToken.None).WaitAsync(HangGuard);

        RecordedRequest sent = rig.Requests("/SendMessage").Single();
        Assert.AreEqual(ActionType.UnpairGaiaPairing, ActionOf(sent));
        Assert.IsFalse(File.Exists(rig.SessionPath));
    }

    /// <summary>
    /// Pairing fetches the config, shows the emoji, finishes, stops and awaits the pairing long poll, saves the
    /// session, raises <see cref="PairSuccessful"/> and reconnects logged in (Go: DoGaiaPairing, then Reconnect).
    /// </summary>
    [TestMethod]
    public async Task Pair_FinishesSavesAndReconnects()
    {
        JsonElement sign_in = Golden.Load("signin");
        AuthData auth       = AuthDataGoldenTests.BuildGolden();
        auth.DestPhoneId    = 0;
        await using Rig rig = CreateRig(auth);

        byte[] config_body  = Encoding.UTF8.GetBytes(sign_in.GetProperty("config").GetProperty("bodyText").GetString()!);
        byte[] sign_in_body = Encoding.UTF8.GetBytes(sign_in.GetProperty("response").GetProperty("bodyText").GetString()!);
        byte[] server_init  = GaiaPairingTests.ServerInit(1, 1);
        byte[] finished     = new GaiaPairingResponseContainer().ToByteArray();
        rig.Respond = req =>
        {
            if (req.Url.EndsWith("/web/config", StringComparison.Ordinal))
            {
                return FakeHttpHandler.Response(HttpStatusCode.OK, PbLiteMime, config_body);
            }

            if (req.Url.EndsWith("/SignInGaia", StringComparison.Ordinal))
            {
                return FakeHttpHandler.Response(HttpStatusCode.OK, PbLiteMime, sign_in_body);
            }

            if (req.Url.EndsWith("/SendMessage", StringComparison.Ordinal))
            {
                ActionType action = ActionOf(req);
                if (action == ActionType.CreateGaiaPairingClientInit)
                {
                    GaiaPairingTests.Deliver(rig.Client.Rpc, req, server_init);
                }
                else if (action == ActionType.CreateGaiaPairingClientFinished)
                {
                    GaiaPairingTests.Deliver(rig.Client.Rpc, req, finished);
                }
            }

            return null;
        };

        string? shown = null;
        await rig.Client.PairAsync(e => shown = e, CancellationToken.None).WaitAsync(HangGuard);

        Assert.IsNotNull(shown, "emoji shown");
        Assert.IsTrue(File.Exists(rig.SessionPath), "saved right after pairing");
        Assert.AreEqual(rig.Auth.PairingId, rig.Store.Load()!.PairingId);
        lock (rig.Events)
        {
            Assert.AreEqual("mixed-case-src/999", rig.Events.OfType<PairSuccessful>().Single().PhoneId);
        }

        // Pairing Poll, Then The Logged-In Poll
        WaitUntil(() => rig.Requests("/ReceiveMessages").Length == 2, "reconnected logged in");
        WaitUntil(() => rig.Client.IsConnected, "connected");
        Assert.IsTrue(rig.Client.IsLoggedIn);
    }
}
