using System.Text;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;

namespace GMessages.Protocol.Tests.PbLite;

/// <summary>
/// Checks that malformed PBLite fails with a <see cref="PbLiteException"/> that names the field.
/// </summary>
/// <remarks>
/// C#-only behavior: Go returns an error here too, but the message text differs, so there is no golden vector.
/// </remarks>
[TestClass]
public class PbLiteErrorTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// A slot of the wrong JSON type throws and the message names the field.
    /// </summary>
    [TestMethod]
    public void Deserialize_WrongShape_ThrowsPbLiteException()
    {
        // Nested Message Slot Holding A String
        var nested = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Conversation>(Utf8("""[null,null,null,"oops"]""")));
        StringAssert.Contains(nested.Message, "conversations.Conversation.latestMessage");

        // Int Slot Holding A Non-Number String
        var number = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Device>(Utf8("""["abc"]""")));
        StringAssert.Contains(number.Message, "authentication.Device.userID");

        // Repeated Slot Holding A Scalar
        var repeated = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Conversation>(Utf8("""[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,5]""")));
        StringAssert.Contains(repeated.Message, "conversations.Conversation.participants");

        // Root Is Not An Array, Or Not JSON At All
        Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Device>(Utf8("""{"1":2}""")));
        Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Device>(Utf8("[1,")));
    }

    /// <summary>
    /// A lone-surrogate escape or an out-of-range number throws PbLiteException, not a raw .NET exception.
    /// </summary>
    [TestMethod]
    public void Deserialize_BadStringOrNumber_ThrowsPbLiteException()
    {
        // Lone Surrogate In A String Field (Device.sourceID) And In A Base64 Field
        var text = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Device>(Utf8("""[null,"\ud800"]""")));
        StringAssert.Contains(text.Message, "authentication.Device.sourceID");
        var base64 = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<AuthMessage>(Utf8("""[null,null,null,null,null,"\ud800"]""")));
        StringAssert.Contains(base64.Message, "authentication.AuthMessage.tachyonAuthToken");

        // 1e400 In A Double Field (DoubleValue.value) And In An Int Field Via The Double Path
        var number = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Google.Protobuf.WellKnownTypes.DoubleValue>(Utf8("[1e400]")));
        StringAssert.Contains(number.Message, "google.protobuf.DoubleValue.value");
        var integer = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Device>(Utf8("[1e400]")));
        StringAssert.Contains(integer.Message, "authentication.Device.userID");
    }

    /// <summary>
    /// Invalid base64 in a bytes field throws and the message names the field.
    /// </summary>
    [TestMethod]
    public void Deserialize_BadBase64_ThrowsPbLiteException()
    {
        var bytes = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<AuthMessage>(Utf8("""[null,null,null,null,null,"!!!not base64"]""")));
        StringAssert.Contains(bytes.Message, "authentication.AuthMessage.tachyonAuthToken");

        // pblite_binary Message Holding Bad Base64
        var binary = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<SignInGaiaRequest.Types.Inner>(
            Utf8("[" + string.Join(",", Enumerable.Repeat("null", 35)) + ",\"@@@\"]")));
        StringAssert.Contains(binary.Message, "someData");
    }
}
