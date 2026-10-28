using System.Net;
using System.Text;
using GMessages.Protocol.Http;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using GMessages.Protocol.Tests.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GMessages.Protocol.Tests.Rpc;

/// <summary>
/// Checks waiting, timeouts and ack retry of <see cref="RpcSession"/>.
/// </summary>
/// <remarks>
/// C#-only behavior: the Go behavior was read from session_handler.go (60 s hard timeout, 5 s slow notice for
/// user-initiated sends, acks re-queued up to 1024) and the timings are driven by a fake clock, not copied from a
/// golden file. See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/session_handler.go"/>.
/// </remarks>
[TestClass]
public class RpcSessionTimingTests
{
    // Fake clock that signals whenever a timer is created, so tests advance only once the wait exists.
    private sealed class SignalingTimeProvider : FakeTimeProvider
    {
        public SemaphoreSlim TimerCreated { get; } = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            TimerCreated.Release();
            return timer;
        }
    }

    private static readonly string[] ExpectedAckOrder = ["a1", "a2", "a3"];

    private sealed record Rig(RpcSession Session, FakeHttpHandler Handler, SignalingTimeProvider Time);

    private static Rig CreateRig()
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        var time      = new SignalingTimeProvider();
        var handler   = new FakeHttpHandler { Time = time };
        handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", Encoding.UTF8.GetBytes("[]"));
        var http = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
        return new Rig(new RpcSession(http, auth, GmEndpoints.Production, time, NullLogger.Instance), handler, time);
    }

    private static async Task WaitForTimersAsync(SignalingTimeProvider time, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static IncomingRpc Response(string requestId) => new()
    {
        Raw     = new IncomingRPCMessage { ResponseID = "resp-1" },
        Message = new RPCMessageData { SessionID = requestId, Action = ActionType.GetUpdates, EncryptedData = ByteString.CopyFromUtf8("x") },
    };

    /// <summary>
    /// A delivered response with the matching request ID completes the send; a second delivery finds no waiter.
    /// </summary>
    [TestMethod]
    public async Task Send_ResolvesWhenMatchingResponseDelivered()
    {
        Rig rig = CreateRig();
        Task<IncomingRpc> send = rig.Session.SendAsync(new SendParams(ActionType.GetUpdates, RequestId: "req-1"), CancellationToken.None);
        await WaitForTimersAsync(rig.Time, 1);

        Assert.IsFalse(rig.Session.TryDeliverResponse(Response("other")));
        IncomingRpc incoming = Response("req-1");
        Assert.IsTrue(rig.Session.TryDeliverResponse(incoming));

        Assert.AreSame(incoming, await send.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsFalse(rig.Session.TryDeliverResponse(Response("req-1")));
    }

    /// <summary>
    /// With no answer, the send fails with <see cref="PhoneNotRespondingException"/> exactly at 60 seconds.
    /// </summary>
    [TestMethod]
    public async Task Send_NoResponseFor60s_ThrowsPhoneNotResponding()
    {
        Rig rig = CreateRig();
        Task<IncomingRpc> send = rig.Session.SendAsync(new SendParams(ActionType.GetUpdates, RequestId: "req-1"), CancellationToken.None);
        await WaitForTimersAsync(rig.Time, 1);

        rig.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.IsFalse(send.IsCompleted);
        rig.Time.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsExactlyAsync<PhoneNotRespondingException>(() => send.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsFalse(rig.Session.TryDeliverResponse(Response("req-1")), "waiter should be gone");
    }

    /// <summary>
    /// A user-initiated send raises <see cref="RpcSession.PhoneSlow"/> once at 5 seconds and keeps waiting.
    /// </summary>
    [TestMethod]
    public async Task Send_UserInitiated_RaisesPhoneSlowAfter5s()
    {
        Rig rig = CreateRig();
        var slow  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        rig.Session.PhoneSlow += (_, _) =>
        {
            Interlocked.Increment(ref count);
            slow.TrySetResult();
        };
        Task<IncomingRpc> send = rig.Session.SendAsync(new SendParams(ActionType.GetUpdates, RequestId: "req-1", UserInitiated: true), CancellationToken.None);
        await WaitForTimersAsync(rig.Time, 2);

        rig.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.AreEqual(0, Volatile.Read(ref count));
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await slow.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, Volatile.Read(ref count));
        Assert.IsFalse(send.IsCompleted);
        Assert.IsTrue(rig.Session.TryDeliverResponse(Response("req-1")));
        await send.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// A background send never raises the slow notice.
    /// </summary>
    [TestMethod]
    public async Task Send_NotUserInitiated_DoesNotRaisePhoneSlow()
    {
        Rig rig = CreateRig();
        int count = 0;
        rig.Session.PhoneSlow += (_, _) => Interlocked.Increment(ref count);
        Task<IncomingRpc> send = rig.Session.SendAsync(new SendParams(ActionType.GetUpdates, RequestId: "req-1"), CancellationToken.None);
        await WaitForTimersAsync(rig.Time, 1);

        rig.Time.Advance(TimeSpan.FromSeconds(30));

        Assert.AreEqual(0, Volatile.Read(ref count));
        Assert.IsTrue(rig.Session.TryDeliverResponse(Response("req-1")));
        await send.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Failing all waiters ends waiting sends, with or without a timeout, with <see cref="ConnectionClosedException"/>.
    /// </summary>
    [TestMethod]
    public async Task FailAllWaiters_ThrowsConnectionClosed()
    {
        Rig rig = CreateRig();
        Task<IncomingRpc> send = rig.Session.SendAsync(new SendParams(ActionType.GetUpdates, RequestId: "req-1"), CancellationToken.None);
        await WaitForTimersAsync(rig.Time, 1);
        Task<IncomingRpc> no_wait = await rig.Session.SendAsyncNoWait(new SendParams(ActionType.GetUpdates, RequestId: "req-2"), CancellationToken.None);

        rig.Session.FailAllWaiters();

        await Assert.ThrowsExactlyAsync<ConnectionClosedException>(() => send.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsExactlyAsync<ConnectionClosedException>(() => no_wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// A rejected request leaves no waiter behind.
    /// </summary>
    [TestMethod]
    public async Task Send_RejectedRequest_ThrowsAndRemovesWaiter()
    {
        Rig rig = CreateRig();
        rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.BadRequest);

        await Assert.ThrowsExactlyAsync<GmRequestException>(() => rig.Session.SendAsync(new SendParams(ActionType.GetUpdates, RequestId: "req-1"), CancellationToken.None));

        Assert.IsFalse(rig.Session.TryDeliverResponse(Response("req-1")));
    }

    /// <summary>
    /// With cookies, unencrypted-only messages are ignored except for the two Google pairing actions.
    /// </summary>
    [TestMethod]
    public async Task TryDeliverResponse_IgnoresUnencryptedOnlyExceptGaiaPairing()
    {
        Rig rig = CreateRig();
        IncomingRpc Unencrypted(string id, ActionType action) => new()
        {
            Raw     = new IncomingRPCMessage { ResponseID = "resp-1" },
            Message = new RPCMessageData { SessionID = id, Action = action, UnencryptedData = ByteString.CopyFromUtf8("x") },
        };
        Task<IncomingRpc> first = await rig.Session.SendAsyncNoWait(new SendParams(ActionType.GetUpdates, RequestId: "req-1"), CancellationToken.None);
        Task<IncomingRpc> second = await rig.Session.SendAsyncNoWait(new SendParams(ActionType.CreateGaiaPairingClientInit, RequestId: "req-2"), CancellationToken.None);

        Assert.IsFalse(rig.Session.TryDeliverResponse(Unencrypted("req-1", ActionType.GetUpdates)));
        Assert.IsTrue(rig.Session.TryDeliverResponse(Unencrypted("req-2", ActionType.CreateGaiaPairingClientInit)));
        Assert.IsFalse(first.IsCompleted);
        await second.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// A failed ack request puts the batch back in front of newer acks, and a later flush sends them in that order.
    /// </summary>
    [TestMethod]
    public async Task FlushAcks_FailedPost_Requeues()
    {
        Rig rig = CreateRig();
        rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.BadRequest);
        rig.Session.QueueAck("a1");
        rig.Session.QueueAck("a2");

        await rig.Session.FlushAcksAsync(CancellationToken.None);

        Assert.AreEqual(1, rig.Handler.Requests.Count);
        rig.Session.QueueAck("a3");
        rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", Encoding.UTF8.GetBytes("[]"));
        await rig.Session.FlushAcksAsync(CancellationToken.None);

        var sent = PbLiteSerializer.Deserialize<AckMessageRequest>(rig.Handler.Requests[1].Body);
        CollectionAssert.AreEqual(ExpectedAckOrder, sent.Acks.Select(a => a.RequestID).ToArray());
    }

    /// <summary>
    /// A failed batch that would push the queue over 1024 is dropped instead of re-queued.
    /// </summary>
    [TestMethod]
    public async Task FlushAcks_FailedPost_OverCap_DropsBatch()
    {
        Rig rig = CreateRig();
        rig.Handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.BadRequest);
        for (int i = 0; i < RpcSession.MaxQueuedAcks + 1; i++)
        {
            rig.Session.QueueAck("id-" + i);
        }

        await rig.Session.FlushAcksAsync(CancellationToken.None);
        await rig.Session.FlushAcksAsync(CancellationToken.None);

        Assert.AreEqual(1, rig.Handler.Requests.Count, "dropped batch must not be retried");
    }
}
