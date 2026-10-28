using System.Net;
using System.Text;
using GMessages.Protocol.Auth;
using GMessages.Protocol.Http;
using GMessages.Protocol.LongPoll;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using GMessages.Protocol.Tests.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Protocol.Tests.Pairing;

/// <summary>
/// Checks the start and finish orchestration of <see cref="GaiaPairing"/>.
/// </summary>
/// <remarks>
/// C#-only tests: the behavior was read from <c>StartGaiaPairing</c> and <c>FinishGaiaPairing</c> (no cookies, no
/// devices, newest device first, phone filter, 20 s init timeout with a cancel request, key storage) and is driven by
/// a fake clock, not copied from a golden file. The sign-in response is the canned one from <c>signin.json</c>: two
/// phones, A (seen at 1700000001, timestamp 5551) and C (seen at 1700000003, timestamp 0, unknown int 999).
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go"/>.
/// </remarks>
[TestClass]
public class GaiaPairingTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private const string PhoneC = "cccccccc-1111-2222-3333-444444444444";

    private sealed class Rig
    {
        public required GaiaPairing Pairing { get; init; }

        public required LongPollLoop Loop { get; init; }

        public required AuthData Auth { get; init; }

        public required FakeHttpHandler Handler { get; init; }

        public required SignalingTimeProvider Time { get; init; }

        // ReceiveMessages Answer; Null Means An Open Quiet Stream
        public Func<HttpResponseMessage>? Receive { get; set; }

        // Response Bytes Delivered For Each Pairing Action; Missing Means The Phone Stays Quiet
        public Dictionary<ActionType, byte[]> Responses { get; } = [];

        public ActionType[] SentActions()
        {
            lock (Handler.Requests)
            {
                return [.. Handler.Requests.Where(r => r.Url.EndsWith("/SendMessage", StringComparison.Ordinal)).Select(ActionOf)];
            }
        }
    }

    /// <summary>
    /// Builds a pairing over the given HTTP client with a long poll loop that is never logged in.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="rpc">The RPC session.</param>
    /// <param name="auth">The account.</param>
    /// <param name="time">The clock.</param>
    /// <returns>The pairing and its long poll loop.</returns>
    internal static (GaiaPairing Pairing, LongPollLoop Loop) CreatePairing(GmHttpClient http, RpcSession rpc, AuthData auth, TimeProvider time)
    {
        var refresher = new TokenRefresher(http, auth, GmEndpoints.Production, time, NullLogger.Instance);
        var loop      = new LongPollLoop(http, rpc, auth, GmEndpoints.Production, refresher, new UpdateDispatcher(rpc, NullLogger.Instance), time, NullLogger.Instance);
        var sign_in   = new GaiaSignIn(http, auth, GmEndpoints.Production, time, NullLogger.Instance);
        return (new GaiaPairing(sign_in, rpc, loop, auth, time, NullLogger.Instance), loop);
    }

    /// <summary>
    /// Hands <paramref name="response"/> to the waiter of a recorded SendMessage request, as if the phone answered.
    /// </summary>
    /// <param name="rpc">The RPC session holding the waiter.</param>
    /// <param name="request">The recorded request.</param>
    /// <param name="response">The unencrypted response bytes.</param>
    internal static void Deliver(RpcSession rpc, RecordedRequest request, byte[] response)
    {
        var outgoing = PbLiteSerializer.Deserialize<OutgoingRPCMessage>(request.Body);
        var data     = OutgoingRPCData.Parser.ParseFrom(outgoing.Data.MessageData);
        rpc.TryDeliverResponse(new IncomingRpc
        {
            Raw     = new IncomingRPCMessage { ResponseID = "test-resp" },
            Message = new RPCMessageData { SessionID = outgoing.Data.RequestID, Action = data.Action, UnencryptedData = ByteString.CopyFrom(response) },
        });
    }

    private static ActionType ActionOf(RecordedRequest req) =>
        OutgoingRPCData.Parser.ParseFrom(PbLiteSerializer.Deserialize<OutgoingRPCMessage>(req.Body).Data.MessageData).Action;

    private static Rig CreateRig(AuthData? auth = null)
    {
        auth ??= AuthDataGoldenTests.BuildGolden();
        var time    = new SignalingTimeProvider();
        var handler = new FakeHttpHandler { Time = time };
        var http    = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
        var rpc     = new RpcSession(http, auth, GmEndpoints.Production, time, NullLogger.Instance);

        (GaiaPairing pairing, LongPollLoop loop) = CreatePairing(http, rpc, auth, time);
        var rig = new Rig { Pairing = pairing, Loop = loop, Auth = auth, Handler = handler, Time = time };

        byte[] sign_in_body = Encoding.UTF8.GetBytes(Golden.Load("signin").GetProperty("response").GetProperty("bodyText").GetString()!);
        handler.Respond = (_, req) =>
        {
            if (req.Url.EndsWith("/SignInGaia", StringComparison.Ordinal))
            {
                return FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", sign_in_body);
            }

            if (req.Url.EndsWith("/ReceiveMessages", StringComparison.Ordinal))
            {
                if (rig.Receive is not null)
                {
                    return rig.Receive();
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new OpenStream("[["u8.ToArray())) };
            }

            if (req.Url.EndsWith("/SendMessage", StringComparison.Ordinal) && rig.Responses.TryGetValue(ActionOf(req), out byte[]? response))
            {
                Deliver(rpc, req, response);
            }

            return FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", "[]"u8.ToArray());
        };
        return rig;
    }

    // A Valid Server Init From A Fresh Server Key
    internal static byte[] ServerInit(int verificationVersion, int keyDerivationVersion)
    {
        using var server = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var q            = server.ExportParameters(false).Q;
        var init         = new Ukey2ServerInit
        {
            Version         = 1,
            Random          = ByteString.CopyFrom(new byte[32]),
            HandshakeCipher = Ukey2HandshakeCipher.P256Sha512,
            PublicKey       = new GenericPublicKey { Type = PublicKeyType.EcP256, EcP256PublicKey = new EcP256PublicKey { X = ByteString.CopyFrom([0, .. q.X!]), Y = ByteString.CopyFrom([0, .. q.Y!]) } },
        };
        var message = new Ukey2Message { MessageType = Ukey2Message.Types.Type.ServerInit, MessageData = init.ToByteString() };
        return new GaiaPairingResponseContainer
        {
            Data                             = message.ToByteString(),
            ConfirmedVerificationCodeVersion = verificationVersion,
            ConfirmedKeyDerivationVersion    = keyDerivationVersion,
        }.ToByteArray();
    }

    /// <summary>
    /// Without a server init in 20 s, a cancel request is sent and the start fails with an init timeout that mentions
    /// the multiple phones, as in Go.
    /// </summary>
    [TestMethod]
    public async Task Start_NoResponseIn20s_SendsCancel_ThrowsInitTimeout()
    {
        Rig rig              = CreateRig();
        rig.Auth.DestPhoneId = 0;

        Task<(string, PairingSession)> start = rig.Pairing.StartAsync(CancellationToken.None);
        Assert.IsTrue(SpinWait.SpinUntil(() => rig.Time.CountTimers(TimeSpan.FromSeconds(GaiaPairing.GaiaInitTimeoutSeconds)) > 0, HangGuard), "init timer");
        rig.Time.Advance(TimeSpan.FromSeconds(GaiaPairing.GaiaInitTimeoutSeconds));

        var ex = await Assert.ThrowsExactlyAsync<PairingException>(() => start.WaitAsync(HangGuard));
        Assert.AreEqual(PairingFailure.InitTimeout, ex.Failure);
        Assert.AreEqual("client init timed out (had multiple primary-looking devices)", ex.Message);
        CollectionAssert.AreEqual(new[] { ActionType.CreateGaiaPairingClientInit, ActionType.CancelGaiaPairing }, rig.SentActions());
    }

    /// <summary>
    /// With several phones the newest one is used; the full start and finish then store the keys, the pairing ID and
    /// the phone ID like Go (phone C has no timestamp, so its unknown int forms the phone ID).
    /// </summary>
    [TestMethod]
    public async Task Start_MultipleDevices_PicksNewestLastSeen()
    {
        Rig rig              = CreateRig();
        rig.Auth.DestPhoneId = 0;
        byte[] old_aes       = rig.Auth.RequestCrypto.AesKey;
        rig.Responses[ActionType.CreateGaiaPairingClientInit]     = ServerInit(1, 1);
        rig.Responses[ActionType.CreateGaiaPairingClientFinished] = new GaiaPairingResponseContainer().ToByteArray();

        (string emoji, PairingSession session) = await rig.Pairing.StartAsync(CancellationToken.None).WaitAsync(HangGuard);
        string phone_id                        = await rig.Pairing.FinishAsync(session, CancellationToken.None).WaitAsync(HangGuard);
        rig.Loop.Stop();

        Assert.AreEqual(PhoneC, session.DestRegDevice.RegId);
        Assert.AreEqual(Guid.Parse(PhoneC), rig.Auth.DestRegId);
        CollectionAssert.Contains(PairingEmojis.V1.ToArray(), emoji);
        Assert.AreEqual(session.Id, rig.Auth.PairingId);
        Assert.AreEqual(0UL, rig.Auth.DestPhoneId);
        Assert.AreEqual("mixed-case-src/999", phone_id);
        CollectionAssert.AreNotEqual(old_aes, rig.Auth.RequestCrypto.AesKey);
        (byte[] aes, byte[] hmac) = session.DeriveRequestKeys();
        CollectionAssert.AreEqual(aes, rig.Auth.RequestCrypto.AesKey);
        CollectionAssert.AreEqual(hmac, rig.Auth.RequestCrypto.HmacKey);
    }

    /// <summary>
    /// A long poll that is refused before it connects makes the start fail with that refusal, not a generic error,
    /// and sends no pairing message.
    /// </summary>
    [TestMethod]
    public async Task Start_LongPollFailsBeforeConnecting_ThrowsItsError()
    {
        Rig rig              = CreateRig();
        rig.Auth.DestPhoneId = 0;
        rig.Receive          = () => FakeHttpHandler.Response(HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsExactlyAsync<GmRequestException>(() => rig.Pairing.StartAsync(CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual(401, ex.StatusCode);
        Assert.IsEmpty(rig.SentActions());
    }

    /// <summary>
    /// A previously paired phone that is no longer on the account fails before the long poll starts.
    /// </summary>
    [TestMethod]
    public async Task Start_ExistingDestPhoneIdNotFound_ThrowsPhoneNotFound()
    {
        Rig rig              = CreateRig();
        rig.Auth.DestPhoneId = 42;

        var ex = await Assert.ThrowsExactlyAsync<PairingException>(() => rig.Pairing.StartAsync(CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual(PairingFailure.PhoneNotFound, ex.Failure);
        Assert.IsFalse(rig.Handler.Requests.Any(r => r.Url.EndsWith("/ReceiveMessages", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A negative device switcher is refused when set (C#-only: Go's modulo would pick no phone).
    /// </summary>
    [TestMethod]
    public void HackyDeviceSwitcher_Negative_Throws()
    {
        Rig rig = CreateRig();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => rig.Pairing.HackyDeviceSwitcher = -1);
        rig.Pairing.HackyDeviceSwitcher = 1;
        Assert.AreEqual(1, rig.Pairing.HackyDeviceSwitcher);
    }

    /// <summary>
    /// A Google account without cookies fails before any request.
    /// </summary>
    [TestMethod]
    public async Task Start_NoCookies_ThrowsNoCookies()
    {
        AuthData auth  = AuthData.CreateNew();
        auth.DestRegId = Guid.NewGuid();
        Rig rig        = CreateRig(auth);

        var ex = await Assert.ThrowsExactlyAsync<PairingException>(() => rig.Pairing.StartAsync(CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual(PairingFailure.NoCookies, ex.Failure);
        Assert.AreEqual("gaia pairing requires cookies", ex.Message);
        Assert.IsEmpty(rig.Handler.Requests);
    }
}
