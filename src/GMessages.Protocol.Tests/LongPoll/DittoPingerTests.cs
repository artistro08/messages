using System.Net;
using System.Text;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.LongPoll;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using GMessages.Protocol.Tests.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Protocol.Tests.LongPoll;

/// <summary>
/// Checks the ping timing, recovery ladder and liveness events of <see cref="DittoPinger"/>.
/// </summary>
/// <remarks>
/// C#-only: the behavior was read from <c>dittoPinger</c> and <c>phoneLiveness</c> in longpoll.go (1 minute pings,
/// 10 s urgent pings, first miss alerts, later only 4 misses in a row do, re-arm after 2 misses, reconnect after 3,
/// recovery waits doubling from 1 minute) and is driven by a fake clock, not copied from a golden file. Every clock
/// advance happens only after the code under test has reached the expected state (a timer with the exact due time
/// exists, an answer was handled, a miss was counted), never on a guess.
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/longpoll.go"/>.
/// </remarks>
[TestClass]
public class DittoPingerTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private sealed class Rig
    {
        public required DittoPinger Pinger { get; init; }

        public required PhoneLiveness Liveness { get; init; }

        public required RpcSession Rpc { get; init; }

        public required FakeHttpHandler Handler { get; init; }

        public required SignalingTimeProvider Time { get; init; }

        public List<object> Events { get; } = [];

        public int SetActiveSessions;

        public int Reconnects;
    }

    private static Rig CreateRig(TimeSpan pingInterval)
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        var time      = new SignalingTimeProvider();
        var handler   = new FakeHttpHandler { Time = time };
        handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", Encoding.UTF8.GetBytes("[]"));

        var http     = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
        var rpc      = new RpcSession(http, auth, GmEndpoints.Production, time, NullLogger.Instance);
        var liveness = new PhoneLiveness(time);
        Rig? rig     = null;
        var pinger   = new DittoPinger(
            rpc,
            liveness,
            time,
            NullLogger.Instance,
            _ => { Interlocked.Increment(ref rig!.SetActiveSessions); return Task.CompletedTask; },
            () => { Interlocked.Increment(ref rig!.Reconnects); return Task.CompletedTask; })
        {
            PingInterval = pingInterval,
        };
        rig = new Rig { Pinger = pinger, Liveness = liveness, Rpc = rpc, Handler = handler, Time = time };
        pinger.EventRaised += e => { lock (rig.Events) { rig.Events.Add(e); } };

        // Like startLongPolling: No Data-Receive Check In The First 10 Minutes
        pinger.BumpDataReceiveCheck(TimeSpan.FromMinutes(10));
        return rig;
    }

    private static void WaitUntil(Func<bool> condition, string what) => Assert.IsTrue(SpinWait.SpinUntil(condition, HangGuard), what);

    private static OutgoingRPCData[] Pings(Rig rig)
    {
        lock (rig.Handler.Requests)
        {
            return [.. rig.Handler.Requests
                .Select(r => OutgoingRPCData.Parser.ParseFrom(PbLiteSerializer.Deserialize<OutgoingRPCMessage>(r.Body).Data.MessageData))
                .Where(d => d.Action == ActionType.NotifyDittoActivity)];
        }
    }

    private static string[] EventNames(Rig rig)
    {
        lock (rig.Events)
        {
            return [.. rig.Events.Select(e => e.GetType().Name)];
        }
    }

    private static int Count<T>(Rig rig)
    {
        lock (rig.Events)
        {
            return rig.Events.Count(e => e is T);
        }
    }

    private static bool Answer(Rig rig, string requestId) => rig.Rpc.TryDeliverResponse(new IncomingRpc
    {
        Raw     = new IncomingRPCMessage { ResponseID = "pong" },
        Message = new RPCMessageData { SessionID = requestId, Action = ActionType.NotifyDittoActivity, EncryptedData = ByteString.CopyFromUtf8("x") },
    });

    // Short-Circuits Once The Loop Is Waiting (Its Nth 1-Hour Wait Exists), Then Waits For The Nth Ping And Its 10 s Timer
    private static void UrgentPing(Rig rig, int loopWaits, int pings, int shortTimers)
    {
        WaitUntil(() => rig.Time.CountTimers(OneHour) == loopWaits, $"loop wait {loopWaits}");
        rig.Pinger.ShortCircuit();
        WaitUntil(() => Pings(rig).Length == pings && rig.Time.CountTimers(DittoPinger.ShortPingTimeout) == shortTimers, $"urgent ping {pings}");
    }

    // Advances, Then Waits Until The Ping Wait Has Counted Another Miss
    private static void Miss(Rig rig, TimeSpan advance, int timeouts)
    {
        rig.Time.Advance(advance);
        WaitUntil(() => rig.Liveness.TimeoutCount == timeouts, $"miss {timeouts}");
    }

    /// <summary>
    /// A ping goes out every minute; an answered one raises nothing.
    /// </summary>
    [TestMethod]
    public async Task Ping_EveryMinute_AnsweredRaisesNothing()
    {
        Rig rig = CreateRig(TimeSpan.FromMinutes(1));
        using var cts = new CancellationTokenSource();
        Task run      = rig.Pinger.RunAsync(cts.Token);

        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromMinutes(1)) == 1, "first wait");
        rig.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.AreEqual(0, Pings(rig).Length);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        WaitUntil(() => Pings(rig).Length == 1, "ping at 1 minute");

        // The Answer Must Be Fully Handled (Ping Timer Canceled) Before The Clock Moves
        Assert.IsTrue(Answer(rig, Pings(rig)[0].RequestID));
        WaitUntil(() => rig.Pinger.AnsweredPings == 1, "answer handled");
        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromMinutes(1)) == 3, "next loop wait");

        rig.Time.Advance(TimeSpan.FromMinutes(1));
        WaitUntil(() => Pings(rig).Length == 2, "ping at 2 minutes");
        Assert.AreEqual(0, rig.Liveness.TimeoutCount);
        Assert.AreEqual(0, EventNames(rig).Length);

        await cts.CancelAsync();
        await run.WaitAsync(HangGuard);
    }

    /// <summary>
    /// An urgent ping (from a slow request) that is not answered in 10 s raises <see cref="PhoneNotResponding"/>; the
    /// recovery ping a minute later is answered, which raises <see cref="PhoneRespondingAgain"/>.
    /// </summary>
    [TestMethod]
    public async Task PingUnanswered_RaisesPhoneNotResponding_ThenRespondingAgain()
    {
        Rig rig = CreateRig(OneHour);
        using var cts = new CancellationTokenSource();
        Task run      = rig.Pinger.RunAsync(cts.Token);

        // Urgent Ping, Unanswered
        UrgentPing(rig, 1, 1, 1);
        Miss(rig, DittoPinger.ShortPingTimeout, 1);
        Assert.AreEqual(1, Count<PhoneNotResponding>(rig), "first ping ever alerts at once");

        // Recovery Ping After 1 Minute, Answered
        WaitUntil(() => rig.Time.CountTimers(DittoPinger.InitialRecoveryInterval) == 1, "recovery wait");
        rig.Time.Advance(DittoPinger.InitialRecoveryInterval);
        WaitUntil(() => Pings(rig).Length == 2, "recovery ping");
        Assert.IsTrue(Answer(rig, Pings(rig)[1].RequestID));
        WaitUntil(() => rig.Pinger.AnsweredPings == 1, "answer handled");

        // Not Responding Was Sent, So Go Also Asks For Missed Updates
        WaitUntil(() => Count<NoDataReceived>(rig) == 1, "catch-up");
        CollectionAssert.AreEqual(new[] { nameof(PhoneNotResponding), nameof(PhoneRespondingAgain), nameof(NoDataReceived) }, EventNames(rig));

        await cts.CancelAsync();
        await run.WaitAsync(HangGuard);
    }

    /// <summary>
    /// The recovery ladder: 2 misses re-arm the session once, 3 misses reconnect.
    /// </summary>
    [TestMethod]
    public async Task Recovery_RearmsAfter2Misses_ReconnectsAfter3()
    {
        Rig rig = CreateRig(OneHour);
        using var cts = new CancellationTokenSource();
        Task run      = rig.Pinger.RunAsync(cts.Token);

        // Miss 1 At 10 s, Recovery Ping At 70 s (Next Recovery Wait 2 min), Miss 2 At 130 s
        UrgentPing(rig, 1, 1, 1);
        Miss(rig, DittoPinger.ShortPingTimeout, 1);
        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromMinutes(1)) == 1, "recovery wait 1 min");
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        WaitUntil(() => Pings(rig).Length == 2 && rig.Time.CountTimers(TimeSpan.FromMinutes(2)) == 1, "recovery ping 2");
        Miss(rig, TimeSpan.FromMinutes(1), 2);
        Assert.AreEqual(0, rig.SetActiveSessions);

        // 190 s: 2 Misses Re-Arm Once, Then Re-Ping (Next Recovery Wait 4 min); Miss 3 At 250 s
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        WaitUntil(() => Pings(rig).Length == 3 && rig.Time.CountTimers(TimeSpan.FromMinutes(4)) == 1, "recovery ping 3");
        Assert.AreEqual(1, rig.SetActiveSessions);
        Assert.AreEqual(0, rig.Reconnects);
        Miss(rig, TimeSpan.FromMinutes(1), 3);

        // 430 s: 3 Misses Reconnect, Which Ends Recovery
        rig.Time.Advance(TimeSpan.FromMinutes(3));
        WaitUntil(() => rig.Reconnects == 1 && !rig.Pinger.IsRecovering, "reconnect");
        Assert.AreEqual(1, rig.SetActiveSessions, "re-armed only once");
        Assert.AreEqual(3, Pings(rig).Length, "no ping after reconnecting");

        await cts.CancelAsync();
        await run.WaitAsync(HangGuard);
    }

    /// <summary>
    /// Once a ping has been answered, misses alert only at the 4th in a row (Go's <c>alertTimeoutCount</c>).
    /// </summary>
    [TestMethod]
    public async Task AfterFirstAnswer_AlertsOnlyAt4Misses()
    {
        Rig rig = CreateRig(OneHour);
        using var cts = new CancellationTokenSource();
        Task run      = rig.Pinger.RunAsync(cts.Token);

        // First Ping Answered
        UrgentPing(rig, 1, 1, 1);
        Assert.IsTrue(Answer(rig, Pings(rig)[0].RequestID));
        WaitUntil(() => rig.Pinger.AnsweredPings == 1, "answer handled");
        rig.Time.Advance(DittoPinger.MinPingInterval);

        // Misses 1-3: Urgent Ping, Then The Recovery Ladder Up To The Reconnect
        UrgentPing(rig, 2, 2, 2);
        Miss(rig, DittoPinger.ShortPingTimeout, 1);
        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromMinutes(1)) == 1, "recovery wait 1 min");
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        WaitUntil(() => Pings(rig).Length == 3 && rig.Time.CountTimers(TimeSpan.FromMinutes(2)) == 1, "recovery ping 3");
        Miss(rig, TimeSpan.FromMinutes(1), 2);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        WaitUntil(() => Pings(rig).Length == 4 && rig.Time.CountTimers(TimeSpan.FromMinutes(4)) == 1, "recovery ping 4");
        Miss(rig, TimeSpan.FromMinutes(1), 3);
        rig.Time.Advance(TimeSpan.FromMinutes(3));
        WaitUntil(() => rig.Reconnects == 1 && !rig.Pinger.IsRecovering, "reconnect");
        Assert.AreEqual(0, Count<PhoneNotResponding>(rig), "no alert for misses 1-3");

        // Miss 4 Alerts
        UrgentPing(rig, 3, 5, 3);
        Miss(rig, DittoPinger.ShortPingTimeout, 4);
        WaitUntil(() => Count<PhoneNotResponding>(rig) == 1, "alert at miss 4");

        await cts.CancelAsync();
        await run.WaitAsync(HangGuard);
    }

    /// <summary>
    /// A live data event through the dispatcher counts as the phone answering: it clears the misses, raises
    /// <see cref="PhoneRespondingAgain"/>, asks for missed updates and ends recovery.
    /// </summary>
    [TestMethod]
    public async Task DispatcherActivity_ClearsFailureState()
    {
        Rig rig = CreateRig(OneHour);
        var dispatcher = new UpdateDispatcher(rig.Rpc, NullLogger.Instance) { Pinger = rig.Pinger };
        using var cts  = new CancellationTokenSource();
        Task run       = rig.Pinger.RunAsync(cts.Token);

        UrgentPing(rig, 1, 1, 1);
        Miss(rig, DittoPinger.ShortPingTimeout, 1);
        Assert.IsTrue(rig.Pinger.IsRecovering);

        // A Live Data Event
        dispatcher.Handle(new IncomingRPCMessage
        {
            ResponseID  = "live-1",
            BugleRoute  = BugleRoute.DataEvent,
            MessageData = new RPCMessageData { SessionID = "unrelated", Action = ActionType.GetUpdates }.ToByteString(),
        });

        Assert.AreEqual(0, rig.Liveness.TimeoutCount);
        WaitUntil(() => Count<NoDataReceived>(rig) == 1, "catch-up");
        CollectionAssert.AreEqual(new[] { nameof(PhoneNotResponding), nameof(PhoneRespondingAgain), nameof(NoDataReceived) }, EventNames(rig));

        // The Ping Generation Is Released 5 s Later, Which Ends Recovery
        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromSeconds(5)) == 1, "release wait");
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        WaitUntil(() => !rig.Pinger.IsRecovering, "recovery ended");

        await cts.CancelAsync();
        await run.WaitAsync(HangGuard);
    }
}
