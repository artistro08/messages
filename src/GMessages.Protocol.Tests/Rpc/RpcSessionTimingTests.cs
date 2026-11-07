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
/// These tests have no Go golden file. The expected behavior comes from the Go session_handler.go: a hard timeout
/// of 60 seconds, a slow notice after 5 seconds for user-initiated sends, and up to 1024 acks queued again. A fake
/// clock drives the timings. See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/session_handler.go"/>.
/// </remarks>
[TestClass]
public class RpcSessionTimingTests
{

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
            Assert.IsTrue(await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(10)), "timer was not created");
        }
    }

    private static IncomingRpc Response(string requestId) => new()
    {
        Raw     = new IncomingRPCMessage { ResponseID = "resp-1" },
        Message = new RPCMessageData { SessionID = requestId, Action = ActionType.GetUpdates, EncryptedData = ByteString.CopyFromUtf8("x") },
    };

    /// <summary>
    /// A response with the matching request ID completes the send. A second delivery finds no waiter.
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
    /// A user-initiated send answered in time never raises the slow notice later.
    /// </summary>
    [TestMethod]
    public async Task Send_UserInitiated_AnsweredEarly_NeverRaisesPhoneSlow()
    {
        Rig rig = CreateRig();
        int count = 0;
        rig.Session.PhoneSlow += (_, _) => Interlocked.Increment(ref count);
        Task<IncomingRpc> send = rig.Session.SendAsync(new SendParams(ActionType.GetUpdates, RequestId: "req-1", UserInitiated: true), CancellationToken.None);
        await WaitForTimersAsync(rig.Time, 2);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(rig.Session.TryDeliverResponse(Response("req-1")));
        await send.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Time.Advance(TimeSpan.FromSeconds(9));
        await Task.Yield();

        Assert.AreEqual(0, Volatile.Read(ref count));
    }

    /// <summary>
    /// A user-initiated send that FailAllWaiters ends never raises the slow notice later.
    /// </summary>
    [TestMethod]
    public async Task Send_UserInitiated_ConnectionClosedEarly_NeverRaisesPhoneSlow()
    {
        Rig rig = CreateRig();
        int count = 0;
        rig.Session.PhoneSlow += (_, _) => Interlocked.Increment(ref count);
        Task<IncomingRpc> send = rig.Session.SendAsync(new SendParams(ActionType.GetUpdates, RequestId: "req-1", UserInitiated: true), CancellationToken.None);
        await WaitForTimersAsync(rig.Time, 2);

        rig.Session.FailAllWaiters();
        await Assert.ThrowsExactlyAsync<ConnectionClosedException>(() => send.WaitAsync(TimeSpan.FromSeconds(10)));
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        await Task.Yield();

        Assert.AreEqual(0, Volatile.Read(ref count));
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
    /// FailAllWaiters ends the waiting sends with <see cref="ConnectionClosedException"/>, with or without a timeout.
    /// </summary>
    [TestMethod]
    public async Task FailAllWaiters_ThrowsConnectionClosed()
    {
        Rig rig = CreateRig();
        Task<IncomingRpc> send = rig.Session.SendAsync(new SendParams(ActionType.GetUpdates, RequestId: "req-1"), CancellationToken.None);
        await WaitForTimersAsync(rig.Time, 1);
        Task<IncomingRpc> noWait = await rig.Session.SendNoWaitAsync(new SendParams(ActionType.GetUpdates, RequestId: "req-2"), CancellationToken.None);

        rig.Session.FailAllWaiters();

        await Assert.ThrowsExactlyAsync<ConnectionClosedException>(() => send.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsExactlyAsync<ConnectionClosedException>(() => noWait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// A request that the server rejects leaves no waiter.
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
    /// With cookies, the session ignores unencrypted-only messages. The two Google pairing actions are the exception.
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
        Task<IncomingRpc> first = await rig.Session.SendNoWaitAsync(new SendParams(ActionType.GetUpdates, RequestId: "req-1"), CancellationToken.None);
        Task<IncomingRpc> second = await rig.Session.SendNoWaitAsync(new SendParams(ActionType.CreateGaiaPairingClientInit, RequestId: "req-2"), CancellationToken.None);

        Assert.IsFalse(rig.Session.TryDeliverResponse(Unencrypted("req-1", ActionType.GetUpdates)));
        Assert.IsTrue(rig.Session.TryDeliverResponse(Unencrypted("req-2", ActionType.CreateGaiaPairingClientInit)));
        Assert.IsFalse(first.IsCompleted);
        await second.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// A failed ack request puts the batch in front of the newer acks. A later flush sends them in that order.
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
    /// The session drops a failed batch that would push the queue over 1024 acks. It does not queue the batch again.
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
