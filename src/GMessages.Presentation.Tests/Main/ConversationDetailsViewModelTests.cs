using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Tests.Main;

/// <summary>
/// Checks the Details dialog's people and its Block &amp; report spam rule.
/// </summary>
[TestClass]
public sealed class ConversationDetailsViewModelTests
{
    [TestMethod]
    public void People_SkipMe_NumberFallbacks()
    {
        Conversation proto = MainRig.Conv("g", "Team");
        proto.IsGroupChat  = true;
        proto.Participants.Clear();
        proto.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "me", Number = "+15550000000" }, IsMe = true, FullName = "Me" });
        proto.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "p-bo", Number = "+15550000002" }, FullName = "Bo", FormattedNumber = "(555) 000-0002", AvatarHexColor = "#112233" });
        proto.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "p-x", Number = "+15550000009" } });

        var details = new ConversationDetailsViewModel(ConversationInfo.From(proto));

        // Me Is Left Out; The Phone's Order Is Kept
        Assert.HasCount(2, details.People);

        // A Saved Person: Full Name, Formatted Number, Their Letter, Color And Photo
        Assert.AreEqual(new PersonRow("Bo", "(555) 000-0002", "B", 0xFF112233, "p-bo"), details.People[0]);

        // A Number Only: The Raw Number Twice, No Letter (The Person Icon), The Fallback Gray
        Assert.AreEqual(new PersonRow("+15550000009", "+15550000009", string.Empty, AvatarParts.FallbackColor, "p-x"), details.People[1]);
    }

    [TestMethod]
    [DataRow(ConversationStatus.Active, false, false, true)]
    [DataRow(ConversationStatus.Archived, false, true, true)]
    [DataRow(ConversationStatus.Active, true, false, false)]
    [DataRow(ConversationStatus.SpamFolder, false, false, false)]
    [DataRow(ConversationStatus.BlockedFolder, false, true, false)]
    public void CanBlock_FalseForGroupsAndSpam(ConversationStatus status, bool group, bool muted, bool expected)
    {
        ConversationInfo c = ConversationInfo.From(MainRig.Conv("a", "Ann", status: status)) with { IsGroup = group, IsMuted = muted };

        var details = new ConversationDetailsViewModel(c);

        Assert.AreEqual(expected, details.CanBlock);
        Assert.AreEqual(muted, details.IsMuted);
    }
}
