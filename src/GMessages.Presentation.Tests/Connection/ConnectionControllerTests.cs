using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GMessages.Presentation.Connection;
using GMessages.Protocol;
using GMessages.Protocol.Crypto;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.LongPoll;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Presentation.Tests.Connection;

/// <summary>
/// Checks how <see cref="ConnectionController"/> starts, pairs, unpairs, reconnects and signs out, against a fake
/// server and a fake clock.
/// </summary>
/// <remarks>
/// Every test's <c>post</c> runner flags that it is running, so each event handler can check it was called through
/// <c>post</c>. Waits use <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/> with a 10 s guard, never a sleep.
/// </remarks>
[TestClass]
public sealed class ConnectionControllerTests
{
    internal static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private const string PbLiteMime = "application/json+protobuf";

    internal static readonly Dictionary<string, string> PairCookies = new() { ["SAPISID"] = "fake-sapisid", ["SID"] = "fake-sid" };

    // =========================================================================
    // RIG
    // =========================================================================

    internal sealed class Rig : IAsyncDisposable
    {
        private readonly Lock _postLock = new();
        private bool _inPost;

        public Rig(Action<Action>? post = null)
        {
            DataFolder = Path.Combine(Path.GetTempPath(), "conn-test-" + Guid.NewGuid().ToString("N"));
            Paths      = new AppPaths(DataFolder);
            Handler    = new FakeHttpHandler { Time = Time };

            // Inline Post That Flags Itself, Unless The Test Brings Its Own
            Action<Action> inline = a =>
            {
                lock (_postLock)
                {
                    _inPost = true;
                    try
                    {
                        a();
                    }
                    finally
                    {
                        _inPost = false;
                    }
                }
            };
            Controller = new ConnectionController(Paths, GmEndpoints.Production, Time, NullLoggerFactory.Instance, post ?? inline, Handler);

            // Record Events And Whether They Came Through Post
            Controller.StateChanged += (_, _) =>
            {
                CheckPosted();
                States.Enqueue(Controller.State);
            };
            Controller.ClientEvent += (_, e) =>
            {
                CheckPosted();
                Events.Enqueue(e);
            };

            Handler.Respond = (_, req) =>
            {
                if (Respond?.Invoke(req) is { } custom)
                {
                    return custom;
                }

                if (req.Url.EndsWith("/ReceiveMessages", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new OpenStream("[["u8.ToArray())) };
                }

                return FakeHttpHandler.Response(HttpStatusCode.OK, PbLiteMime, "[]"u8.ToArray());
            };
        }

        public bool CustomPost { get; init; }

        public string DataFolder { get; }

        public AppPaths Paths { get; }

        public SignalingTimeProvider Time { get; } = new();

        public FakeHttpHandler Handler { get; }

        public ConnectionController Controller { get; }

        public ConcurrentQueue<ConnectionState> States { get; } = new();

        public ConcurrentQueue<object> Events { get; } = new();

        public int NotPosted;

        // Answer For A Request, Or Null For The Default (An Open Quiet Stream For The Long Poll, "[]" Otherwise)
        public Func<RecordedRequest, HttpResponseMessage?>? Respond { get; set; }

        public RecordedRequest[] Requests(string suffix)
        {
            lock (Handler.Requests)
            {
                return [.. Handler.Requests.Where(r => r.Url.EndsWith(suffix, StringComparison.Ordinal))];
            }
        }

