using GMessages.Protocol.Proto;
using Google.Protobuf;

namespace GMessages.Protocol.Tests;

/// <summary>
/// Checks the generated protobuf types against the Go LibGM golden vectors.
/// </summary>
/// <remarks>
/// See <see href="https://github.com/mautrix/gmessages/tree/main/pkg/libgm/gmproto">gmproto</see>.
/// </remarks>
[TestClass]
public class ProtoTests
{
    /// <summary>
    /// The binary form of <see cref="ConfigVersion"/> matches what Go writes.
    /// </summary>
    [TestMethod]
    public void ConfigVersion_BinaryMatchesGo()
    {
        var version = new ConfigVersion { Year = 2026, Month = 9, Day = 2, V1 = 4, V2 = 6 };
        CollectionAssert.AreEqual(Golden.Hex(Golden.Load("proto").GetProperty("configVersionHex")), version.ToByteArray());
    }

    /// <summary>
    /// The binary form of <see cref="Device"/> matches what Go writes.
    /// </summary>
    [TestMethod]
    public void Device_BinaryMatchesGo()
    {
        var device = new Device { UserID = 1, SourceID = "abc", Network = "GDitto" };
        CollectionAssert.AreEqual(Golden.Hex(Golden.Load("proto").GetProperty("deviceHex")), device.ToByteArray());
    }

    /// <summary>
    /// The <c>pblite_binary</c> field option is readable on <c>OutgoingRPCMessage.dest_registration_ids</c>.
    /// </summary>
    [TestMethod]
    public void PbliteBinaryOption_IsVisibleOnDestRegistrationIds()
    {
        var field = OutgoingRPCMessage.Descriptor.FindFieldByNumber(9);
        Assert.IsTrue(field.GetOptions().GetExtension(PbliteExtensions.PbliteBinary));
    }
}
