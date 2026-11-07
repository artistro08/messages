using System.Net;
using System.Text;
using System.Text.Json;
using GMessages.Protocol.Http;
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
/// Checks <see cref="PairingEmojis"/>, <see cref="PairingSession"/> and the requests of <see cref="GaiaPairing"/>
/// against Go's <c>pair_google.go</c>, using <c>pairing.json</c>.
/// </summary>
/// <remarks>
/// Some fields are random. The test copies them from Go: the 32 random bytes of the client init, and the request IDs
/// of the client init, client finished and unpair requests. The unpair payload uses a random IV. The test
/// decrypts both payloads with the golden key and compares them. It then copies the Go ciphertext over.
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go"/>.
/// </remarks>
[TestClass]
public class PairingGoldenTests
{
    private static readonly JsonElement Go = Golden.Load("pairing");

    private static PairingSession GoSession()
    {
        JsonElement s = Go.GetProperty("session");
        var dest      = new PrimaryDevice(s.GetProperty("destRegId").GetString()!, s.GetProperty("destUnknownInt").GetUInt64(), s.GetProperty("destUnknownTs").GetUInt64(), default);
        return PairingSession.FromPrivateKey(
            Guid.Parse(s.GetProperty("uuid").GetString()!),
            DateTimeOffset.FromUnixTimeMilliseconds(s.GetProperty("startUnixMilli").GetInt64()),
            Golden.Hex(s.GetProperty("clientScalarHex")),
            dest);
    }

    private static PairingSession SessionWithGoInit()
    {
        PairingSession session = GoSession();
        session.PreparePayloads();
        session.InitPayload = Golden.Hex(Go.GetProperty("session").GetProperty("initPayloadHex"));
        return session;
    }

    private static GaiaPairingResponseContainer Container(JsonElement c) => GaiaPairingResponseContainer.Parser.ParseFrom(Golden.Hex(c.GetProperty("containerHex")));

    /// <summary>
    /// Both emoji lists equal the Go lists, item by item, in an ordinal comparison. The lists include ZWJ sequences and emoji without a variation selector.
    /// </summary>
    [TestMethod]
    public void EmojiLists_EqualGo_OrdinalPerItem()
    {
        foreach ((string name, IReadOnlyList<string> list) in new[] { ("emojisV0", PairingEmojis.V0), ("emojisV1", PairingEmojis.V1) })
        {
            string[] go = [.. Go.GetProperty(name).EnumerateArray().Select(e => e.GetString()!)];
            Assert.AreEqual(go.Length, list.Count, name);
            for (int i = 0; i < go.Length; i++)
            {
                Assert.IsTrue(string.Equals(go[i], list[i], StringComparison.Ordinal), $"{name}[{i}]: Go {go[i]}, C# {list[i]}");
            }
        }
    }

    /// <summary>
    /// The client finished payload is deterministic and equals the Go bytes.
    /// </summary>
    [TestMethod]
    public void FinishPayload_EqualsGoBytes()
    {
        PairingSession session = GoSession();

        (_, byte[] finish) = session.PreparePayloads();

        Assert.AreEqual(Go.GetProperty("session").GetProperty("finishPayloadHex").GetString(), Convert.ToHexStringLower(finish));
        CollectionAssert.AreEqual(finish, session.FinishPayload);
    }

    /// <summary>
    /// The client init payload equals the Go payload after the test copies in the 32 random bytes from Go.
    /// </summary>
    [TestMethod]
    public void InitPayload_EqualsGo_ExceptRandom()
    {
        JsonElement s          = Go.GetProperty("session");
        PairingSession session = GoSession();

        (byte[] init, _) = session.PreparePayloads();

        var message     = Ukey2Message.Parser.ParseFrom(init);
        var clientInit  = Ukey2ClientInit.Parser.ParseFrom(message.MessageData);
        Assert.AreEqual(32, clientInit.Random.Length);
        clientInit.Random   = ByteString.CopyFrom(Golden.Hex(s.GetProperty("initRandomHex")));
        message.MessageData = clientInit.ToByteString();
        Assert.AreEqual(s.GetProperty("initPayloadHex").GetString(), Convert.ToHexStringLower(message.ToByteArray()));
        CollectionAssert.AreEqual(init, session.InitPayload);
    }

