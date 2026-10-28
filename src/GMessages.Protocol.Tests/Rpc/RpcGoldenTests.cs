using System.Net;
using System.Text;
using System.Text.Json;
using GMessages.Protocol.Http;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using GMessages.Protocol.Tests.Session;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GMessages.Protocol.Tests.Rpc;

/// <summary>
/// Checks the RPC session against requests and decrypt results from the Go LibGM, using <c>rpc.json</c>.
/// </summary>
/// <remarks>
/// Go's encryption uses a random IV, so encrypted requests are compared by decrypting Go's payload and checking the
/// plaintext, with the encrypted field normalized away before comparing the rest. The ack request carries a random
/// request ID, which is copied from Go's request before comparing. Everything else is compared exactly.
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/session_handler.go"/>.
/// </remarks>
[TestClass]
public class RpcGoldenTests
{
    private static readonly ListConversationsRequest Data = new() { Count = 25 };

    private static readonly string PbLiteEmpty = "[]";

    private static readonly string[] TtlCases = ["customTtl", "omitTtl", "gaia2"];

    // Same fixed account as Go's golden client; the options pick which URL rules apply.
    private static AuthData BuildAuth(bool cookies = true, bool google = true)
    {
        AuthData golden = AuthDataGoldenTests.BuildGolden();
        var auth = new AuthData
        {
            RequestCrypto    = golden.RequestCrypto,
            RefreshKey       = golden.RefreshKey,
            Browser          = golden.Browser,
            Mobile           = golden.Mobile,
            TachyonAuthToken = golden.TachyonAuthToken,
            TachyonTtlMicros = golden.TachyonTtlMicros,
            DestRegId        = google ? golden.DestRegId : Guid.Empty,
        };
        if (cookies)
        {
            auth.Cookies.Set(new Dictionary<string, string> { ["SAPISID"] = "fake-sapisid", ["SID"] = "fake-sid" });
        }

        return auth;
    }

    private static (RpcSession Session, FakeHttpHandler Handler) CreateSession(AuthData auth)
    {
        var time    = new FakeTimeProvider();
        var handler = new FakeHttpHandler { Time = time };
        handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", Encoding.UTF8.GetBytes(PbLiteEmpty));
        var http    = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
        var session = new RpcSession(http, auth, GmEndpoints.Production, time, NullLogger.Instance) { SessionId = "golden-session" };
        return (session, handler);
    }

    private static string GoPbLite(string area, string name) =>
        Golden.Load("rpc").GetProperty(area).GetProperty(name).GetProperty("pblite").GetString()!;

