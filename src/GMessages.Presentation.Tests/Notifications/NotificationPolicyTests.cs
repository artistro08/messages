using GMessages.Presentation.Messaging;
using GMessages.Presentation.Notifications;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Tests.Notifications;

[TestClass]
public sealed class NotificationPolicyTests
{
    private static ConversationInfo Conv(ConversationStatus status = ConversationStatus.Active, bool group = false, params ParticipantInfo[] people) =>
        new("c1", "Ann", group, status, false, false, DateTimeOffset.UnixEpoch, "", false, "", people, "me", "m1");

    private static ChatMessage Msg(bool fromMe = false, MessageKind kind = MessageKind.Text, string text = "hi", string sender = "p1") =>
        new("m1", "", "c1", sender, DateTimeOffset.UnixEpoch, fromMe, kind, text, MessageStatusType.StatusUnknown);

    private static bool Run(ChatMessage? m = null, ConversationInfo? c = null, bool nullConv = false, bool isOld = false, bool isNew = true,
        bool enabled = true, string? viewed = null, bool focused = false, bool notifyViewed = false) =>
        NotificationPolicy.ShouldNotify(new MessageArrivedEventArgs(m ?? Msg(), nullConv ? null : c ?? Conv(), isOld, isNew), enabled, viewed, focused, notifyViewed);

    [TestMethod] public void LiveIncomingInMainView_Notifies() => Assert.IsTrue(Run());
    [TestMethod] public void NotificationsOff_DoesNotNotify() => Assert.IsFalse(Run(enabled: false));
    [TestMethod] public void Old_DoesNotNotify() => Assert.IsFalse(Run(isOld: true));
    [TestMethod] public void NotNew_DoesNotNotify() => Assert.IsFalse(Run(isNew: false));
    [TestMethod] public void FromMe_DoesNotNotify() => Assert.IsFalse(Run(m: Msg(fromMe: true)));
    [TestMethod] public void Tombstone_DoesNotNotify() => Assert.IsFalse(Run(m: Msg(kind: MessageKind.Tombstone)));
    [TestMethod] public void NullConversation_DoesNotNotify() => Assert.IsFalse(Run(nullConv: true));
    [TestMethod] public void NotMainView_DoesNotNotify() => Assert.IsFalse(Run(c: Conv(ConversationStatus.Archived)));
    [TestMethod] public void ViewedAndFocused_DoesNotNotify() => Assert.IsFalse(Run(viewed: "c1", focused: true));
    [TestMethod] public void ViewedAndFocused_NotifyForViewedOn_Notifies() => Assert.IsTrue(Run(viewed: "c1", focused: true, notifyViewed: true));
    [TestMethod] public void ViewedButUnfocused_Notifies() => Assert.IsTrue(Run(viewed: "c1", focused: false));

    [TestMethod]
    public void Compose_Group_PrefixesSenderName()
    {
        var c = Conv(group: true, people: [new ParticipantInfo("p1", "Bob Lee", "Bob", "", false, "+1 555")]);

        Assert.AreEqual("Bob Lee: hey", NotificationContent.Compose(Msg(text: "hey"), c).Body);
    }

    [TestMethod]
    public void Compose_Group_FallsBackToNumber()
    {
        var c = Conv(group: true, people: [new ParticipantInfo("p1", "", "", "", false, "+1 555")]);

        Assert.AreEqual("+1 555: hey", NotificationContent.Compose(Msg(text: "hey"), c).Body);
    }

    [TestMethod]
    public void Compose_OneToOne_BodyIsText()
    {
        var n = NotificationContent.Compose(Msg(text: "hey"), Conv());

        Assert.AreEqual("hey", n.Body);
        Assert.AreEqual("Ann", n.Title);
        Assert.AreEqual("m1", n.Tag);
        Assert.AreEqual("c1", n.Group);
        Assert.AreEqual("p1", n.AvatarParticipantId);
    }

    [TestMethod]
    public void Compose_Photo_UsesLabel() => Assert.AreEqual("Photo", NotificationContent.Compose(Msg(kind: MessageKind.Photo, text: ""), Conv()).Body);
}
