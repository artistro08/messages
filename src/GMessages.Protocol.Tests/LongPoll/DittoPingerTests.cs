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
/// Checks the ping timing and liveness events of <see cref="DittoPinger"/>.
/// </summary>
/// <remarks>
/// C#-only: the behavior was read from <c>dittoPinger</c> and <c>phoneLiveness</c> in longpoll.go (1 minute pings,
/// 10 s urgent pings, first miss raises "not responding", recovery re-ping after 1 minute) and is driven by a fake
/// clock, not copied from a golden file.
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/longpoll.go"/>.
/// </remarks>
[TestClass]
public class DittoPingerTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private sealed record Rig(DittoPinger Pinger, RpcSession Rpc, FakeHttpHandler Handler, SignalingTimeProvider Time, List<object> Events);

    private static Rig CreateRig(TimeSpan pingInterval)
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        var time      = new SignalingTimeProvider();
        var handler   = new FakeHttpHandler { Time = time };
        handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", Encoding.UTF8.GetBytes("[]"));

        var http   = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
        var rpc    = new RpcSession(http, auth, GmEndpoints.Production, time, NullLogger.Instance);
        var pinger = new DittoPinger(rpc, time, NullLogger.Instance, _ => Task.CompletedTask, () => Task.CompletedTask) { PingInterval = pingInterval };
        var events = new List<object>();
        pinger.EventRaised += e => { lock (events) { events.Add(e); } };

        // Like startLongPolling: No Data-Receive Check In The First 10 Minutes
        pinger.BumpDataReceiveCheck(TimeSpan.FromMinutes(10));
        return new Rig(pinger, rpc, handler, time, events);
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

    private static bool Answer(Rig rig, string requestId) => rig.Rpc.TryDeliverResponse(new IncomingRpc
    {
        Raw     = new IncomingRPCMessage { ResponseID = "pong" },
        Message = new RPCMessageData { SessionID = requestId, Action = ActionType.NotifyDittoActivity, EncryptedData = ByteString.CopyFromUtf8("x") },
    });

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
        Assert.IsTrue(Answer(rig, Pings(rig)[0].RequestID));

        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromMinutes(1)) == 3, "ping wait and next loop wait");
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        WaitUntil(() => Pings(rig).Length == 2, "ping at 2 minutes");
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
        Rig rig = CreateRig(TimeSpan.FromHours(1));
        using var cts = new CancellationTokenSource();
        Task run      = rig.Pinger.RunAsync(cts.Token);
        WaitUntil(() => rig.Time.CountTimers(TimeSpan.FromHours(1)) == 1, "loop wait");

        // Urgent Ping, Unanswered
        rig.Pinger.ShortCircuit();
        WaitUntil(() => Pings(rig).Length == 1 && rig.Time.CountTimers(DittoPinger.ShortPingTimeout) == 1, "urgent ping");
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        WaitUntil(() => EventNames(rig).Contains(nameof(PhoneNotResponding)), "not responding");

        // Recovery Ping After 1 Minute, Answered
        WaitUntil(() => rig.Time.CountTimers(DittoPinger.InitialRecoveryInterval) >= 1, "recovery wait");
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        WaitUntil(() => Pings(rig).Length == 2, "recovery ping");
        Assert.IsTrue(Answer(rig, Pings(rig)[1].RequestID));
        WaitUntil(() => EventNames(rig).Contains(nameof(PhoneRespondingAgain)), "responding again");

        // Not Responding Was Sent, So Go Also Asks For Missed Updates
        WaitUntil(() => EventNames(rig).Contains(nameof(NoDataReceived)), "catch-up");
        CollectionAssert.AreEqual(new[] { nameof(PhoneNotResponding), nameof(PhoneRespondingAgain), nameof(NoDataReceived) }, EventNames(rig));

        await cts.CancelAsync();
        await run.WaitAsync(HangGuard);
    }
}