    private static SendParams ParamsFor(string name) => name switch
    {
        "dontEncrypt" => new SendParams(ActionType.ListConversations, Data, "golden-req-1", DontEncrypt: true),
        "encrypted"   => new SendParams(ActionType.ListConversations, Data, "golden-req-2"),
        "customTtl"   => new SendParams(ActionType.GetUpdates, RequestId: "golden-req-3", CustomTtlMicros: 123456789),
        "omitTtl"     => new SendParams(ActionType.GetUpdates, RequestId: "golden-req-4", OmitTtl: true),
        "gaia2"       => new SendParams(ActionType.CreateGaiaPairingClientInit, Data, "golden-req-5", DontEncrypt: true, MessageType: MessageType.Gaia2),
        _             => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>
    /// An unencrypted request with a fixed ID is byte-for-byte what Go builds.
    /// </summary>
    [TestMethod]
    public void Build_DontEncrypt_MatchesGoExactly()
    {
        (RpcSession session, _) = CreateSession(BuildAuth());

        OutgoingRPCMessage built = session.Build(ParamsFor("dontEncrypt"), out string request_id);

        Assert.AreEqual("golden-req-1", request_id);
        Assert.AreEqual(GoPbLite("build", "dontEncrypt"), Encoding.UTF8.GetString(PbLiteSerializer.Serialize(built)));
    }

    /// <summary>
    /// An encrypted request differs from Go's only by the random IV: the decrypted payload and all other fields are equal.
    /// </summary>
    [TestMethod]
    public void Build_Encrypted_SameFieldsAsGo_AndPlaintextMatches()
    {
        AuthData auth = BuildAuth();
        (RpcSession session, _) = CreateSession(auth);

        OutgoingRPCMessage built = session.Build(ParamsFor("encrypted"), out _);
        var go = PbLiteSerializer.Deserialize<OutgoingRPCMessage>(Encoding.UTF8.GetBytes(GoPbLite("build", "encrypted")));

        OutgoingRPCData go_inner = OutgoingRPCData.Parser.ParseFrom(go.Data.MessageData);
        OutgoingRPCData cs_inner = OutgoingRPCData.Parser.ParseFrom(built.Data.MessageData);
        byte[] expected_plain = Data.ToByteArray();
        CollectionAssert.AreEqual(expected_plain, auth.RequestCrypto.Decrypt(go_inner.EncryptedProtoData.Span), "Go plaintext");
        CollectionAssert.AreEqual(expected_plain, auth.RequestCrypto.Decrypt(cs_inner.EncryptedProtoData.Span), "C# plaintext");
        Assert.IsTrue(cs_inner.UnencryptedProtoData.IsEmpty);

        // Normalize The Random Ciphertext, Then Everything Else Must Be Equal
        go_inner.EncryptedProtoData = ByteString.Empty;
        cs_inner.EncryptedProtoData = ByteString.Empty;
        go.Data.MessageData         = go_inner.ToByteString();
        built.Data.MessageData      = cs_inner.ToByteString();
        Assert.AreEqual(go, built);
    }

    /// <summary>
    /// Custom TTL, omitted TTL and the GAIA_2 message type match Go exactly.
    /// </summary>
    [TestMethod]
    public void Build_TtlVariants_MatchGo()
    {
        (RpcSession session, _) = CreateSession(BuildAuth());

        foreach (string name in TtlCases)
        {
            OutgoingRPCMessage built = session.Build(ParamsFor(name), out _);
            Assert.AreEqual(GoPbLite("build", name), Encoding.UTF8.GetString(PbLiteSerializer.Serialize(built)), name);
        }
    }

    /// <summary>
    /// The send URL follows the cookie rule: Google host with cookies (and for non-Google accounts), QR host otherwise.
    /// </summary>
    [TestMethod]
    public async Task SendUrl_MatchesGoWithAndWithoutCookies()
    {
        var go = Golden.Load("rpc").GetProperty("sendUrl");
        var cases = new (string Name, AuthData Auth)[]
        {
            ("withCookies", BuildAuth()),
            ("googleNoCookies", BuildAuth(cookies: false)),
            ("nonGoogle", BuildAuth(cookies: false, google: false)),
        };

        foreach ((string name, AuthData auth) in cases)
        {
            (RpcSession session, FakeHttpHandler handler) = CreateSession(auth);

            await session.SendNoResponseAsync(new SendParams(ActionType.GetUpdates, RequestId: "golden-req-6", DontEncrypt: true), CancellationToken.None);

            Assert.AreEqual(go.GetProperty(name).GetString(), handler.Requests[0].Url, name);
        }
    }

    /// <summary>
    /// Queued acks (a1, a2, a1) are deduplicated and the ack request equals Go's apart from its random request ID.
    /// </summary>
    [TestMethod]
    public async Task Acks_DedupedAndShapedLikeGo()
    {
        var go = Golden.Load("rpc").GetProperty("acks");
        (RpcSession session, FakeHttpHandler handler) = CreateSession(BuildAuth());
        session.QueueAck("a1");
        session.QueueAck("a2");
        session.QueueAck("a1");

        await session.FlushAcksAsync(CancellationToken.None);

        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(go.GetProperty("url").GetString(), handler.Requests[0].Url);
        var go_body = PbLiteSerializer.Deserialize<AckMessageRequest>(Golden.Hex(go.GetProperty("bodyHex")));
        var cs_body = PbLiteSerializer.Deserialize<AckMessageRequest>(handler.Requests[0].Body);
        Assert.AreEqual(2, cs_body.Acks.Count);
        Assert.IsTrue(Guid.TryParse(cs_body.AuthData.RequestID, out _));

        // Random Request ID Copied From Go
        cs_body.AuthData.RequestID = go_body.AuthData.RequestID;
        Assert.AreEqual(go_body, cs_body);

        // Queue Is Empty Afterwards
        await session.FlushAcksAsync(CancellationToken.None);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    /// <summary>
    /// Messages Go encrypted decode to the same bytes and message type as Go's <c>decryptInternalMessage</c>.
    /// </summary>
    [TestMethod]
    public void Decode_GoEncryptedMessages_MatchGo()
    {
        (RpcSession session, _) = CreateSession(BuildAuth());
        JsonElement cases = Golden.Load("rpc").GetProperty("decode");
        Assert.AreEqual(3, cases.GetArrayLength());

        foreach (JsonElement go in cases.EnumerateArray())
        {
            var raw = PbLiteSerializer.Deserialize<IncomingRPCMessage>(Encoding.UTF8.GetBytes(go.GetProperty("pblite").GetString()!));

            IncomingRpc result = session.Decode(raw);

            string action = go.GetProperty("action").GetString()!;
            Assert.IsNotNull(result.Message, action);
            CollectionAssert.AreEqual(Golden.Hex(go.GetProperty("decryptedDataHex")), result.DecryptedData, action);
            Assert.AreEqual(go.GetProperty("messageName").GetString(), result.DecryptedMessage!.Descriptor.FullName, action);
            Assert.AreSame(raw, result.Raw);
        }
    }

    /// <summary>
    /// The action-to-response-type table equals Go's <c>responseType</c> map.
    /// </summary>
    [TestMethod]
    public void ResponseTable_MatchesGo()
    {
        var go = Golden.Load("rpc").GetProperty("responseTable").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

        var actual = RpcSession.ResponseTypes.ToDictionary(
            kv => typeof(ActionType).GetField(kv.Key.ToString())!.GetCustomAttributes(typeof(OriginalNameAttribute), false).Cast<OriginalNameAttribute>().Single().Name,
            kv => kv.Value.ParseFrom([]).Descriptor.FullName);

        CollectionAssert.AreEquivalent(go, actual);
    }
}
