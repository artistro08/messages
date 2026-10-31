using GMessages.Presentation.Settings;

namespace GMessages.Presentation.Tests.Settings;

/// <summary>
/// Checks <see cref="LaunchWindow"/>: the user's launch shows the window, the startup task and toast clicks don't.
/// </summary>
[TestClass]
public sealed class LaunchWindowTests
{
    [TestMethod]
    [DataRow(false, false, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    public void ShowsAtLaunch_OnlyForTheUsersLaunch(bool byStartupTask, bool byNotification, bool expected) =>
        Assert.AreEqual(expected, LaunchWindow.ShowsAtLaunch(byStartupTask, byNotification));
}
