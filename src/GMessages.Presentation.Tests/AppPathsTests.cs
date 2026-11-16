namespace GMessages.Presentation.Tests;

[TestClass]
public sealed class AppPathsTests
{
    [TestMethod]
    public void TestDataDirWinsOverEverything()
    {
        var paths = AppPaths.Resolve(@"C:\Test\Data");

        Assert.AreEqual(@"C:\Test\Data", paths.DataFolder);
        Assert.AreEqual(Path.Combine(@"C:\Test\Data", "session.bin"), paths.SessionFile);
        Assert.AreEqual(Path.Combine(@"C:\Test\Data", "settings.json"), paths.SettingsFile);
        Assert.AreEqual(Path.Combine(@"C:\Test\Data", "Logs"), paths.LogsFolder);
    }

    [TestMethod]
    public void UnpackagedUsesLocalAppDataWithTheDisplayName()
    {
        var paths = AppPaths.Resolve(null);

#if DEBUG
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.DisplayName + ".Debug");
#else
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.DisplayName);
#endif
        Assert.AreEqual(expected, paths.DataFolder);
        Assert.IsFalse(paths.DataFolder.EndsWith("GMessages.Dev", StringComparison.OrdinalIgnoreCase));
    }

}
