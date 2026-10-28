using System.Net;
using System.Text;
using System.Text.Json;
using GMessages.Protocol.Auth;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.LongPoll;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using GMessages.Protocol.Tests.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Protocol.Tests.LongPoll;

/// <summary>
/// Checks the requests and the reconnect timing of <see cref="LongPollLoop"/>.
/// </summary>
/// <remarks>
/// The two request tests compare with <c>longpoll-request.json</c> after copying Go's random request and session IDs.
/// The timing tests are C#-only: the behavior was read from <c>doLongPoll</c> (401/403 fatal, other errors retried
/// after <c>(errorCount + 1) × 5</c> s, recovered event, catch-up after a 1 minute gap, acks every 5 s) and is
/// driven by a fake clock, not copied from a golden file.
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/longpoll.go"/>.
/// </remarks>
[TestClass]
public class LongPollLoopTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private sealed class Rig
    {
        public required LongPollLoop Loop { get; init; }

        public required RpcSession Rpc { get; init; }

        public required FakeHttpHandler Handler { get; init; }

        public required SignalingTimeProvider Time { get; init; }

        public List<object> Events { get; } = [];

        // Open Quiet Streams Handed Out After The Script, In Order
        public List<OpenStream> Streams { get; } = [];

        // ReceiveMessages Answers In Order; After The Script, An Open Quiet Stream
        public Queue<Func<HttpResponseMessage>> Script { get; } = new();

        public int Count<T>() { lock (Events) { return Events.Count(e => e is T); } }

        public RecordedRequest[] Requests(string suffix) { lock (Handler.Requests) { return [.. Handler.Requests.Where(r => r.Url.EndsWith(suffix, StringComparison.Ordinal))]; } }
    }

    private static Rig CreateRig()
    {
        AuthData auth      = AuthDataGoldenTests.BuildGolden();
        var time           = new SignalingTimeProvider();
        auth.TachyonExpiry = time.GetUtcNow() + TimeSpan.FromDays(1);

        var handler   = new FakeHttpHandler { Time = time };
        var http      = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
        var rpc       = new RpcSession(http, auth, GmEndpoints.Production, time, NullLogger.Instance);
        var refresher = new TokenRefresher(http, auth, GmEndpoints.Production, time, NullLogger.Instance);
        var loop      = new LongPollLoop(http, rpc, auth, GmEndpoints.Production, refresher, new UpdateDispatcher(rpc, NullLogger.Instance), time, NullLogger.Instance);
        var rig       = new Rig { Loop = loop, Rpc = rpc, Handler = handler, Time = time };

        loop.EventRaised += e => { lock (rig.Events) { rig.Events.Add(e); } };
        handler.Respond = (_, req) =>
        {
            if (!req.Url.EndsWith("/ReceiveMessages", StringComparison.Ordinal))
            {
                return FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", Encoding.UTF8.GetBytes("[]"));
            }

            if (rig.Script.TryDequeue(out Func<HttpResponseMessage>? next))
            {
                return next();
            }

            var stream = new OpenStream("[["u8.ToArray());
            lock (rig.Streams)
            {
                rig.Streams.Add(stream);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        };
        return rig;
    }

    private static HttpResponseMessage Ended() => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", "[[]]"u8.ToArray());

    private static HttpResponseMessage Status(int code) => FakeHttpHandler.Response((HttpStatusCode)code);

    private static void WaitUntil(Func<bool> condition, string what) => Assert.IsTrue(SpinWait.SpinUntil(condition, HangGuard), what);

    // The Stream That Ended At Once Is Followed By A 5 s Pause (Logged In, The Ack Timer Is The Other 5 s Timer)
    private static void SkipShortStreamPause(Rig rig)
    {
        WaitUntil(() => rig.Time.CountTimers(LongPollLoop.MinStreamLifetime) == 2, "5 s pause after a short stream");
        rig.Time.Advance(LongPollLoop.MinStreamLifetime);
    }

    private static ActionType ActionOf(RecordedRequest req) =>
        OutgoingRPCData.Parser.ParseFrom(PbLiteSerializer.Deserialize<OutgoingRPCMessage>(req.Body).Data.MessageData).Action;

    // =========================================================================
    // REQUESTS (GOLDEN)
    // =========================================================================

    /// <summary>
    /// The ReceiveMessages request matches Go's once the random listen request ID is copied.
    /// </summary>
    [TestMethod]
    public async Task ReceiveMessagesRequest_MatchesGo()
    {
        JsonElement go = Golden.Load("longpoll-request").GetProperty("receiveMessages");
        Rig rig        = CreateRig();
        rig.Script.Enqueue(() =>
        {
            rig.Loop.Stop();
            return Ended();
        });

        await rig.Loop.RunAsync(false, null, CancellationToken.None).WaitAsync(HangGuard);

        RecordedRequest sent = rig.Requests("/ReceiveMessages").Single();
        var go_req = PbLiteSerializer.Deserialize<ReceiveMessagesRequest>(Encoding.UTF8.GetBytes(go.GetProperty("bodyText").GetString()!));
        var cs_req = PbLiteSerializer.Deserialize<ReceiveMessagesRequest>(sent.Body);
        cs_req.Auth.RequestID = go_req.Auth.RequestID;
        Assert.AreEqual(go.GetProperty("bodyText").GetString(), Encoding.UTF8.GetString(PbLiteSerializer.Serialize(cs_req)));
        Assert.AreEqual(go.GetProperty("url").GetString(), sent.Url);
    }

    /// <summary>
    /// SetActiveSession sends Go's GET_UPDATES request once the random session ID is copied.
    /// </summary>
    [TestMethod]
    public async Task GetUpdatesRequest_MatchesGo()
    {
        JsonElement go = Golden.Load("longpoll-request").GetProperty("setActiveSession");
        string go_id   = go.GetProperty("sessionId").GetString()!;
        Rig rig        = CreateRig();

        await rig.Loop.SetActiveSessionAsync(CancellationToken.None);

        RecordedRequest sent = rig.Requests("/SendMessage").Single();
        var cs_req           = PbLiteSerializer.Deserialize<OutgoingRPCMessage>(sent.Body);
        var inner            = OutgoingRPCData.Parser.ParseFrom(cs_req.Data.MessageData);
        Assert.AreEqual(rig.Rpc.SessionId, inner.RequestID, "request ID is the new session ID");
        Assert.AreEqual(rig.Rpc.SessionId, inner.SessionID);

        // Normalize The Random Session ID
        inner.RequestID           = go_id;
        inner.SessionID           = go_id;
        cs_req.Data.MessageData   = Google.Protobuf.ByteString.CopyFrom(Google.Protobuf.MessageExtensions.ToByteArray(inner));
        cs_req.Data.RequestID     = go_id;
        cs_req.Auth.RequestID     = go_id;
        Assert.AreEqual(go.GetProperty("bodyText").GetString(), Encoding.UTF8.GetString(PbLiteSerializer.Serialize(cs_req)));
        Assert.AreEqual(go.GetProperty("url").GetString(), sent.Url);
    }

    // =========================================================================
    // TIMING (C#-ONLY)
    // =========================================================================

    /// <summary>
    /// A 401 on the long poll raises <see cref="ListenFatalError"/> and ends the loop.
    /// </summary>
    [TestMethod]
    public async Task Status401_RaisesFatalAndStops()
    {
        Rig rig = CreateRig();
        rig.Script.Enqueue(() => Status(401));

        await rig.Loop.RunAsync(true, null, CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual(1, rig.Count<ListenFatalError>());
        Assert.AreEqual(0, rig.Count<ListenRecovered>());
        Assert.AreEqual(1, rig.Requests("/ReceiveMessages").Length);
    }

    /// <summary>
    /// When not logged in (pairing), a 401 has no event to carry it, so the run throws it instead.
    /// </summary>
    [TestMethod]
    public async Task Status401_NotLoggedIn_ThrowsIt()
    {
        Rig rig = CreateRig();
        rig.Script.Enqueue(() => Status(401));

        var ex = await Assert.ThrowsExactlyAsync<GmRequestException>(() => rig.Loop.RunAsync(false, null, CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual(401, ex.StatusCode);
        Assert.AreEqual(0, rig.Count<ListenFatalError>());
    }

    /// <summary>
    /// A run that ends after a newer run started leaves the newer one stoppable.
    /// </summary>
    [TestMethod]
    public async Task OldRunEnding_KeepsNewerRunStoppable()
    {
        Rig rig        = CreateRig();
        using var cts  = new CancellationTokenSource();
        Task old_run   = rig.Loop.RunAsync(false, null, cts.Token);
        WaitUntil(() => { lock (rig.Streams) { return rig.Streams.Count == 1; } }, "old run connected");

        Task new_run = rig.Loop.RunAsync(false, null, CancellationToken.None);
        await cts.CancelAsync();
        await old_run.WaitAsync(HangGuard);

        rig.Loop.Stop();
        await new_run.WaitAsync(HangGuard);
    }

    /// <summary>
    /// After a working connection drops, a 500 raises <see cref="ListenTemporaryError"/>, the retry comes exactly
    /// 10 s later, and the next success raises <see cref="ListenRecovered"/> again.
    /// </summary>
    [TestMethod]
    public async Task Status500_RaisesTemporary_RetriesAfter10s_ThenRecovered()
    {
        Rig rig = CreateRig();
        rig.Script.Enqueue(Ended);
        rig.Script.Enqueue(() => Status(500));
        using var cts = new CancellationTokenSource();
        Task run      = rig.Loop.RunAsync(true, null, cts.Token);
        SkipShortStreamPause(rig);

        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromSeconds(10)) == 1, "10 s retry wait");
        Assert.AreEqual(1, rig.Count<ListenTemporaryError>());
        Assert.AreEqual(1, rig.Count<ListenRecovered>(), "first connect counts as recovered, as in Go");

        rig.Time.Advance(TimeSpan.FromSeconds(9));
        Assert.AreEqual(2, rig.Requests("/ReceiveMessages").Length, "no retry before 10 s");
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        WaitUntil(() => rig.Requests("/ReceiveMessages").Length == 3 && rig.Count<ListenRecovered>() == 2, "retry and recovered");

        await cts.CancelAsync();
        await run.WaitAsync(HangGuard);
    }

    /// <summary>
    /// Reconnecting after more than a minute away sends GET_UPDATES with the session ID; a short gap does not.
    /// </summary>
    [TestMethod]
    public async Task DisconnectedOver1Minute_SendsGetUpdatesOnReconnect()
    {
        Rig rig = CreateRig();
        rig.Script.Enqueue(Ended);
        int[] sleeps = [10, 15, 20, 25];
        foreach (int _ in sleeps)
        {
            rig.Script.Enqueue(() => Status(500));
        }

        rig.Rpc.ResetSessionId();
        using var cts = new CancellationTokenSource();
        Task run      = rig.Loop.RunAsync(true, null, cts.Token);
        SkipShortStreamPause(rig);

        // 70 s Of Failures (75 s Away With The Pause)
        foreach (int sleep in sleeps)
        {
            WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromSeconds(sleep)) == 1, $"{sleep} s wait");
            Assert.IsFalse(rig.Requests("/SendMessage").Any(r => ActionOf(r) == ActionType.GetUpdates), "no catch-up yet");
            rig.Time.Advance(TimeSpan.FromSeconds(sleep));
        }

        WaitUntil(() => rig.Requests("/SendMessage").Any(r => ActionOf(r) == ActionType.GetUpdates), "catch-up GET_UPDATES");
        RecordedRequest get_updates = rig.Requests("/SendMessage").First(r => ActionOf(r) == ActionType.GetUpdates);
        var outgoing = PbLiteSerializer.Deserialize<OutgoingRPCMessage>(get_updates.Body);
        Assert.AreEqual(rig.Rpc.SessionId, outgoing.Data.RequestID);
        Assert.AreEqual(0, outgoing.TTL, "TTL omitted");
        Assert.AreEqual(1, rig.Count<NoDataReceived>());

        await cts.CancelAsync();
        await run.WaitAsync(HangGuard);
    }

    /// <summary>
    /// Reconnecting after a short gap (10 s) sends no catch-up GET_UPDATES.
    /// </summary>
    [TestMethod]
    public async Task ShortGap_NoCatchUp()
    {
        Rig rig = CreateRig();
        rig.Script.Enqueue(Ended);
        rig.Script.Enqueue(() => Status(500));
        using var cts = new CancellationTokenSource();
        Task run      = rig.Loop.RunAsync(true, null, cts.Token);
        SkipShortStreamPause(rig);

        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromSeconds(10)) == 1, "10 s retry wait");
        rig.Time.Advance(TimeSpan.FromSeconds(10));

        // The Catch-Up Would Be Started Before Reading, So Once The New Stream Is Being Read, It Would Have Shown
        WaitUntil(() => { lock (rig.Streams) { return rig.Streams.Count == 1; } }, "reconnected");
        await rig.Streams[0].Waiting.WaitAsync(HangGuard);
        Assert.AreEqual(0, rig.Count<NoDataReceived>());
        Assert.IsFalse(rig.Requests("/SendMessage").Any(r => ActionOf(r) == ActionType.GetUpdates));

        await cts.CancelAsync();
        await run.WaitAsync(HangGuard);
    }

    /// <summary>
    /// The retry wait is Go's <c>(errorCount + 1) × 5</c> seconds, capped at 5 minutes (a C#-only limit).
    /// </summary>
    [TestMethod]
    public void RetryDelay_GrowsLikeGo_CappedAt5Minutes()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), LongPollLoop.RetryDelay(1));
        Assert.AreEqual(TimeSpan.FromSeconds(15), LongPollLoop.RetryDelay(2));
        Assert.AreEqual(TimeSpan.FromSeconds(295), LongPollLoop.RetryDelay(58));
        Assert.AreEqual(LongPollLoop.MaxRetryDelay, LongPollLoop.RetryDelay(59));
        Assert.AreEqual(LongPollLoop.MaxRetryDelay, LongPollLoop.RetryDelay(100_000));
    }

    /// <summary>
    /// A stream that ends at once is followed by a 5 second pause before the next open (C#-only; Go reopens at
    /// once), so a server that keeps closing right away can't spin the loop.
    /// </summary>
    [TestMethod]
    public async Task ShortStream_PausesBeforeReopening()
    {
        Rig rig = CreateRig();
        rig.Script.Enqueue(Ended);
        using var cts = new CancellationTokenSource();
        Task run      = rig.Loop.RunAsync(false, null, cts.Token);

        WaitUntil(() => rig.Time.CountTimers(LongPollLoop.MinStreamLifetime) == 1, "5 s pause");
        rig.Time.Advance(LongPollLoop.MinStreamLifetime - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, rig.Requests("/ReceiveMessages").Length, "no reopen inside the pause");
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        WaitUntil(() => rig.Requests("/ReceiveMessages").Length == 2, "reopened after the pause");

        await cts.CancelAsync();
        await run.WaitAsync(HangGuard);
    }

    /// <summary>
    /// An open that gets no response headers within a minute (a dead socket) gives up and retries after the normal
    /// backoff (C#-only; Go waits on the HTTP client's timeout).
    /// </summary>
    [TestMethod]
    public async Task OpenStuck_GivesUpAfter1Minute_ThenRetries()
    {
        Rig rig          = CreateRig();
        int receives     = 0;
        rig.Handler.Hang = req => req.Url.EndsWith("/ReceiveMessages", StringComparison.Ordinal) && Interlocked.Increment(ref receives) == 1;
        using var cts    = new CancellationTokenSource();
        Task run         = rig.Loop.RunAsync(false, null, cts.Token);

        WaitUntil(() => rig.Time.CountTimers(LongPollLoop.ReadIdleTimeout) == 1 && rig.Requests("/ReceiveMessages").Length == 1, "open waiting for headers");
        rig.Time.Advance(LongPollLoop.ReadIdleTimeout);
        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromSeconds(15)) == 1, "15 s retry wait");
        rig.Time.Advance(TimeSpan.FromSeconds(15));
        WaitUntil(() => rig.Requests("/ReceiveMessages").Length == 2 && rig.Loop.IsConnected, "reopened");

        await cts.CancelAsync();
        await run.WaitAsync(HangGuard);
    }

    /// <summary>
    /// A reconnect request (resume, network back) interrupts an open stuck on a dead socket and starts over at once,
    /// with no backoff and no temporary error.
    /// </summary>
    [TestMethod]
    public async Task ReconnectDuringOpen_StartsOverAtOnce()
    {
        Rig rig          = CreateRig();
        int receives     = 0;
        rig.Handler.Hang = req => req.Url.EndsWith("/ReceiveMessages", StringComparison.Ordinal) && Interlocked.Increment(ref receives) == 1;
        using var cts    = new CancellationTokenSource();
        Task run         = rig.Loop.RunAsync(true, null, cts.Token);
        WaitUntil(() => rig.Requests("/ReceiveMessages").Length == 1, "open in flight");

        rig.Loop.RequestReconnect();

        WaitUntil(() => rig.Requests("/ReceiveMessages").Length == 2 && rig.Loop.IsConnected, "reopened");
        Assert.AreEqual(0, rig.Count<ListenTemporaryError>());
        Assert.AreEqual(1, rig.Count<ListenRecovered>(), "only the first connect");

        await cts.CancelAsync();
        await run.WaitAsync(HangGuard);
    }

    /// <summary>
    /// Queued acks go out on the 5 second timer.
    /// </summary>
    [TestMethod]
    public async Task Acks_FlushEvery5Seconds()
    {
        Rig rig = CreateRig();
        using var cts = new CancellationTokenSource();
        Task run      = rig.Loop.RunAsync(true, null, cts.Token);
        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromSeconds(5)) >= 1 && rig.Requests("/ReceiveMessages").Length == 1, "ack timer and connection");

        rig.Rpc.QueueAck("ack-1");
        rig.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.AreEqual(0, rig.Requests("/AckMessages").Length);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        WaitUntil(() => rig.Requests("/AckMessages").Length == 1, "ack request");

        await cts.CancelAsync();
        await run.WaitAsync(HangGuard);
    }
}
