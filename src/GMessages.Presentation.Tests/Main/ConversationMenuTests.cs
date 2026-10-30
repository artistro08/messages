using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Tests.Main;

/// <summary>
/// Checks which items <see cref="ConversationMenu"/> offers for each folder, mute state, group and place.
/// </summary>
[TestClass]
public sealed class ConversationMenuTests
{
    [TestMethod]
    [DataRow(ConversationStatus.Active, false, false, false, "Archive,Mute,BlockAndReport,MoveToTrash")]
    [DataRow(ConversationStatus.Active, true, false, true, "Details,Archive,Unmute,BlockAndReport,MoveToTrash")]
    [DataRow(ConversationStatus.Active, false, true, false, "Archive,Mute,MoveToTrash")]
    [DataRow(ConversationStatus.Archived, false, false, false, "Unarchive,Mute,BlockAndReport,MoveToTrash")]
    [DataRow(ConversationStatus.Archived, true, true, true, "Details,Unarchive,Unmute,MoveToTrash")]
    [DataRow(ConversationStatus.KeepArchived, true, false, false, "Unarchive,Unmute,BlockAndReport,MoveToTrash")]
    [DataRow(ConversationStatus.KeepArchived, false, true, true, "Details,Unarchive,Mute,MoveToTrash")]
    [DataRow(ConversationStatus.SpamFolder, false, false, false, "MoveToTrash")]
    [DataRow(ConversationStatus.SpamFolder, true, true, true, "Details,MoveToTrash")]
    [DataRow(ConversationStatus.BlockedFolder, false, false, false, "Unblock,MoveToTrash")]
    [DataRow(ConversationStatus.BlockedFolder, true, true, true, "Details,Unblock,MoveToTrash")]
    public void For_Table(ConversationStatus status, bool muted, bool group, bool inHeader, string expected)
    {
        ConversationInfo c = ConversationInfo.From(MainRig.Conv("a", "Ann", status: status)) with { IsMuted = muted, IsGroup = group };

        Assert.AreEqual(expected, string.Join(",", ConversationMenu.For(c, inHeader)));
    }
}