    /// <summary>
    /// For all four version combinations, the emoji and the next key match Go.
    /// </summary>
    [TestMethod]
    public void ProcessServerInit_EmojiAndNextKey_MatchGo_AllFourCases()
    {
        foreach (JsonElement c in Go.GetProperty("serverInit").EnumerateArray())
        {
            PairingSession session = SessionWithGoInit();

            string emoji = session.ProcessServerInit(Container(c));

            Assert.IsTrue(string.Equals(c.GetProperty("emoji").GetString(), emoji, StringComparison.Ordinal), $"emoji {emoji}");
            Assert.AreEqual(c.GetProperty("nextKeyHex").GetString(), Convert.ToHexStringLower(session.NextKey));
            Assert.AreEqual(c.GetProperty("keyDerivationVersion").GetInt32(), session.KeyDerivationVersion);
        }
    }

    /// <summary>
    /// For all four version combinations, the request keys match the keys that the Go FinishGaiaPairing stored.
    /// </summary>
    [TestMethod]
    public void DeriveRequestKeys_MatchGo_AllFourCases()
    {
        foreach (JsonElement c in Go.GetProperty("serverInit").EnumerateArray())
        {
            PairingSession session = SessionWithGoInit();
            session.ProcessServerInit(Container(c));

            (byte[] aes, byte[] hmac) = session.DeriveRequestKeys();

            Assert.AreEqual(c.GetProperty("aesKeyHex").GetString(), Convert.ToHexStringLower(aes));
            Assert.AreEqual(c.GetProperty("hmacKeyHex").GetString(), Convert.ToHexStringLower(hmac));
        }
    }

    /// <summary>
    /// For all four version combinations, FinishAsync stores the keys, the pairing ID and the phone ID. It returns the
    /// phone ID that the Go FinishGaiaPairing returned.
    /// </summary>
    [TestMethod]
    public async Task FinishAsync_StoresKeysAndIds_MatchGo_AllFourCases()
    {
        foreach (JsonElement c in Go.GetProperty("serverInit").EnumerateArray())
        {
            // The fake phone accepts the finish with an empty answer, as in the Go export.
            AuthData auth   = AuthDataGoldenTests.BuildGolden();
            var time        = new SignalingTimeProvider();
            var handler     = new FakeHttpHandler { Time = time };
            var http        = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
            var rpc         = new RpcSession(http, auth, GmEndpoints.Production, time, NullLogger.Instance);
            handler.Respond = (_, req) =>
            {
                GaiaPairingTests.Deliver(rpc, req, []);
                return FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", "[]"u8.ToArray());
            };
            (GaiaPairing pairing, _) = GaiaPairingTests.CreatePairing(http, rpc, auth, time);
            PairingSession session   = SessionWithGoInit();
            session.ProcessServerInit(Container(c));

            string phoneId = await pairing.FinishAsync(session, CancellationToken.None);

            Assert.AreEqual(c.GetProperty("phoneId").GetString(), phoneId);
            Assert.AreEqual(Guid.Parse(c.GetProperty("pairingId").GetString()!), auth.PairingId);
            Assert.AreEqual(c.GetProperty("destPhoneId").GetUInt64(), auth.DestPhoneId);
            Assert.AreEqual(c.GetProperty("aesKeyHex").GetString(), Convert.ToHexStringLower(auth.RequestCrypto.AesKey));
            Assert.AreEqual(c.GetProperty("hmacKeyHex").GetString(), Convert.ToHexStringLower(auth.RequestCrypto.HmacKey));
        }
    }

    /// <summary>
    /// Every invalid server init fails with the Go error text.
    /// </summary>
    [TestMethod]
    public void ProcessServerInit_InvalidInputs_FailLikeGo()
    {
        foreach (JsonProperty c in Go.GetProperty("serverInitErrors").EnumerateObject())
        {
            PairingSession session = SessionWithGoInit();

            var ex = Assert.ThrowsExactly<InvalidDataException>(() => session.ProcessServerInit(Container(c.Value)), c.Name);

            Assert.AreEqual(c.Value.GetProperty("error").GetString(), ex.Message, c.Name);
        }
    }

