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
    public void People_SkipMeAndHidden_NumberFallbacks()
    {
        Conversation proto = MainRig.Conv("g", "Team");
        proto.IsGroupChat  = true;
        proto.Participants.Clear();
        proto.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "me", Number = "+15550000000" }, IsMe = true, IsVisible = true, FullName = "Me" });
        proto.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "p-bo", Number = "+15550000002" }, IsVisible = true, FullName = "Bo", FormattedNumber = "(555) 000-0002", AvatarHexColor = "#112233" });
        proto.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "p-x", Number = "+15550000009" }, IsVisible = true });

        // The phone lists some people but never shows them: an old copy of me, an unknown sender and someone who left.
        proto.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "me-old", Number = "+15550000000" }, FullName = "Me", FormattedNumber = "(555) 000-0000" });
        proto.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "unknown", Number = "UNKNOWN_SENDER" }, FormattedNumber = "Unknown sender" });
        proto.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "p-left", Number = "+15550000005" }, FullName = "Left" });

        var details = new ConversationDetailsViewModel(ConversationInfo.From(proto));

        Assert.HasCount(2, details.People);

        Assert.AreEqual(new PersonRow("Bo", "(555) 000-0002", "B", 0xFF112233, "p-bo"), details.People[0]);

        // A person with a number only shows the raw number once, as the name, with no second line.
        // The row has no letter, so it shows the person icon, and it takes the fallback gray.
        Assert.AreEqual(new PersonRow("+15550000009", string.Empty, string.Empty, AvatarParts.FallbackColor, "p-x"), details.People[1]);
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

    [TestMethod]
    [DataRow(ConversationStatus.Active, false, true)]
    [DataRow(ConversationStatus.Archived, true, true)]
    [DataRow(ConversationStatus.KeepArchived, false, true)]
    [DataRow(ConversationStatus.SpamFolder, false, false)]
    [DataRow(ConversationStatus.BlockedFolder, true, false)]
    public void CanMute_FalseForSpamAndBlocked(ConversationStatus status, bool muted, bool expected)
    {
        ConversationInfo c = ConversationInfo.From(MainRig.Conv("a", "Ann", status: status)) with { IsMuted = muted };

        Assert.AreEqual(expected, new ConversationDetailsViewModel(c).CanMute);
    }
}
