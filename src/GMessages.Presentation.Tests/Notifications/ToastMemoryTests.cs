using GMessages.Presentation.Notifications;

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
}
