using GMessages.Presentation.Main;

namespace GMessages.Presentation.Tests.Main;

[TestClass]
public sealed class ScrollEdgesTests
{
    [TestMethod] public void NotScrollable_FadesNeitherEdge() => Assert.AreEqual((false, false), ScrollEdges.Fades(0, 0));
    [TestMethod] public void AtTop_FadesOnlyBottom() => Assert.AreEqual((false, true), ScrollEdges.Fades(0, 1000));
    [TestMethod] public void AtBottom_FadesOnlyTop() => Assert.AreEqual((true, false), ScrollEdges.Fades(1000, 1000));
    [TestMethod] public void InTheMiddle_FadesBothEdges() => Assert.AreEqual((true, true), ScrollEdges.Fades(500, 1000));

    // A scaled display can leave the offset a fraction of a pixel off either end.
    [TestMethod] public void WithinAPixelOfTop_FadesOnlyBottom() => Assert.AreEqual((false, true), ScrollEdges.Fades(0.4, 1000));
    [TestMethod] public void WithinAPixelOfBottom_FadesOnlyTop() => Assert.AreEqual((true, false), ScrollEdges.Fades(999.6, 1000));
    [TestMethod] public void ScrollableByAFraction_FadesNeitherEdge() => Assert.AreEqual((false, false), ScrollEdges.Fades(0.3, 0.6));
}
