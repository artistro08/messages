using System.Net;
using System.Text;
using System.Text.Json;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.LongPoll;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
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
    /// A session that holds the main sign-in cookie still sends no cookie rotation request when it connects.
    /// </summary>
    [TestMethod]
    public async Task Connect_SendsNoRotateCookies()
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        auth.Cookies.Set(new Dictionary<string, string>(auth.Cookies.Snapshot()) { ["__Secure-1PSID"] = "fake-1psid" });
        await using Rig rig = CreateRig(auth);
        await rig.Client.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        WaitUntil(() => rig.Client.IsConnected && rig.Requests("/ReceiveMessages").Length > 0, "long poll opened");

        Assert.AreEqual(0, rig.Requests("/RotateCookies").Length);
        Assert.IsFalse(rig.Handler.Requests.Any(r => r.Url.Contains("accounts.google.com", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A session with a browser and cookies but no Tachyon token fails with "No auth token." before sending anything.
    /// </summary>
    [TestMethod]
    public async Task Connect_NoToken_Throws()
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        auth.Tachyon  = TachyonToken.None;
        await using Rig rig = CreateRig(auth);

        InvalidOperationException ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rig.Client.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual("No auth token.", ex.Message);
        Assert.AreEqual(0, rig.Handler.Requests.Count);
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

    /// <summary>
    /// Two connects at once start exactly one long poll run (one ack timer). The second may make that run reconnect
    /// once, depending on whether it lands before or after the first stream opens.
    /// </summary>
    [TestMethod]
    public async Task Connect_Concurrent_StartsOneLongPoll()
    {
        await using Rig rig = CreateRig();

        await Task.WhenAll(rig.Client.ConnectAsync(CancellationToken.None), rig.Client.ConnectAsync(CancellationToken.None)).WaitAsync(HangGuard);
        WaitUntil(() => rig.Client.IsConnected, "connected");

        Assert.AreEqual(1, rig.Time.CountTimers(LongPollLoop.AckInterval), "one run");
        Assert.IsTrue(rig.Requests("/ReceiveMessages").Length <= 2);
    }

    /// <summary>
    /// Connect while the long poll is running makes that run reconnect once, right away, instead of starting a
    /// second run; the token is not refreshed twice.
    /// </summary>
    [TestMethod]
    public async Task Connect_WhileRunning_ReconnectsOnce()
    {
        await using Rig rig = CreateRig();

        await rig.Client.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        WaitUntil(() => rig.Client.IsConnected && rig.Requests("/ReceiveMessages").Length == 1, "first connect");

        await rig.Client.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        WaitUntil(() => rig.Requests("/ReceiveMessages").Length == 2, "reconnected");
        WaitUntil(() => rig.Client.IsConnected, "connected again");

        Assert.AreEqual(1, rig.Time.CountTimers(LongPollLoop.AckInterval), "still one run");
        Assert.AreEqual(0, rig.Requests("/RegisterRefresh").Length);
        lock (rig.Events)
        {
            Assert.AreEqual(0, rig.Events.OfType<AuthTokenRefreshed>().Count());
        }
    }

    /// <summary>
    /// A request that waits for the phone fails at once with <see cref="ConnectionClosedException"/> when not
    /// connected, instead of waiting 60 seconds and blaming the phone; nothing is sent (C#-only).
    /// </summary>
    [TestMethod]
    public async Task SendWhileDisconnected_FailsFastWithConnectionClosed()
    {
        await using Rig rig = CreateRig();

        await Assert.ThrowsExactlyAsync<ConnectionClosedException>(() => rig.Client.Rpc.SendAsync(new SendParams(ActionType.ListConversations), CancellationToken.None).WaitAsync(HangGuard));
        await Assert.ThrowsExactlyAsync<ConnectionClosedException>(() => rig.Client.Rpc.SendAsyncNoWait(new SendParams(ActionType.ListConversations), CancellationToken.None).WaitAsync(HangGuard));

        Assert.IsEmpty(rig.Requests("/SendMessage"));
        Assert.AreEqual(0, rig.Time.CountTimers(RpcSession.ResponseHardTimeout));
    }

    /// <summary>
    /// Connect, disconnect, connect again opens a second long poll; after dispose, connect throws.
    /// </summary>
    [TestMethod]
    public async Task Connect_Disconnect_Connect_Reconnects()
    {
        await using Rig rig = CreateRig();

        await rig.Client.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        WaitUntil(() => rig.Client.IsConnected, "first connect");
        await rig.Client.DisconnectAsync().WaitAsync(HangGuard);
        Assert.IsFalse(rig.Client.IsConnected);

        await rig.Client.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        WaitUntil(() => rig.Client.IsConnected, "second connect");
        Assert.AreEqual(2, rig.Requests("/ReceiveMessages").Length);

        await rig.Client.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => rig.Client.ConnectAsync(CancellationToken.None));
    }

    // =========================================================================
    // SESSION SAVING (C#-ONLY)
    // =========================================================================

    /// <summary>
    /// The first cookie change saves right away; changes inside the next 5 s are saved once, at the end of that
    /// window; a change right after that save waits for a new window and is flushed on dispose.
    /// </summary>
    [TestMethod]
    public async Task CookieChange_SavesSessionDebounced()
    {
        await using Rig rig = CreateRig();

        rig.Auth.Cookies.UpdateFrom(FakeHttpHandler.Response(HttpStatusCode.OK, setCookies: "SID=new-sid; Path=/"));
        Assert.AreEqual("new-sid", rig.Store.Load()!.Cookies.Snapshot()["SID"], "saved right away");

        rig.Auth.Cookies.UpdateFrom(FakeHttpHandler.Response(HttpStatusCode.OK, setCookies: "SID=newer-sid; Path=/"));
        rig.Auth.Cookies.UpdateFrom(FakeHttpHandler.Response(HttpStatusCode.OK, setCookies: "SID=newest-sid; Path=/"));
        Assert.AreEqual(1, rig.Time.CountTimers(GmClient.SaveDebounce), "one window timer");
        rig.Time.Advance(GmClient.SaveDebounce - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual("new-sid", rig.Store.Load()!.Cookies.Snapshot()["SID"], "not saved again inside the window");

        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual("newest-sid", rig.Store.Load()!.Cookies.Snapshot()["SID"], "saved at the end of the window");
        Assert.AreEqual(2, rig.Client.SaveCount);

        // A Change Right After That Save Waits For A New Window; Dispose Flushes It
        rig.Auth.Cookies.UpdateFrom(FakeHttpHandler.Response(HttpStatusCode.OK, setCookies: "SID=final-sid; Path=/"));
        Assert.AreEqual(2, rig.Client.SaveCount);
        await rig.Client.DisposeAsync();
        Assert.AreEqual("final-sid", rig.Store.Load()!.Cookies.Snapshot()["SID"]);
    }

    /// <summary>
    /// Dispose still writes a pending debounced save when the background ack flush fails with a non-cancel error
    /// (the aborted socket) while the long poll stops, and does not throw.
    /// </summary>
    [TestMethod]
    public async Task Dispose_BackgroundFlushThrowsDuringStop_StillWritesPendingSave()
    {
        await using Rig rig = CreateRig();
        rig.Respond = req => req.Url.EndsWith("/AckMessages", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailOnCancelStream()) }
            : null;

        // Connect And Let postConnect Finish (2 s, 1 s, GET_UPDATES)
        await rig.Client.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromSeconds(2)) > 0, "2 s post-connect wait");
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromSeconds(1)) > 0, "1 s post-connect wait");
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        WaitUntil(() => rig.Requests("/SendMessage").Length > 0, "GET_UPDATES");

        // The Ack Timer's Flush Hangs Until Canceled, Then Fails With An IOException
        rig.Client.Rpc.QueueAck("ack-1");
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        WaitUntil(() => rig.Requests("/AckMessages").Length == 1, "ack flush in flight");

        // A Pending Debounced Save
        rig.Auth.Cookies.UpdateFrom(FakeHttpHandler.Response(HttpStatusCode.OK, setCookies: "SID=first-sid; Path=/"));
        rig.Auth.Cookies.UpdateFrom(FakeHttpHandler.Response(HttpStatusCode.OK, setCookies: "SID=pending-sid; Path=/"));
        Assert.AreEqual(1, rig.Client.SaveCount);

        await rig.Client.DisposeAsync().AsTask().WaitAsync(HangGuard);

        Assert.AreEqual("pending-sid", rig.Store.Load()!.Cookies.Snapshot()["SID"], "pending save written");
        Assert.IsFalse(rig.Client.IsConnected);
    }

    // A Body That Waits Until The Read Is Canceled, Then Fails Like An Aborted Socket
    private sealed class FailOnCancelStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            throw new IOException("The socket was aborted.");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A failed save raises <see cref="SessionSaveFailed"/> with only the exception's type name, and is not thrown.
    /// </summary>
    [TestMethod]
    public async Task SaveFails_RaisesSessionSaveFailed()
    {
        await using Rig rig          = CreateRig();
        rig.Client.OnWritingForTests = () => throw new IOException("C:\\secret\\path is full");

        rig.Auth.Cookies.UpdateFrom(FakeHttpHandler.Response(HttpStatusCode.OK, setCookies: "SID=new-sid; Path=/"));

        Assert.AreEqual(0, rig.Client.SaveCount);
        lock (rig.Events)
        {
            Assert.AreEqual("IOException", rig.Events.OfType<SessionSaveFailed>().Single().ErrorType);
        }
    }

    /// <summary>
    /// A change that arrives while a save is being written leads to a second save at the end of the window.
    /// </summary>
    [TestMethod]
    public async Task ChangeDuringInFlightSave_SavesAgain()
    {
        await using Rig rig = CreateRig();
        bool changed_once   = false;
        rig.Client.OnWritingForTests = () =>
        {
            if (!changed_once)
            {
                changed_once = true;
                rig.Auth.Cookies.UpdateFrom(FakeHttpHandler.Response(HttpStatusCode.OK, setCookies: "SID=during-sid; Path=/"));
            }
        };

        rig.Auth.Cookies.UpdateFrom(FakeHttpHandler.Response(HttpStatusCode.OK, setCookies: "SID=first-sid; Path=/"));
        Assert.AreEqual(1, rig.Client.SaveCount, "first save");
        Assert.AreEqual(1, rig.Time.CountTimers(GmClient.SaveDebounce), "the change during the write opened a window");

        rig.Time.Advance(GmClient.SaveDebounce);
        Assert.AreEqual(2, rig.Client.SaveCount, "second save");
        Assert.AreEqual("during-sid", rig.Store.Load()!.Cookies.Snapshot()["SID"]);
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
        Assert.IsFalse(rig.Client.IsLoggedIn, "pairing cleared in memory");

        // The Client Is Finished
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => rig.Client.ConnectAsync(CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => rig.Client.PairAsync(_ => { }, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => rig.Client.UnpairAsync(CancellationToken.None));

        // No Later Change Brings The File Back
        rig.Auth.Cookies.UpdateFrom(FakeHttpHandler.Response(HttpStatusCode.OK, setCookies: "SID=late-sid; Path=/"));
        rig.Time.Advance(GmClient.SaveDebounce);
        Assert.IsFalse(File.Exists(rig.SessionPath));
    }

    /// <summary>
    /// When the server refuses the unpair request, the local session is still removed and the error is rethrown.
    /// </summary>
    [TestMethod]
    public async Task Unpair_ServerFails_StillDeletesSessionAndRethrows()
    {
        await using Rig rig = CreateRig();
        rig.Store.Save(rig.Auth);
        rig.Respond = req => req.Url.EndsWith("/SendMessage", StringComparison.Ordinal) ? FakeHttpHandler.Response(HttpStatusCode.Forbidden) : null;

        await Assert.ThrowsExactlyAsync<GmRequestException>(() => rig.Client.UnpairAsync(CancellationToken.None).WaitAsync(HangGuard));

        Assert.IsFalse(File.Exists(rig.SessionPath));
        Assert.IsFalse(rig.Client.IsLoggedIn);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => rig.Client.ConnectAsync(CancellationToken.None));
    }

    /// <summary>
    /// A session file that can't be deleted (locked) does not hide the server's error or skip clearing the pairing.
    /// </summary>
    [TestMethod]
    public async Task Unpair_DeleteFails_StillRethrowsServerErrorAndClearsPairing()
    {
        await using Rig rig = CreateRig();
        rig.Store.Save(rig.Auth);
        rig.Respond = req => req.Url.EndsWith("/SendMessage", StringComparison.Ordinal) ? FakeHttpHandler.Response(HttpStatusCode.Forbidden) : null;

        using (new FileStream(rig.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsExactlyAsync<GmRequestException>(() => rig.Client.UnpairAsync(CancellationToken.None).WaitAsync(HangGuard));
        }

        Assert.IsFalse(rig.Client.IsLoggedIn, "pairing cleared");
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

    /// <summary>
    /// A wrong emoji fails the pairing: the half-set pairing is cleared, nothing is saved (not even the cookie change
    /// that came with the sign-in), and no long poll is left running.
    /// </summary>
    [TestMethod]
    public async Task Pair_WrongEmoji_ClearsPairingAndSavesNothing()
    {
        JsonElement sign_in = Golden.Load("signin");
        AuthData auth       = AuthDataGoldenTests.BuildGolden();
        auth.DestPhoneId    = 0;
        await using Rig rig = CreateRig(auth);

        byte[] config_body  = Encoding.UTF8.GetBytes(sign_in.GetProperty("config").GetProperty("bodyText").GetString()!);
        byte[] sign_in_body = Encoding.UTF8.GetBytes(sign_in.GetProperty("response").GetProperty("bodyText").GetString()!);
        byte[] server_init  = GaiaPairingTests.ServerInit(1, 1);
        byte[] wrong_emoji  = new GaiaPairingResponseContainer { FinishErrorType = 1, FinishErrorCode = GaiaPairingErrorCode.WrongVerificationCodeSelected }.ToByteArray();
        rig.Respond = req =>
        {
            if (req.Url.EndsWith("/web/config", StringComparison.Ordinal))
            {
                return FakeHttpHandler.Response(HttpStatusCode.OK, PbLiteMime, config_body);
            }

            if (req.Url.EndsWith("/SignInGaia", StringComparison.Ordinal))
            {
                return FakeHttpHandler.Response(HttpStatusCode.OK, PbLiteMime, sign_in_body, "SID=pairing-sid; Path=/");
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
                    GaiaPairingTests.Deliver(rig.Client.Rpc, req, wrong_emoji);
                }
            }

            return null;
        };

        var ex = await Assert.ThrowsExactlyAsync<PairingException>(() => rig.Client.PairAsync(_ => { }, CancellationToken.None).WaitAsync(HangGuard));
        Assert.AreEqual(PairingFailure.IncorrectEmoji, ex.Failure);

        rig.Time.Advance(GmClient.SaveDebounce);
        Assert.IsFalse(File.Exists(rig.SessionPath), "nothing saved");
        Assert.AreEqual(0, rig.Client.SaveCount);
        Assert.IsFalse(rig.Client.IsLoggedIn, "pairing cleared");
        Assert.IsFalse(rig.Client.IsConnected, "pairing poll stopped");
        Assert.AreEqual(1, rig.Requests("/ReceiveMessages").Length, "no reconnect");
        lock (rig.Events)
        {
            Assert.AreEqual(0, rig.Events.OfType<PairSuccessful>().Count());
        }
    }

    /// <summary>
    /// Disconnect during the emoji wait cancels the pairing at once (Go: the bridge cancels a login with
    /// Disconnect): PairAsync fails with a cancellation, the pairing is cleared and no long poll is left running.
    /// </summary>
    [TestMethod]
    public async Task Disconnect_DuringEmojiWait_CancelsPairing()
    {
        JsonElement sign_in = Golden.Load("signin");
        AuthData auth       = AuthDataGoldenTests.BuildGolden();
        auth.DestPhoneId    = 0;
        await using Rig rig = CreateRig(auth);

        byte[] config_body  = Encoding.UTF8.GetBytes(sign_in.GetProperty("config").GetProperty("bodyText").GetString()!);
        byte[] sign_in_body = Encoding.UTF8.GetBytes(sign_in.GetProperty("response").GetProperty("bodyText").GetString()!);
        byte[] server_init  = GaiaPairingTests.ServerInit(1, 1);
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

            // The Phone Answers The Init, Then Stays Quiet (The User Never Taps)
            if (req.Url.EndsWith("/SendMessage", StringComparison.Ordinal) && ActionOf(req) == ActionType.CreateGaiaPairingClientInit)
            {
                GaiaPairingTests.Deliver(rig.Client.Rpc, req, server_init);
            }

            return null;
        };

        bool shown = false;
        Task pair  = rig.Client.PairAsync(_ => shown = true, CancellationToken.None);
        WaitUntil(() => rig.Requests("/SendMessage").Any(r => ActionOf(r) == ActionType.CreateGaiaPairingClientFinished), "waiting for the tap");

        await rig.Client.DisconnectAsync().WaitAsync(HangGuard);

        await Assert.ThrowsAsync<OperationCanceledException>(() => pair.WaitAsync(HangGuard));
        Assert.IsTrue(shown);
        Assert.IsFalse(rig.Client.IsLoggedIn, "pairing cleared");
        Assert.IsFalse(rig.Client.IsConnected, "pairing poll stopped");
        Assert.AreEqual(1, rig.Requests("/ReceiveMessages").Length, "no reconnect");
        Assert.IsFalse(File.Exists(rig.SessionPath));
    }

    /// <summary>
    /// The pairing long poll dying during the emoji wait (its reconnect gets a 401) fails PairAsync at once with
    /// that 401, instead of waiting forever for an answer that can no longer arrive.
    /// </summary>
    [TestMethod]
    public async Task Pair_LongPollDiesDuringEmojiWait_FailsWithItsError()
    {
        AuthData auth       = AuthDataGoldenTests.BuildGolden();
        auth.DestPhoneId    = 0;
        await using Rig rig = CreateRig(auth);
        var first_stream    = new OpenStream("[["u8.ToArray());
        AnswerUntilEmoji(rig, receive: n => n == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(first_stream) }
            : FakeHttpHandler.Response(HttpStatusCode.Unauthorized));

        Task pair = rig.Client.PairAsync(_ => { }, CancellationToken.None);
        WaitUntil(() => rig.Requests("/SendMessage").Any(r => ActionOf(r) == ActionType.CreateGaiaPairingClientFinished), "waiting for the tap");

        first_stream.End();
        WaitUntil(() => rig.Time.CountTimers(LongPollLoop.MinStreamLifetime) == 1, "5 s pause after the short stream");
        rig.Time.Advance(LongPollLoop.MinStreamLifetime);

        var ex = await Assert.ThrowsExactlyAsync<GmRequestException>(() => pair.WaitAsync(HangGuard));
        Assert.AreEqual(401, ex.StatusCode);
        AssertPairingCleanedUp(rig);
    }

    /// <summary>
    /// With no answer for 300 seconds (the pairing messages' TTL), PairAsync fails with a timeout and sends a cancel.
    /// </summary>
    [TestMethod]
    public async Task Pair_NoAnswerIn300s_FailsWithTimeout()
    {
        AuthData auth       = AuthDataGoldenTests.BuildGolden();
        auth.DestPhoneId    = 0;
        await using Rig rig = CreateRig(auth);
        AnswerUntilEmoji(rig);

        Task pair = rig.Client.PairAsync(_ => { }, CancellationToken.None);
        WaitUntil(() => rig.Time.CountTimers(GaiaPairing.PairingTtl) > 0, "300 s ceiling");
        rig.Time.Advance(GaiaPairing.PairingTtl);

        var ex = await Assert.ThrowsExactlyAsync<PairingException>(() => pair.WaitAsync(HangGuard));
        Assert.AreEqual(PairingFailure.Timeout, ex.Failure);
        AssertPairingCleanedUp(rig);
    }

    // Config, Sign-In And The Init Answer; The Finish Gets No Answer (The User Never Taps)
    private static void AnswerUntilEmoji(Rig rig, Func<int, HttpResponseMessage>? receive = null)
    {
        JsonElement sign_in = Golden.Load("signin");
        byte[] config_body  = Encoding.UTF8.GetBytes(sign_in.GetProperty("config").GetProperty("bodyText").GetString()!);
        byte[] sign_in_body = Encoding.UTF8.GetBytes(sign_in.GetProperty("response").GetProperty("bodyText").GetString()!);
        byte[] server_init  = GaiaPairingTests.ServerInit(1, 1);
        int receives        = 0;
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

            if (req.Url.EndsWith("/ReceiveMessages", StringComparison.Ordinal) && receive is not null)
            {
                return receive(Interlocked.Increment(ref receives));
            }

            if (req.Url.EndsWith("/SendMessage", StringComparison.Ordinal) && ActionOf(req) == ActionType.CreateGaiaPairingClientInit)
            {
                GaiaPairingTests.Deliver(rig.Client.Rpc, req, server_init);
            }

            return null;
        };
    }

    // No Pairing Left Behind: Cleared, Not Connected, Nothing Saved, The Attempt Canceled On The Phone
    private static void AssertPairingCleanedUp(Rig rig)
    {
        Assert.IsFalse(rig.Client.IsLoggedIn, "pairing cleared");
        Assert.IsFalse(rig.Client.IsConnected, "pairing poll stopped");
        Assert.IsFalse(File.Exists(rig.SessionPath), "nothing saved");
        Assert.AreEqual(0, rig.Client.SaveCount);
        Assert.IsTrue(rig.Requests("/SendMessage").Any(r => ActionOf(r) == ActionType.CancelGaiaPairing), "cancel sent");
    }
}