    /// <summary>
    /// The client init, client finished, cancel and unpair requests match the Go requests.
    /// </summary>
    [TestMethod]
    public async Task PairingRequests_MatchGo()
    {
        JsonElement go = Go.GetProperty("requests");

        // Every send gets the Go version-2 server init back, as in the Go export.
        AuthData auth   = AuthDataGoldenTests.BuildGolden();
        var time        = new SignalingTimeProvider();
        var handler     = new FakeHttpHandler { Time = time };
        var http        = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
        var rpc         = new RpcSession(http, auth, GmEndpoints.Production, time, NullLogger.Instance);
        byte[] response = Golden.Hex(Go.GetProperty("serverInitErrors").GetProperty("version2").GetProperty("containerHex"));
        handler.Respond = (_, req) =>
        {
            GaiaPairingTests.Deliver(rpc, req, response);
            return FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", "[]"u8.ToArray());
        };
        (GaiaPairing pairing, _) = GaiaPairingTests.CreatePairing(http, rpc, auth, time);

        PairingSession session = SessionWithGoInit();
        await pairing.SendGaiaPairingMessageAsync(session, ActionType.CreateGaiaPairingClientInit, session.InitPayload, CancellationToken.None);
        await pairing.SendGaiaPairingMessageAsync(session, ActionType.CreateGaiaPairingClientFinished, session.FinishPayload, CancellationToken.None);
        await pairing.CancelAsync(session, CancellationToken.None);
        auth.PairingId = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10");
        await pairing.UnpairAsync(CancellationToken.None);

        string[] names = ["clientInit", "clientFinished", "cancel", "unpair"];
        Assert.AreEqual(names.Length, handler.Requests.Count);
        for (int i = 0; i < names.Length; i++)
        {
            JsonElement g = go.GetProperty(names[i]);
            var goMsg     = PbLiteSerializer.Deserialize<OutgoingRPCMessage>(Encoding.UTF8.GetBytes(g.GetProperty("bodyText").GetString()!));
            var csMsg     = PbLiteSerializer.Deserialize<OutgoingRPCMessage>(handler.Requests[i].Body);
            var goData    = OutgoingRPCData.Parser.ParseFrom(goMsg.Data.MessageData);
            var csData    = OutgoingRPCData.Parser.ParseFrom(csMsg.Data.MessageData);

            // The cancel request uses the fixed session UUID, so only the other requests have random IDs.
            if (names[i] != "cancel")
            {
                Assert.IsTrue(Guid.TryParse(csData.RequestID, out _), names[i]);
                csData.RequestID     = goData.RequestID;
                csMsg.Data.RequestID = goMsg.Data.RequestID;
                csMsg.Auth.RequestID = goMsg.Auth.RequestID;
            }

            // The IV is random, so compare the plaintexts and copy the Go ciphertext.
            if (goData.EncryptedProtoData.Length > 0)
            {
                CollectionAssert.AreEqual(auth.RequestCrypto.Decrypt(goData.EncryptedProtoData.Span), auth.RequestCrypto.Decrypt(csData.EncryptedProtoData.Span), names[i]);
                csData.EncryptedProtoData = goData.EncryptedProtoData;
            }

            csMsg.Data.MessageData = csData.ToByteString();
            Assert.AreEqual(g.GetProperty("bodyText").GetString(), Encoding.UTF8.GetString(PbLiteSerializer.Serialize(csMsg)), names[i]);
            Assert.AreEqual(g.GetProperty("url").GetString(), handler.Requests[i].Url, names[i]);
        }
    }

    /// <summary>
    /// Every finish error code maps to the same failure and message as in the Go FinishGaiaPairing.
    /// </summary>
    [TestMethod]
    public void FinishErrorCodes_MapLikeGo()
    {
        foreach (JsonElement f in Go.GetProperty("finishErrors").EnumerateArray())
        {
            var response = new GaiaPairingResponseContainer { FinishErrorType = 1, FinishErrorCode = (GaiaPairingErrorCode)f.GetProperty("code").GetInt32() };

            PairingException ex = GaiaPairing.MapFinishError(response);

            string code = f.GetProperty("codeName").GetString()!;
            Assert.AreEqual(Enum.Parse<PairingFailure>(f.GetProperty("failure").GetString()!), ex.Failure, code);
            // The owner chose different wording for the two phone-cancel messages than the Go text.
            string expected = f.GetProperty("error").GetString()!.Replace("user cancelled pairing on phone", GaiaPairing.CanceledOnPhoneMessage, StringComparison.Ordinal);
            Assert.AreEqual(expected, ex.Message, code);
        }
    }
}
