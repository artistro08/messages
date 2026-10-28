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
/// Normalized fields: the 32 random bytes of the client init (copied from Go), and the random request IDs of the
/// client init, client finished and unpair requests (copied from Go). The unpair payload is encrypted with a random
/// IV, so both sides are decrypted with the golden key and compared, then Go's ciphertext is copied over.
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
    /// Both emoji lists equal Go's, item by item, compared ordinally (ZWJ sequences and missing variation selectors included).
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
    /// The client finished payload is deterministic and equals Go's bytes.
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
    /// The client init payload equals Go's once Go's 32 random bytes are copied in.
    /// </summary>
    [TestMethod]
    public void InitPayload_EqualsGo_ExceptRandom()
    {
        JsonElement s          = Go.GetProperty("session");
        PairingSession session = GoSession();

        (byte[] init, _) = session.PreparePayloads();

        var message     = Ukey2Message.Parser.ParseFrom(init);
        var client_init = Ukey2ClientInit.Parser.ParseFrom(message.MessageData);
        Assert.AreEqual(32, client_init.Random.Length);
        client_init.Random  = ByteString.CopyFrom(Golden.Hex(s.GetProperty("initRandomHex")));
        message.MessageData = client_init.ToByteString();
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
    /// For all four version combinations, the request keys match the ones Go's FinishGaiaPairing stored.
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
    /// Every invalid server init fails with Go's error text.
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
    /// The client init, client finished, cancel and unpair requests match Go's.
    /// </summary>
    [TestMethod]
    public async Task PairingRequests_MatchGo()
    {
        JsonElement go = Go.GetProperty("requests");

        // Rig: Every Send Gets Go's Version-2 Server Init Back, Like The Export
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

        // Send Like The Export
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
            var go_msg    = PbLiteSerializer.Deserialize<OutgoingRPCMessage>(Encoding.UTF8.GetBytes(g.GetProperty("bodyText").GetString()!));
            var cs_msg    = PbLiteSerializer.Deserialize<OutgoingRPCMessage>(handler.Requests[i].Body);
            var go_data   = OutgoingRPCData.Parser.ParseFrom(go_msg.Data.MessageData);
            var cs_data   = OutgoingRPCData.Parser.ParseFrom(cs_msg.Data.MessageData);

            // Normalize Random Request IDs (Cancel Uses The Fixed Session UUID)
            if (names[i] != "cancel")
            {
                Assert.IsTrue(Guid.TryParse(cs_data.RequestID, out _), names[i]);
                cs_data.RequestID     = go_data.RequestID;
                cs_msg.Data.RequestID = go_msg.Data.RequestID;
                cs_msg.Auth.RequestID = go_msg.Auth.RequestID;
            }

            // Normalize The Random IV: Plaintexts Must Match
            if (go_data.EncryptedProtoData.Length > 0)
            {
                CollectionAssert.AreEqual(auth.RequestCrypto.Decrypt(go_data.EncryptedProtoData.Span), auth.RequestCrypto.Decrypt(cs_data.EncryptedProtoData.Span), names[i]);
                cs_data.EncryptedProtoData = go_data.EncryptedProtoData;
            }

            cs_msg.Data.MessageData = cs_data.ToByteString();
            Assert.AreEqual(g.GetProperty("bodyText").GetString(), Encoding.UTF8.GetString(PbLiteSerializer.Serialize(cs_msg)), names[i]);
            Assert.AreEqual(g.GetProperty("url").GetString(), handler.Requests[i].Url, names[i]);
        }
    }

    /// <summary>
    /// Every finish error code maps to the same failure and message as Go's FinishGaiaPairing.
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
            Assert.AreEqual(f.GetProperty("error").GetString(), ex.Message, code);
        }
    }
}
