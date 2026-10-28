using GMessages.Presentation.Placement;

namespace GMessages.Presentation.Tests.Placement;

/// <summary>
/// Edge detection ported from Magic Buds' FlyoutPlacementTests, plus the tray menu anchor math.
/// </summary>
[TestClass]
public sealed class MenuPlacementTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);

    [TestMethod]
    public void BottomTaskbar_IsDetected_AndAnchorSitsAboveIt()
    {
        var workArea = new PixelRect(0, 0, 1920, 1032);

        Assert.AreEqual(TaskbarEdge.Bottom, MenuPlacement.DetectEdge(Monitor, workArea));
        Assert.AreEqual((1800, 1020, TaskbarEdge.Bottom), MenuPlacement.GetAnchor(1800, 1050, Monitor, workArea, 1.0));
    }

    [TestMethod]
    public void TopTaskbar_IsDetected_AndAnchorSitsBelowIt()
    {
        var workArea = new PixelRect(0, 48, 1920, 1080);

        Assert.AreEqual(TaskbarEdge.Top, MenuPlacement.DetectEdge(Monitor, workArea));
        Assert.AreEqual((1800, 60, TaskbarEdge.Top), MenuPlacement.GetAnchor(1800, 20, Monitor, workArea, 1.0));
    }

    [TestMethod]
    public void LeftTaskbar_IsDetected_AndAnchorSitsRightOfIt()
    {
        var workArea = new PixelRect(48, 0, 1920, 1080);

        Assert.AreEqual(TaskbarEdge.Left, MenuPlacement.DetectEdge(Monitor, workArea));
        Assert.AreEqual((60, 900, TaskbarEdge.Left), MenuPlacement.GetAnchor(20, 900, Monitor, workArea, 1.0));
    }

    [TestMethod]
    public void RightTaskbar_IsDetected_AndAnchorSitsLeftOfIt()
    {
        var workArea = new PixelRect(0, 0, 1872, 1080);

        Assert.AreEqual(TaskbarEdge.Right, MenuPlacement.DetectEdge(Monitor, workArea));
        Assert.AreEqual((1860, 900, TaskbarEdge.Right), MenuPlacement.GetAnchor(1900, 900, Monitor, workArea, 1.0));
    }

    [TestMethod]
    public void AutoHiddenTaskbar_CountsAsBottom() =>
        Assert.AreEqual(TaskbarEdge.Bottom, MenuPlacement.DetectEdge(Monitor, Monitor));

    [TestMethod]
    public void Margin_ScalesWithDpi()
    {
        var monitor  = new PixelRect(0, 0, 2880, 1620);
        var workArea = new PixelRect(0, 0, 2880, 1548);

        // 12 DIP at 150% is 18 px
        Assert.AreEqual((2700, 1530, TaskbarEdge.Bottom), MenuPlacement.GetAnchor(2700, 1600, monitor, workArea, 1.5));
    }

    [TestMethod]
    public void SecondaryMonitor_LeftOfPrimary_UsesItsOwnWorkArea()
    {
        var monitor  = new PixelRect(-1920, 0, 0, 1080);
        var workArea = new PixelRect(-1920, 0, 0, 1032);

        Assert.AreEqual((-100, 1020, TaskbarEdge.Bottom), MenuPlacement.GetAnchor(-100, 1050, monitor, workArea, 1.0));
    }
}
