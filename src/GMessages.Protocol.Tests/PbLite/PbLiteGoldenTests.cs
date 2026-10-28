using System.Text;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using Google.Protobuf;

namespace GMessages.Protocol.Tests.PbLite;

/// <summary>
/// Checks the PBLite serializer against what Go's pblite package writes and reads.
/// </summary>
/// <remarks>
/// Encode cases are real gmproto messages: the exact PBLite string must match, and Go's own output must decode back
/// to an equal message. Decode cases are hand-written lenient inputs; the decoded message must equal Go's result.
/// See <see href="https://github.com/mautrix/go-util/tree/main/pblite"/>.
/// </remarks>
[TestClass]
public class PbLiteGoldenTests
{
    // =========================================================================
    // GOLDEN DATA
    // =========================================================================

    /// <summary>
    /// Encode cases from <c>pblite.json</c>: type, binary protobuf, Go's PBLite text.
    /// </summary>
    public static IEnumerable<object[]> EncodeCases()
    {
        foreach (var c in Golden.Load("pblite").GetProperty("encode").EnumerateArray())
        {
            yield return [c.GetProperty("type").GetString()!, Golden.Hex(c.GetProperty("binaryHex")), c.GetProperty("pblite").GetString()!];
        }
    }

    /// <summary>
    /// Decode cases from <c>pblite.json</c>: type, lenient input, Go's binary result.
    /// </summary>
    public static IEnumerable<object[]> DecodeCases()
    {
        foreach (var c in Golden.Load("pblite").GetProperty("decode").EnumerateArray())
        {
            yield return [c.GetProperty("type").GetString()!, c.GetProperty("input").GetString()!, Golden.Hex(c.GetProperty("binaryHex"))];
        }
    }

    /// <summary>
    /// Gets the parser for a proto full name used in <c>pblite.json</c>.
    /// </summary>
    /// <remarks>
    /// An explicit switch keeps the test AOT safe and fails loudly when the exporter adds a type.
    /// </remarks>
    private static MessageParser ParserFor(string type) => type switch
    {
        "authentication.AuthMessage"                => AuthMessage.Parser,
        "authentication.SignInGaiaRequest"          => SignInGaiaRequest.Parser,
        "authentication.RegisterRefreshRequest"     => RegisterRefreshRequest.Parser,
        "authentication.SignInGaiaResponse.Header"  => SignInGaiaResponse.Types.Header.Parser,
        "authentication.Device"                     => Device.Parser,
        "authentication.ConfigVersion"              => ConfigVersion.Parser,
        "rpc.OutgoingRPCMessage"                    => OutgoingRPCMessage.Parser,
        "client.AckMessageRequest"                  => AckMessageRequest.Parser,
        "client.ReceiveMessagesRequest"             => ReceiveMessagesRequest.Parser,
        "conversations.Conversation"                => Conversation.Parser,
        _                                           => throw new AssertFailedException("unknown type " + type),
    };

    /// <summary>
    /// Runs <see cref="PbLiteSerializer.Deserialize{T}"/> for a proto full name used in <c>pblite.json</c>.
    /// </summary>
    private static IMessage Decode(string type, byte[] json) => type switch
    {
        "authentication.AuthMessage"                => PbLiteSerializer.Deserialize<AuthMessage>(json),
        "authentication.SignInGaiaRequest"          => PbLiteSerializer.Deserialize<SignInGaiaRequest>(json),
        "authentication.RegisterRefreshRequest"     => PbLiteSerializer.Deserialize<RegisterRefreshRequest>(json),
        "authentication.SignInGaiaResponse.Header"  => PbLiteSerializer.Deserialize<SignInGaiaResponse.Types.Header>(json),
        "authentication.Device"                     => PbLiteSerializer.Deserialize<Device>(json),
        "authentication.ConfigVersion"              => PbLiteSerializer.Deserialize<ConfigVersion>(json),
        "rpc.OutgoingRPCMessage"                    => PbLiteSerializer.Deserialize<OutgoingRPCMessage>(json),
        "client.AckMessageRequest"                  => PbLiteSerializer.Deserialize<AckMessageRequest>(json),
        "client.ReceiveMessagesRequest"             => PbLiteSerializer.Deserialize<ReceiveMessagesRequest>(json),
        "conversations.Conversation"                => PbLiteSerializer.Deserialize<Conversation>(json),
        _                                           => throw new AssertFailedException("unknown type " + type),
    };

    // =========================================================================
    // TESTS
    // =========================================================================

    /// <summary>
    /// Serializing the message Go marshalled gives Go's exact PBLite text.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(EncodeCases))]
    public void Serialize_MatchesGoExactly(string type, byte[] binary, string expectedPbLite)
    {
        IMessage message = ParserFor(type).ParseFrom(binary);

        Assert.AreEqual(expectedPbLite, Encoding.UTF8.GetString(PbLiteSerializer.Serialize(message)));
    }

    /// <summary>
    /// Deserializing Go's PBLite text gives a message equal to the one Go marshalled.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(EncodeCases))]
    public void Deserialize_GoOutput_EqualsBinaryMessage(string type, byte[] binary, string pblite)
    {
        IMessage expected = ParserFor(type).ParseFrom(binary);

        Assert.AreEqual(expected, Decode(type, Encoding.UTF8.GetBytes(pblite)));
    }

    /// <summary>
    /// Lenient inputs (strings as ints, bool as 1, override object, nulls, short or long arrays) decode like Go.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(DecodeCases))]
    public void Deserialize_LenientInputs_MatchGo(string type, string input, byte[] expectedBinary)
    {
        IMessage expected = ParserFor(type).ParseFrom(expectedBinary);

        Assert.AreEqual(expected, Decode(type, Encoding.UTF8.GetBytes(input)));
    }

    /// <summary>
    /// C#-only: doubles print the way Go's json.Marshal prints them (Go semantics, no golden because gmproto has no float fields).
    /// </summary>
    [TestMethod]
    [DataRow(0.5, "[0.5]")]
    [DataRow(100.0, "[100]")]
    [DataRow(-2.25, "[-2.25]")]
    [DataRow(123456789.5, "[123456789.5]")]
    [DataRow(1e20, "[100000000000000000000]")]
    [DataRow(1e21, "[1e+21]")]
    [DataRow(1.5e-7, "[1.5e-7]")]
    [DataRow(0.000001, "[0.000001]")]
    public void Serialize_Double_UsesGoNumberFormat(double value, string expected)
    {
        var message = new Google.Protobuf.WellKnownTypes.DoubleValue { Value = value };

        Assert.AreEqual(expected, Encoding.UTF8.GetString(PbLiteSerializer.Serialize(message)));
    }
}
