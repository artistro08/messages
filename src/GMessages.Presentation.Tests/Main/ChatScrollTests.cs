using GMessages.Presentation.Main;

namespace GMessages.Presentation.Tests.Main;

[TestClass]
public sealed class ChatScrollTests
{
    [TestMethod] public void TooShortToScroll_LoadsOlder() => Assert.IsTrue(ChatScroll.ShouldLoadOlder(0, 0, 500));
    [TestMethod] public void NearTop_LoadsOlder() => Assert.IsTrue(ChatScroll.ShouldLoadOlder(200, 3000, 500));
    [TestMethod] public void AtTop_LoadsOlder() => Assert.IsTrue(ChatScroll.ShouldLoadOlder(0, 3000, 500));
    [TestMethod] public void FarFromTop_DoesNotLoad() => Assert.IsFalse(ChatScroll.ShouldLoadOlder(2500, 3000, 500));
    [TestMethod] public void HalfAScreenDown_DoesNotLoad() => Assert.IsFalse(ChatScroll.ShouldLoadOlder(250, 3000, 500));

    [TestMethod] public void AtBottom_HidesScrollToBottom() => Assert.IsFalse(ChatScroll.ShowScrollToBottom(3000, 3000, 500));
    [TestMethod] public void LessThanAScreenUp_HidesScrollToBottom() => Assert.IsFalse(ChatScroll.ShowScrollToBottom(2600, 3000, 500));
    [TestMethod] public void MoreThanAScreenUp_ShowsScrollToBottom() => Assert.IsTrue(ChatScroll.ShowScrollToBottom(2400, 3000, 500));
    [TestMethod] public void NotMeasuredYet_HidesScrollToBottom() => Assert.IsFalse(ChatScroll.ShowScrollToBottom(0, 0, 0));
}
