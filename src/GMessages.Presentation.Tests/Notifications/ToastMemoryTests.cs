using GMessages.Presentation.Messaging;
using GMessages.Presentation.Notifications;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Tests.Notifications;

[TestClass]
public sealed class ToastMemoryTests
{
    private static ShownToast Toast(string tag, string group = "c1") => new(tag, group, "<toast/>");

    /// <summary>
    /// A waiting snooze survives any number of newer toasts, and once shown again it can be snoozed again.
    /// </summary>
    [TestMethod]
    public void Snooze_SurvivesNewerToasts_AndCanSnoozeAgain()
    {
        var memory = new ToastMemory(3);
        memory.Remember(Toast("m1"));
        ShownToast snoozed = memory.Snooze("m1", "c1")!;

        for (int i = 0; i < 25; i++)
        {
            memory.Remember(Toast("n" + i, "c2"));
        }

        Assert.IsTrue(memory.Wake(snoozed));
        Assert.IsNotNull(memory.Snooze("m1", "c1"), "Shown again, it can be snoozed again.");
    }

    /// <summary>
    /// Only the latest toasts can be snoozed; the oldest drop out past the capacity.
    /// </summary>
    [TestMethod]
    public void Snooze_OldToastPastCapacity_IsNull()
    {
        var memory = new ToastMemory(2);
        memory.Remember(Toast("m1"));
        memory.Remember(Toast("m2"));
        memory.Remember(Toast("m3"));

        Assert.IsNull(memory.Snooze("m1", "c1"));
        Assert.IsNotNull(memory.Snooze("m3", "c1"));
    }

    /// <summary>
    /// Opening the chat or clearing every toast cancels its waiting snoozes; other chats' snoozes stay.
    /// </summary>
    [TestMethod]
    public void OpeningTheChatOrClearing_CancelsTheSnooze()
    {
        var memory = new ToastMemory(5);
        memory.Remember(Toast("m1"));
        memory.Remember(Toast("m2", "c2"));
        ShownToast first  = memory.Snooze("m1", "c1")!;
        ShownToast second = memory.Snooze("m2", "c2")!;

        memory.RemoveConversation("c1");

        Assert.IsFalse(memory.Wake(first));
        Assert.IsTrue(memory.Wake(second), "Another chat's snooze stays.");

        ShownToast again = memory.Snooze("m2", "c2")!;
        memory.Clear();
        Assert.IsFalse(memory.Wake(again));
    }

    /// <summary>
    /// A snoozed toast whose chat is then archived or muted doesn't wake: the gate NotificationService runs on a
    /// conversation change dismisses it, as a read does. A still active, unread chat's snooze stays.
    /// </summary>
    [TestMethod]
    public void LeftTheInboxOrMuted_CancelsTheSnooze()
    {
        var memory = new ToastMemory(5);
        memory.Remember(Toast("m1"));
        memory.Remember(Toast("m2", "c2"));
        memory.Remember(Toast("m3", "c3"));
        ShownToast archived = memory.Snooze("m1", "c1")!;
        ShownToast muted    = memory.Snooze("m2", "c2")!;
        ShownToast active   = memory.Snooze("m3", "c3")!;

        // The Gate NotificationService.OnConversationChanged Runs, With Every Chat Still Unread
        ConversationInfo[] changed =
        [
            Unread("c1") with { Status = ConversationStatus.Archived },
            Unread("c2") with { IsMuted = true },
            Unread("c3"),
        ];
        foreach (ConversationInfo conversation in changed)
        {
            if (NotificationPolicy.ShouldDismissOnLeave(conversation) || NotificationPolicy.ShouldDismissOnRead(conversation, null, false, false))
            {
                memory.RemoveConversation(conversation.Id);
            }
        }

        Assert.IsFalse(memory.Wake(archived));
        Assert.IsFalse(memory.Wake(muted));
        Assert.IsTrue(memory.Wake(active), "A still active, unread chat's snooze stays.");
    }

    private static ConversationInfo Unread(string id) =>
        new(id, "Ann", false, ConversationStatus.Active, true, false, DateTimeOffset.UnixEpoch, "", false, "", [], "me", "m1");

    /// <summary>
    /// The real phone sends several updates per incoming message (status changes, reactions). Even when every one of
    /// them reaches the toast gate as new, the message gets one toast; a snooze wake still shows its own toast again.
    /// </summary>
    [TestMethod]
    public void RepeatedArrivals_SameMessage_ShowOneToast()
    {
        var memory       = new ToastMemory(3);
        var conversation = new ConversationInfo("c1", "Ann", false, ConversationStatus.Active, false, true, DateTimeOffset.UnixEpoch, "", false, "", [], "me", "m1");
        int shows        = 0;

        // The Gate NotificationService.ShowAsync Runs: The Policy, Then The First-Show Check
        for (int i = 0; i < 4; i++)
        {
            var message = new ChatMessage("m1", "", "c1", "p1", DateTimeOffset.UnixEpoch, false, MessageKind.Text, "hi", MessageStatusType.IncomingComplete);
            var e       = new MessageArrivedEventArgs(message, conversation, isOld: false, isNew: true);
            if (NotificationPolicy.ShouldNotify(e, true, null, false, false) && memory.FirstShow(e.Message.Id))
            {
                memory.Remember(Toast("m1"));
                shows++;
            }
        }

        Assert.AreEqual(1, shows);

        // Snoozed And Waiting, Or Its Chat Opened: Still Not Shown Again By An Update
        ShownToast snoozed = memory.Snooze("m1", "c1")!;
        Assert.IsFalse(memory.FirstShow("m1"));
        memory.RemoveConversation("c1");
        Assert.IsFalse(memory.FirstShow("m1"));
        Assert.IsFalse(memory.Wake(snoozed), "Opening the chat cancelled the snooze.");

        // A Snooze Wake Still Shows Its Own Toast
        memory.Remember(Toast("m2"));
        ShownToast second = memory.Snooze("m2", "c1")!;
        Assert.IsTrue(memory.Wake(second));
        Assert.IsTrue(memory.FirstShow("m3"), "Another message still shows.");
    }
}
