namespace GMessages.Presentation.Tests;

[TestClass]
public sealed class AppPathsTests
{
    [TestMethod]
    public void TestDataDirWinsOverEverything()
    {
        var paths = AppPaths.Resolve(@"C:\Packaged\LocalState", @"C:\Test\Data");

        Assert.AreEqual(@"C:\Test\Data", paths.DataFolder);
        Assert.AreEqual(Path.Combine(@"C:\Test\Data", "session.bin"), paths.SessionFile);
        Assert.AreEqual(Path.Combine(@"C:\Test\Data", "settings.json"), paths.SettingsFile);
        Assert.AreEqual(Path.Combine(@"C:\Test\Data", "Logs"), paths.LogsFolder);
    }

    [TestMethod]
    public void PackagedUsesTheLocalFolder()
    {
        var paths = AppPaths.Resolve(@"C:\Packaged\LocalState", null);

        Assert.AreEqual(@"C:\Packaged\LocalState", paths.DataFolder);
    }

    [TestMethod]
    public void UnpackagedUsesLocalAppDataWithTheDisplayName()
    {
        var paths = AppPaths.Resolve(null, null);

        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.DisplayName);
        Assert.AreEqual(expected, paths.DataFolder);
        Assert.IsFalse(paths.DataFolder.EndsWith("GMessages.Dev", StringComparison.OrdinalIgnoreCase));
    }
}
