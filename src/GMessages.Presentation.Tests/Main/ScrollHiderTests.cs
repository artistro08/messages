using GMessages.Presentation.Main;

namespace GMessages.Presentation.Tests.Main;

[TestClass]
public sealed class ScrollHiderTests
{
    /// <summary>
    /// A real scroll down hides it and a real scroll up shows it, but small back-and-forth nudges (a touchpad's jitter,
    /// the list re-measuring rows mid-scroll) never flip it.
    /// </summary>
    [TestMethod]
    public void HidesOnScrollDown_ShowsOnScrollUp_IgnoresSmallNudges()
    {
        var hider = new ScrollHider(24);

        Assert.IsTrue(hider.IsShown);
        Assert.IsFalse(hider.Scrolled(10), "10 px down is a nudge.");
        Assert.IsTrue(hider.IsShown);

        Assert.IsTrue(hider.Scrolled(40), "40 px down hides.");
        Assert.IsFalse(hider.IsShown);

        // Jitter While Scrolling Down: A Few Pixels Back Up Keeps It Hidden, And Down Moves The Mark Along
        Assert.IsFalse(hider.Scrolled(36));
        Assert.IsFalse(hider.Scrolled(120));
        Assert.IsFalse(hider.Scrolled(110));
        Assert.IsFalse(hider.IsShown);

        Assert.IsTrue(hider.Scrolled(90), "30 px up from the lowest point shows.");
        Assert.IsTrue(hider.IsShown);

        Assert.IsFalse(hider.Scrolled(100), "10 px down again is a nudge.");
        Assert.IsTrue(hider.Scrolled(200));
        Assert.IsTrue(hider.Scrolled(0), "The top always shows it.");
        Assert.IsTrue(hider.IsShown);
    }
}
