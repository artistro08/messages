using System.Text;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;

namespace GMessages.Protocol.Tests.PbLite;

/// <summary>
/// Checks that malformed PBLite throws a <see cref="PbLiteException"/> that names the field.
/// </summary>
/// <remarks>
/// These checks are C#-only. Go also returns an error here, but the message text is different. Thus no golden vector exists.
/// </remarks>
[TestClass]
public class PbLiteErrorTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// A slot of the wrong JSON type throws, and the message names the field.
    /// </summary>
    [TestMethod]
    public void Deserialize_WrongShape_ThrowsPbLiteException()
    {
        // A nested message slot holds a string.
        var nested = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Conversation>(Utf8("""[null,null,null,"oops"]""")));
        StringAssert.Contains(nested.Message, "conversations.Conversation.latestMessage");

        // An int slot holds a string that is not a number.
        var number = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Device>(Utf8("""["abc"]""")));
        StringAssert.Contains(number.Message, "authentication.Device.userID");

        // A repeated slot holds a scalar.
        var repeated = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Conversation>(Utf8("""[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,5]""")));
        StringAssert.Contains(repeated.Message, "conversations.Conversation.participants");

        // The root is not an array, or the text is not JSON.
        Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Device>(Utf8("""{"1":2}""")));
        Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Device>(Utf8("[1,")));
    }

    /// <summary>
    /// A lone-surrogate escape or an out-of-range number throws a PbLiteException, not a raw .NET exception.
    /// </summary>
    [TestMethod]
    public void Deserialize_BadStringOrNumber_ThrowsPbLiteException()
    {
        // A lone surrogate is in the string field Device.sourceID, then in a base64 field.
        var text = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Device>(Utf8("""[null,"\ud800"]""")));
        StringAssert.Contains(text.Message, "authentication.Device.sourceID");
        var base64 = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<AuthMessage>(Utf8("""[null,null,null,null,null,"\ud800"]""")));
        StringAssert.Contains(base64.Message, "authentication.AuthMessage.tachyonAuthToken");

        // 1e400 is in the double field DoubleValue.value, then in an int field through the double path.
        var number = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Google.Protobuf.WellKnownTypes.DoubleValue>(Utf8("[1e400]")));
        StringAssert.Contains(number.Message, "google.protobuf.DoubleValue.value");
        var integer = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<Device>(Utf8("[1e400]")));
        StringAssert.Contains(integer.Message, "authentication.Device.userID");
    }

    /// <summary>
    /// Invalid base64 in a bytes field throws, and the message names the field.
    /// </summary>
    [TestMethod]
    public void Deserialize_BadBase64_ThrowsPbLiteException()
    {
        var bytes = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<AuthMessage>(Utf8("""[null,null,null,null,null,"!!!not base64"]""")));
        StringAssert.Contains(bytes.Message, "authentication.AuthMessage.tachyonAuthToken");

        // A pblite_binary message holds bad base64.
        var binary = Assert.ThrowsExactly<PbLiteException>(() => PbLiteSerializer.Deserialize<SignInGaiaRequest.Types.Inner>(
            Utf8("[" + string.Join(",", Enumerable.Repeat("null", 35)) + ",\"@@@\"]")));
        StringAssert.Contains(binary.Message, "someData");
    }
}