        public void SaveSession(TimeSpan tokenLifetime) => new SessionStore(Paths.SessionFile).Save(BuildSession(Time.GetUtcNow() + tokenLifetime));

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            if (Directory.Exists(DataFolder))
            {
                Directory.Delete(DataFolder, recursive: true);
            }
        }

        private void CheckPosted()
        {
            if (!_inPost && !CustomPost)
            {
                Interlocked.Increment(ref NotPosted);
            }
        }
    }

    // A Logged-In Google Account Session With A Token Valid For The Given Time
    private static AuthData BuildSession(DateTimeOffset tokenExpiry)
    {
        AuthData ad = new()
        {
            RequestCrypto    = AesCtrHelper.CreateRandom(),
            RefreshKey       = RefreshKey.Generate(),
            Browser          = new Device { UserID = 1, SourceID = "test-src", Network = "GDitto" },
            Mobile           = new Device { UserID = 1, SourceID = "test-src", Network = "GDitto" },
            TachyonAuthToken = "fake-tachyon"u8.ToArray(),
            TachyonTtlMicros = 86400000000,
            TachyonExpiry    = tokenExpiry,
            SessionId        = Guid.NewGuid(),
            DestRegId        = Guid.NewGuid(),
            DestPhoneId      = 42,
            PairingId        = Guid.NewGuid(),
        };
        ad.Cookies.Set(PairCookies);
        return ad;
    }

    internal static void WaitUntil(Func<bool> condition, string what) => Assert.IsTrue(SpinWait.SpinUntil(condition, HangGuard), what);

    // =========================================================================
    // FAKE PHONE (SMALL COPIES OF THE PROTOCOL TESTS' PAIRING HELPERS)
    // =========================================================================

    private static ActionType ActionOf(RecordedRequest req) =>
        OutgoingRPCData.Parser.ParseFrom(PbLiteSerializer.Deserialize<OutgoingRPCMessage>(req.Body).Data.MessageData).Action;

    // Hands A Response To The Waiter Of A Recorded SendMessage, As If The Phone Answered
    private static void Deliver(RpcSession rpc, RecordedRequest request, byte[] response)
    {
        var outgoing = PbLiteSerializer.Deserialize<OutgoingRPCMessage>(request.Body);
        var data     = OutgoingRPCData.Parser.ParseFrom(outgoing.Data.MessageData);
        rpc.TryDeliverResponse(new IncomingRpc
        {
            Raw     = new IncomingRPCMessage { ResponseID = "test-resp" },
            Message = new RPCMessageData { SessionID = outgoing.Data.RequestID, Action = data.Action, UnencryptedData = ByteString.CopyFrom(response) },
        });
    }

    // A Valid UKEY2 Server Init With A Fresh P-256 Key
    private static byte[] ServerInit()
    {
        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
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
            ConfirmedVerificationCodeVersion = 1,
            ConfirmedKeyDerivationVersion    = 1,
        }.ToByteArray();
    }

    // Scripts The Config, Sign-In And Phone Answers; The Finish Answer Decides Success Or Failure (Null: Never Tapped)
    internal static void ScriptPairing(Rig rig, byte[]? finished)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "signin.json")));
        JsonElement sign_in    = doc.RootElement;
        byte[] config_body     = Encoding.UTF8.GetBytes(sign_in.GetProperty("config").GetProperty("bodyText").GetString()!);
        byte[] sign_in_body    = Encoding.UTF8.GetBytes(sign_in.GetProperty("response").GetProperty("bodyText").GetString()!);
        byte[] server_init     = ServerInit();

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
                RpcSession rpc    = rig.Controller.CurrentClient!.Rpc;
                ActionType action = ActionOf(req);
                if (action == ActionType.CreateGaiaPairingClientInit)
                {
                    Deliver(rpc, req, server_init);
                }
                else if (action == ActionType.CreateGaiaPairingClientFinished && finished is not null)
                {
                    Deliver(rpc, req, finished);
                }
            }

            return null;
        };
    }

    // =========================================================================
    // START
    // =========================================================================

    /// <summary>
    /// No session file means not paired; nothing is sent and no client exists.
    /// </summary>
    [TestMethod]
    public async Task Start_NoSession_IsNotPaired()
    {
        await using var rig = new Rig();

        await rig.Controller.StartAsync().WaitAsync(HangGuard);

        Assert.AreEqual(ConnectionState.NotPaired, rig.Controller.State);
        Assert.IsNull(rig.Controller.Client);
        Assert.IsEmpty(rig.Handler.Requests);
        Assert.AreEqual(0, rig.NotPosted);
    }

    /// <summary>
    /// A valid session connects: Connecting, then Connected, and the long poll opens.
    /// </summary>
    [TestMethod]
    public async Task Start_ValidSession_IsConnected()
    {
        await using var rig = new Rig();
        rig.SaveSession(TimeSpan.FromDays(1));

        await rig.Controller.StartAsync().WaitAsync(HangGuard);

        Assert.AreEqual(ConnectionState.Connected, rig.Controller.State);
        Assert.IsNotNull(rig.Controller.Client);
        CollectionAssert.AreEqual(new[] { ConnectionState.Connecting, ConnectionState.Connected }, rig.States.ToArray());
        WaitUntil(() => rig.Controller.Client!.IsConnected, "long poll open");
        Assert.AreEqual(0, rig.NotPosted);
    }

    /// <summary>
    /// A 401 on the start-up token refresh signs out with the plain-text reason and drops the client.
    /// </summary>
    [TestMethod]
    public async Task Start_RefreshUnauthorized_IsSignedOut()
    {
        await using var rig = new Rig();
        rig.SaveSession(TimeSpan.FromMinutes(30));
        rig.Respond = req => req.Url.EndsWith("/RegisterRefresh", StringComparison.Ordinal) ? FakeHttpHandler.Response(HttpStatusCode.Unauthorized) : null;

        await rig.Controller.StartAsync().WaitAsync(HangGuard);

        Assert.AreEqual(ConnectionState.SignedOut, rig.Controller.State);
        Assert.AreEqual("You were signed out. Sign in again to keep getting texts.", rig.Controller.SignedOutReason);
        Assert.IsNull(rig.Controller.Client);
        Assert.IsEmpty(rig.Requests("/ReceiveMessages"));
        Assert.IsFalse(File.Exists(rig.Paths.SessionFile), "dead session deleted");
        Assert.AreEqual(0, rig.NotPosted);
    }

    // =========================================================================
    // SIGN OUT AND RECONNECT
    // =========================================================================

    /// <summary>
    /// A 401 on the long poll while connected signs out and disposes the client (off the event thread).
    /// </summary>
    [TestMethod]
    public async Task LongPollUnauthorized_IsSignedOut_AndClientDisposed()
    {
        await using var rig = new Rig();
        rig.SaveSession(TimeSpan.FromDays(1));
        GmClient? client = null;
        rig.Respond = req =>
        {
            if (!req.Url.EndsWith("/ReceiveMessages", StringComparison.Ordinal))
            {
                return null;
            }

            // The Client Making The Long Poll Is Still Current Here
            client ??= rig.Controller.CurrentClient;
            return FakeHttpHandler.Response(HttpStatusCode.Unauthorized);
        };

        await rig.Controller.StartAsync().WaitAsync(HangGuard);
        WaitUntil(() => rig.Controller.State == ConnectionState.SignedOut, "signed out");

        Assert.AreEqual("You were signed out. Sign in again to keep getting texts.", rig.Controller.SignedOutReason);
        Assert.IsNull(rig.Controller.Client);
        Assert.IsNotNull(client);
        Assert.IsFalse(File.Exists(rig.Paths.SessionFile), "dead session deleted");
        Assert.IsTrue(rig.Events.OfType<ListenFatalError>().Any(), "fatal error forwarded");
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.AreEqual(0, rig.NotPosted);
    }

    /// <summary>
    /// GaiaLoggedOut (signed out on the phone) signs out, drops the client, deletes the session and is forwarded.
    /// </summary>
    [TestMethod]
    public async Task GaiaLoggedOut_IsSignedOut_AndFileDeleted()
    {
        await using var rig = new Rig();
        rig.SaveSession(TimeSpan.FromDays(1));
        await rig.Controller.StartAsync().WaitAsync(HangGuard);

        // Fed Straight In: Producing It Over The Wire Needs An Encrypted GET_UPDATES Stream
        rig.Controller.OnClientEvent(rig.Controller.CurrentClient!, new GaiaLoggedOut());
        WaitUntil(() => rig.Controller.State == ConnectionState.SignedOut, "signed out");

        Assert.IsNull(rig.Controller.Client);
        Assert.IsFalse(File.Exists(rig.Paths.SessionFile), "dead session deleted");
        Assert.IsTrue(rig.Events.OfType<GaiaLoggedOut>().Any(), "forwarded");
        Assert.AreEqual(0, rig.NotPosted);
    }

    /// <summary>
    /// A failing long poll moves to Reconnecting; the next successful open moves back to Connected.
    /// </summary>
    [TestMethod]
    public async Task TemporaryError_IsReconnecting_ThenRecoveredIsConnected()
    {
        await using var rig = new Rig();
        rig.SaveSession(TimeSpan.FromDays(1));
        int polls   = 0;
        rig.Respond = req => req.Url.EndsWith("/ReceiveMessages", StringComparison.Ordinal) && Interlocked.Increment(ref polls) == 1
            ? FakeHttpHandler.Response(HttpStatusCode.InternalServerError)
            : null;

        await rig.Controller.StartAsync().WaitAsync(HangGuard);
        WaitUntil(() => rig.Controller.State == ConnectionState.Reconnecting, "reconnecting");
        Assert.IsTrue(rig.Events.OfType<ListenTemporaryError>().Any());

        // Wait Out The Backoff On The Fake Clock
        TimeSpan backoff = LongPollLoop.RetryDelay(2);
        WaitUntil(() => rig.Time.CountTimers(backoff) > 0, "backoff wait");
        rig.Time.Advance(backoff);

        WaitUntil(() => rig.Controller.State == ConnectionState.Connected, "recovered");
        Assert.IsTrue(rig.Events.OfType<ListenRecovered>().Any());
        Assert.AreEqual(ConnectionState.Connected, rig.States.Last());
        Assert.AreEqual(0, rig.NotPosted);
    }

    /// <summary>
    /// A session save that fails (its temp file path is a folder) is forwarded through ClientEvent.
    /// </summary>
    [TestMethod]
    public async Task SessionSaveFailed_IsForwarded()
    {
        await using var rig = new Rig();
        rig.SaveSession(TimeSpan.FromDays(1));
        Directory.CreateDirectory(rig.Paths.SessionFile + ".tmp");
        rig.Respond = req =>
        {
            if (!req.Url.EndsWith("/ReceiveMessages", StringComparison.Ordinal))
            {
                return null;
            }

            // A Rotated Cookie Triggers A Save Right Away
            var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new OpenStream("[["u8.ToArray())) };
            res.Headers.TryAddWithoutValidation("Set-Cookie", "SID=rotated-sid; Path=/");
            return res;
        };

        await rig.Controller.StartAsync().WaitAsync(HangGuard);

        WaitUntil(() => rig.Events.OfType<SessionSaveFailed>().Any(), "save failure forwarded");
        Assert.AreEqual(ConnectionState.Connected, rig.Controller.State, "still connected");
        Assert.AreEqual(0, rig.NotPosted);
    }

    /// <summary>
    /// Resume while connected makes the long poll reconnect once (Plan 1 R24): exactly one more /ReceiveMessages.
    /// </summary>
    [TestMethod]
    public async Task NotifyResumed_WhileConnected_ReconnectsOnce()
    {
        await using var rig = new Rig();
        rig.SaveSession(TimeSpan.FromDays(1));
        await rig.Controller.StartAsync().WaitAsync(HangGuard);
        WaitUntil(() => rig.Controller.Client!.IsConnected && rig.Requests("/ReceiveMessages").Length == 1, "first connect");

        await rig.Controller.NotifyResumedAsync().WaitAsync(HangGuard);

        WaitUntil(() => rig.Requests("/ReceiveMessages").Length == 2, "reconnected");
        WaitUntil(() => rig.Controller.Client!.IsConnected, "connected again");
        Assert.HasCount(2, rig.Requests("/ReceiveMessages"));
        Assert.AreEqual(ConnectionState.Connected, rig.Controller.State);
    }

    /// <summary>
    /// Resume and network-back do nothing while not paired.
    /// </summary>
    [TestMethod]
    public async Task Notify_WhileNotPaired_DoesNothing()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync().WaitAsync(HangGuard);

        await rig.Controller.NotifyResumedAsync().WaitAsync(HangGuard);
        await rig.Controller.NotifyNetworkAvailableAsync().WaitAsync(HangGuard);

        Assert.AreEqual(ConnectionState.NotPaired, rig.Controller.State);
        Assert.IsEmpty(rig.Handler.Requests);
    }

    // =========================================================================
    // PAIRING AND UNPAIRING
    // =========================================================================

    /// <summary>
    /// A successful pairing shows the emoji, saves the session and ends Connected with a client.
    /// </summary>
    [TestMethod]
    public async Task Pair_Success_IsConnected()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync().WaitAsync(HangGuard);
        ScriptPairing(rig, new GaiaPairingResponseContainer().ToByteArray());

        string? shown = null;
        await rig.Controller.PairAsync(PairCookies, e => shown = e, CancellationToken.None).WaitAsync(HangGuard);

        Assert.IsNotNull(shown, "emoji shown");
        Assert.AreEqual(ConnectionState.Connected, rig.Controller.State);
        Assert.IsNotNull(rig.Controller.Client);
        Assert.IsTrue(File.Exists(rig.Paths.SessionFile), "session saved");
        Assert.IsTrue(rig.Events.OfType<PairSuccessful>().Any(), "pair event forwarded");
        Assert.AreEqual(0, rig.NotPosted);
    }

    /// <summary>
    /// A wrong emoji keeps NotPaired, saves nothing, drops the client and rethrows the pairing error.
    /// </summary>
    [TestMethod]
    public async Task Pair_WrongEmoji_StaysNotPaired_AndRethrows()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync().WaitAsync(HangGuard);
        ScriptPairing(rig, new GaiaPairingResponseContainer { FinishErrorType = 1, FinishErrorCode = GaiaPairingErrorCode.WrongVerificationCodeSelected }.ToByteArray());

        var ex = await Assert.ThrowsExactlyAsync<PairingException>(() => rig.Controller.PairAsync(PairCookies, _ => { }, CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual(PairingFailure.IncorrectEmoji, ex.Failure);
        Assert.AreEqual(ConnectionState.NotPaired, rig.Controller.State);
        Assert.IsNull(rig.Controller.Client);
        Assert.IsNull(rig.Controller.CurrentClient);
        rig.Time.Advance(GmClient.SaveDebounce);
        Assert.IsFalse(File.Exists(rig.Paths.SessionFile), "nothing saved");
        Assert.IsEmpty(rig.States, "no state change");
    }

    /// <summary>
    /// A failed pairing after a sign-out stays SignedOut, with its reason.
    /// </summary>
    [TestMethod]
    public async Task Pair_FailsAfterSignOut_StaysSignedOut()
    {
        await using var rig = new Rig();
        rig.SaveSession(TimeSpan.FromMinutes(30));
        rig.Respond = req => req.Url.EndsWith("/RegisterRefresh", StringComparison.Ordinal) ? FakeHttpHandler.Response(HttpStatusCode.Unauthorized) : null;
        await rig.Controller.StartAsync().WaitAsync(HangGuard);
        ScriptPairing(rig, new GaiaPairingResponseContainer { FinishErrorType = 1, FinishErrorCode = GaiaPairingErrorCode.WrongVerificationCodeSelected }.ToByteArray());

        await Assert.ThrowsExactlyAsync<PairingException>(() => rig.Controller.PairAsync(PairCookies, _ => { }, CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual(ConnectionState.SignedOut, rig.Controller.State);
        Assert.IsNotNull(rig.Controller.SignedOutReason);
    }

    /// <summary>
    /// Dispose during the emoji wait cancels the pairing at once instead of waiting out the tap: dispose finishes and
    /// PairAsync throws a cancellation.
    /// </summary>
    [TestMethod]
    public async Task Dispose_DuringEmojiWait_CancelsPairing()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync().WaitAsync(HangGuard);
        ScriptPairing(rig, finished: null);

        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pair = rig.Controller.PairAsync(PairCookies, _ => shown.TrySetResult(), CancellationToken.None);
        await shown.Task.WaitAsync(HangGuard);

        await rig.Controller.DisposeAsync().AsTask().WaitAsync(HangGuard);

        await Assert.ThrowsAsync<OperationCanceledException>(() => pair.WaitAsync(HangGuard));
        Assert.IsNull(rig.Controller.CurrentClient);
    }

    /// <summary>
    /// Unpair while connected ends NotPaired with the session file gone and no client.
    /// </summary>
    [TestMethod]
    public async Task Unpair_IsNotPaired_AndFileGone()
    {
        await using var rig = new Rig();
        rig.SaveSession(TimeSpan.FromDays(1));
        await rig.Controller.StartAsync().WaitAsync(HangGuard);

        await rig.Controller.UnpairAsync().WaitAsync(HangGuard);

        Assert.AreEqual(ConnectionState.NotPaired, rig.Controller.State);
        Assert.IsNull(rig.Controller.Client);
        Assert.IsFalse(File.Exists(rig.Paths.SessionFile));
        Assert.AreEqual(0, rig.NotPosted);
    }

    // =========================================================================
    // MARSHALING
    // =========================================================================

    /// <summary>
    /// With a queued post, no handler runs until the queue is pumped; then every state change and event arrives in
    /// order.
    /// </summary>
    [TestMethod]
    public async Task Events_AreRaisedOnlyThroughPost()
    {
        var queue = new ConcurrentQueue<Action>();
        await using var rig = new Rig(queue.Enqueue) { CustomPost = true };
        rig.SaveSession(TimeSpan.FromDays(1));
        rig.Respond = req => req.Url.EndsWith("/ReceiveMessages", StringComparison.Ordinal) ? FakeHttpHandler.Response(HttpStatusCode.Unauthorized) : null;

        await rig.Controller.StartAsync().WaitAsync(HangGuard);
        WaitUntil(() => rig.Controller.State == ConnectionState.SignedOut, "signed out");

        Assert.IsEmpty(rig.States, "nothing raised before pumping");
        Assert.IsEmpty(rig.Events, "nothing raised before pumping");

        while (queue.TryDequeue(out Action? action))
        {
            action();
        }

        CollectionAssert.AreEqual(new[] { ConnectionState.SignedOut, ConnectionState.SignedOut, ConnectionState.SignedOut }, rig.States.ToArray(), "handlers read the current state");
        Assert.IsTrue(rig.Events.OfType<ListenFatalError>().Any());
    }
}
